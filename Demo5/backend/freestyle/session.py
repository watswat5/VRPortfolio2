"""One battle's AI services over one WebSocket: audio in; transcript, metrics, ratings and the opponent's verse out.

The game itself (rounds, turns, crowd, opponent mood, score) lives in the client: it says when the user's bars
start (`listen`) and end (`listen_end`), keeps the server up to date with the room (`context`, which the rater
reads), and asks for the comeback (`verse`). The server never decides anything about the game; it holds only
what the AI needs: the audio, the transcript, and what each side said last.

The receive loop only buffers audio, so slow work (Whisper, Jev, the LLM) never stalls the stream. While the
user raps, three loops run: transcription, metrics (2 Hz), and the chunker, which cuts the live transcript into
chunks (every few words, or at a pause) and has each one rated as soon as it is cut, so the client can react
mid-verse.
"""
from __future__ import annotations

import asyncio
import logging
import time
import uuid
from collections import OrderedDict
from dataclasses import dataclass
from typing import Awaitable, Callable

import numpy as np

from . import hints, interview, lipsync, lyrics, mcs, metrics, opponent as opponent_mod, rater as rater_mod, transcribe
from .audio import FeatureTrack, pcm16_to_float
from .config import Settings
from .metrics import BeatGrid
from .rater import CrowdRating, HeuristicRater, OpponentRating, RatingInput
from .transcribe import Word

log = logging.getLogger("freestyle.session")


class ClipStore:
    """Rendered opponent audio, served at /clip/<key> until it ages out."""

    def __init__(self, keep: int = 256):
        self.keep = keep
        self._clips: OrderedDict[str, bytes] = OrderedDict()

    def put(self, wav: bytes) -> str:
        key = uuid.uuid4().hex
        self._clips[key] = wav
        while len(self._clips) > self.keep:
            self._clips.popitem(last=False)
        return key

    def get(self, key: str) -> bytes | None:
        return self._clips.get(key)


Send = Callable[[dict], Awaitable[None]]

CHUNK_WORDS = 6          # rate after this many new words even without a pause
PAUSE_S = 0.35           # a gap this long ends a chunk (if it has >= MIN_CHUNK_WORDS)
MIN_CHUNK_WORDS = 2
SILENCE_CHUNK_S = 2.5    # this much silence with no words gets rated as a stall
RATER_TIMEOUT_S = 4.0
MAX_TURN_S = 300.0       # a turn the client never ends stops listening here
LINE_TIMEOUT_S = 15.0    # one verse line from the voice; past this the client voices the rest itself
DIMS = ("intimidated", "entertained", "confident", "annoyed", "impressed")


@dataclass
class BattleConfig:
    client_sample_rate: int = 16_000
    assist: bool = False         # rhyme hints and word drops


def _resample(x: np.ndarray, src: int, dst: int) -> np.ndarray:
    if src == dst or not len(x):
        return x
    n = int(round(len(x) * dst / src))
    return np.interp(np.linspace(0, len(x) - 1, n), np.arange(len(x)), x).astype(np.float32)


def _num(v, lo: float, hi: float, default: float) -> float:
    """A number from the client, clamped; anything else is the default."""
    try:
        x = float(v)
    except (TypeError, ValueError):
        return default
    return default if x != x else max(lo, min(hi, x))


def _dims(d, fallback: dict[str, float]) -> dict[str, float]:
    d = d if isinstance(d, dict) else {}
    return {k: _num(d.get(k), 0, 10, fallback.get(k, 5.0)) for k in DIMS}


def _strings(v, n: int = 10, size: int = 300) -> list[str]:
    return [str(s)[:size] for s in v[:n]] if isinstance(v, list) else []


def _outfit(v) -> list[str]:
    """What the user wears, from the client: short plain descriptions ("a red baseball cap"), one per accessory.
    Each is one line of at most 80 characters (it goes into the opponent's prompt), at most 8 of them."""
    out = []
    for s in _strings(v, 8, 200):
        s = " ".join("".join(c if c.isprintable() else " " for c in s).split())[:80].strip()
        if s:
            out.append(s)
    return out


class Session:
    def __init__(self, settings: Settings, send: Send, voice=None, clips: ClipStore | None = None):
        """voice: a loaded server voice (or None: the client voices the opponent itself)."""
        self.s = settings
        self.send = send
        self.voice = voice
        self.clips = clips if clips is not None else ClipStore()
        self.sr = settings.sample_rate
        self.track = FeatureTrack(self.sr)
        self.transcriber = None
        self.rater = rater_mod.make(settings)
        self.fallback = HeuristicRater()
        self.opponent = opponent_mod.make(settings)
        self.mc = mcs.get(None)
        self.outfit: list[str] = []
        self.profile = ""   # the player's pre-show interview, summed up (start.profile)
        self.want_mouth = True   # mouth cues with each voiced line (start.mouth: false turns them off)
        self.cfg = BattleConfig()
        self.beat: BeatGrid | None = None
        self.words: list[Word] = []
        self.partial: list[Word] = []
        self.tasks: list[asyncio.Task] = []
        self.started = False
        self.ended = False
        # the room as the client's rules see it, for the rater (`context`)
        self.crowd_level = 3.0
        self.dims = dict(self.mc.baseline)
        # what each side said last
        self.user_verse = ""
        self.opponent_verse = ""
        # the current user turn (`listen` .. `listen_end`)
        self.listening = False
        self.round = 1
        self.turn_start_s = 0.0
        self._turn_over = asyncio.Event()
        self._cursor = 0.0           # end time of the last word already sent for rating
        self._last_chunk_t = 0.0
        self._inflight: asyncio.Task | None = None
        self.drops: list[str] = []           # word drops for the current user turn
        self.drops_used: set[str] = set()
        self._hinted = ""                    # word the last rhyme hints were for

    # ---- inbound -------------------------------------------------------
    async def on_json(self, msg: dict) -> None:
        kind = msg.get("type")
        if kind == "start":
            await self._start(msg)
        elif kind == "ping":
            await self.send({"type": "pong", "t": self.track.duration_s, "client": msg.get("client")})
        elif kind == "stop":
            await self.close()
        elif not self.started:
            await self.send({"type": "error", "message": f"send start first (got {kind!r})"})
        elif kind == "listen":
            await self._listen(msg)
        elif kind == "context":
            self._context(msg)
        elif kind == "listen_end":
            self._turn_over.set()
        elif kind == "verse":
            self.tasks.append(asyncio.create_task(self._verse(msg)))
        else:
            await self.send({"type": "error", "message": f"unknown message type {kind!r}"})

    def on_audio(self, data: bytes) -> None:
        """Mic audio is one continuous timeline (so the beat grid stays valid all battle).

        Outside the user's turn the transcriber gets silence instead, so the
        opponent's voice bleeding into the mic is never transcribed as the user.
        """
        if not self.started:
            return
        x = _resample(pcm16_to_float(data), self.cfg.client_sample_rate, self.sr)
        self.track.push(x)
        self.transcriber.push(x if self._live() else np.zeros_like(x))

    def _live(self) -> bool:
        return self.listening and self.track.duration_s >= self.turn_start_s

    async def _start(self, msg: dict) -> None:
        if self.started:
            return
        self.cfg = BattleConfig(client_sample_rate=int(msg.get("sample_rate", self.sr)),
                                assist=bool(msg.get("assist", False)))
        self.mc = mcs.get(msg.get("mc"))
        self.outfit = _outfit(msg.get("outfit"))
        self.profile = interview.clean_profile(msg.get("profile"))
        self.want_mouth = msg.get("mouth") is not False   # the web GUI has no face to move: no Rhubarb for it
        self.dims = dict(self.mc.baseline)
        if self.cfg.assist:
            await asyncio.get_running_loop().run_in_executor(None, hints.warm)
        beat = msg.get("beat")
        if beat and beat.get("bpm"):
            self.beat = BeatGrid(float(beat["bpm"]), float(beat.get("offset_ms", 0)) / 1000.0,
                                 int(beat.get("beats_per_bar", 4)))
        self.transcriber = await asyncio.get_running_loop().run_in_executor(None, transcribe.make, self.s, self.sr)
        self.started = True
        await self.send({
            "type": "ready", "sample_rate": self.sr,
            "rater": type(self.rater).__name__, "opponent": type(self.opponent).__name__,
            "transcriber": type(self.transcriber).__name__, "mc": self.mc.public(),
            "voice": "qwen" if self._voice_ready() else "client",
            "mouth": "rhubarb" if lipsync.available else None,   # opponent_line carries mouth cues
            "adlibs": ({t: f"adlib/{self.mc.id}/{i}"
                        for i, t in enumerate(self.voice.adlibs.get(self.voice.key_for(self.mc), {}))}
                       if self._voice_ready() else {}),
        })

    def _context(self, msg: dict) -> None:
        self.crowd_level = _num(msg.get("crowd_level"), 0, 11, self.crowd_level)
        if "opponent" in msg:
            self.dims = _dims(msg["opponent"], self.dims)

    def _voice_ready(self) -> bool:
        return bool(self.voice and self.voice.ready)

    def _bar_s(self) -> float:
        return self.beat.beats_per_bar * self.beat.beat_s if self.beat else 2.5

    # ---- the user's turn -----------------------------------------------
    async def _listen(self, msg: dict) -> None:
        if self.listening:
            await self.send({"type": "error", "message": "already listening; send listen_end first"})
            return
        self._context(msg)
        self.round = int(_num(msg.get("round"), 1, 999, self.round))
        self.turn_start_s = _num(msg.get("t"), 0, 1e7, self.track.duration_s)
        self._cursor = self.turn_start_s - 0.3
        self._last_chunk_t = self.turn_start_s
        self._inflight = None
        self.transcriber.begin_turn(self.turn_start_s)
        self._turn_over.clear()
        self.listening = True
        if self.cfg.assist:
            self.drops, self.drops_used, self._hinted = hints.word_drops(self.opponent_verse), set(), ""
            await self.send({"type": "word_drops", "words": self.drops})
        if hasattr(self.transcriber, "keyterms"):   # words worth hearing right (a streaming transcriber can boost them)
            self.transcriber.keyterms = [*self.drops, self.mc.name] if self.cfg.assist else [self.mc.name]
        self.tasks.append(asyncio.create_task(self._listen_loop()))

    async def _listen_loop(self) -> None:
        loops = [asyncio.create_task(f()) for f in (self._transcribe_loop, self._metrics_loop, self._chunk_loop)]
        try:
            while not self._turn_over.is_set() and self.track.duration_s - self.turn_start_s < MAX_TURN_S:
                try:
                    await asyncio.wait_for(self._turn_over.wait(), 0.1)
                except asyncio.TimeoutError:
                    pass
        finally:
            for t in loops:
                t.cancel()
            await asyncio.gather(*loops, return_exceptions=True)
        self.listening = False
        try:
            upd = await self.transcriber.step(final=True)
            await self._emit_transcript(upd)
            if self._inflight:
                await asyncio.gather(self._inflight, return_exceptions=True)
            rest = self._pending_words(include_partial=False)
            if rest:  # the verse's last chunk
                await self._rate_user_chunk(rest)
        except Exception as e:
            log.exception("finishing the turn failed")
            await self.send({"type": "error", "message": f"finishing the turn failed: {e}"})
        self.user_verse = self._turn_text()
        await self.send({"type": "listen_done", "round": self.round, "verse": self.user_verse})

    # ---- the opponent's turn -------------------------------------------
    async def _verse(self, msg: dict) -> None:
        """Write the comeback from the client's view of the battle, voice it, and rate it for the client."""
        try:
            rnd = int(_num(msg.get("round"), 1, 999, self.round))
            bars = int(_num(msg.get("bars"), 1, 64, 8))
            dims = _dims(msg.get("opponent"), self.dims)
            crowd = msg.get("crowd") if isinstance(msg.get("crowd"), dict) else {}
            score = msg.get("score") if isinstance(msg.get("score"), dict) else {}
            crowd_level = _num(crowd.get("level"), 0, 11, self.crowd_level)
            ctx = {
                "opponent": {**dims, "mood": str((msg.get("opponent") or {}).get("mood", "calm"))[:20]},
                "crowd": {"level": crowd_level, "mood": str(crowd.get("mood", ""))[:30]},
                "score": {"user": _num(score.get("user"), 0, 1e6, 0), "opponent": _num(score.get("opponent"), 0, 1e6, 0)},
                "bars": bars, "bar_s": self._bar_s(), "mc": self.mc,
                "riled": _strings(msg.get("riled")), "hits": _strings(msg.get("hits"), 3),
                "outfit": self.outfit, "profile": self.profile,
            }
            user_verse = self.user_verse
            try:
                verse = await self.opponent.respond(user_verse, ctx)
            except Exception as e:
                log.warning("opponent LLM failed, using template: %r", e)
                verse = await opponent_mod.TemplateOpponent().respond(user_verse, ctx)
            lines = verse["lines"] = opponent_mod.clean(verse["lines"], self.mc)
            deliveries = (verse.get("deliveries") or []) + [""] * len(lines)
            self.opponent_verse = "\n".join(lines)
            bar_s = self._bar_s()
            stream = self._voice_ready()
            # Delivery directions are only for the voice; the client gets the lyrics alone. With a server voice,
            # each line's audio follows in its own opponent_line message as soon as it is rendered.
            await self.send({"type": "opponent_verse", "round": rnd, "lines": lines, "bar_s": bar_s,
                             "audio": "stream" if stream else None,
                             "interpretation": verse.get("interpretation", ""), "source": verse.get("source", "")})
            if stream:
                self.tasks.append(asyncio.create_task(self._stream_verse(rnd, lines, deliveries[:len(lines)], bar_s)))

            # Rated two lines at a time, all at once; the client applies each when its lines have played.
            chunks = [(i, min(i + 2, len(lines))) for i in range(0, len(lines), 2)]

            async def rate(a: int, b: int) -> None:
                so_far = " ".join(lines[:b])
                ends = [l.split()[-1].lower() for l in lines[:b] if l.split()]
                lm = lyrics.compute(lyrics.words(so_far), ends).as_dict()
                inp = RatingInput(" / ".join(lines[a:b]), "\n".join(lines[:b]), self.mc.flow_profile, lm, "opponent",
                                  crowd_level, dict(dims), user_verse, rnd, a * bar_s, opponent_profile=self._profile(),
                                  user_outfit=list(self.outfit), user_profile=self.profile)
                c, o = await self._rate_both(inp)
                await self._send_rating("opponent", rnd, {"text": inp.chunk, "lines": [a, b]}, c, o)

            await asyncio.gather(*(rate(a, b) for a, b in chunks))
        except asyncio.CancelledError:
            raise
        except Exception as e:
            log.exception("verse failed")
            await self.send({"type": "error", "message": f"verse failed: {e}"})

    async def _stream_verse(self, rnd: int, lines: list[str], deliveries: list[str], bar_s: float) -> None:
        """Render the verse one line at a time and send each line's audio as soon as it exists.

        The client starts the verse once enough lines are in to play it without a gap. Delivery
        directions stay here; the client only gets audio URLs, and each line's mouth cues (Rhubarb, see
        lipsync.py). If a line fails or times out, the rest go out with audio None and the client voices them itself.
        """
        mc = self.mc
        t0 = time.perf_counter()
        failed = False
        sent: asyncio.Task | None = None   # the previous line's message, so lines go out in order

        async def emit(i: int, line: str, wav: bytes | None, url: str | None, before: asyncio.Task | None) -> None:
            # mouth cues take ~1/6 of the line's length on the CPU; they run while the next line is voiced
            mouth = await lipsync.mouth_cues(wav, line) if wav and self.want_mouth else None
            if before:
                await before
            await self.send({"type": "opponent_line", "round": rnd, "line": i, "audio": url, "mouth": mouth})

        for i, (line, d) in enumerate(zip(lines, deliveries)):
            wav = url = None
            if not failed:
                try:
                    wav = await asyncio.wait_for(
                        self.voice.render_line_async(line, d, bar_s, mc.speaker, mc.voice_style, mc=mc), LINE_TIMEOUT_S)
                    url = f"clip/{self.clips.put(wav)}"
                except Exception as e:
                    failed = True
                    log.warning("voice failed at line %d, client voices the rest: %r", i, e)
            sent = asyncio.create_task(emit(i, line, wav, url, sent))
        if sent:
            await sent
        self.voice.release()
        log.info("voiced %d lines in %.1f s (%.2f s/line, bar %.2f s)", len(lines), time.perf_counter() - t0,
                 (time.perf_counter() - t0) / max(len(lines), 1), bar_s)

    # ---- loops ---------------------------------------------------------
    async def _transcribe_loop(self) -> None:
        while True:
            upd = await self.transcriber.step()
            await self._emit_transcript(upd)
            await asyncio.sleep(0.2)

    async def _emit_transcript(self, upd) -> None:
        self.words.extend(upd.committed)
        changed = bool(upd.committed) or [w.text for w in upd.partial] != [w.text for w in self.partial]
        self.partial = upd.partial
        if changed:
            await self.send({"type": "transcript", "t": round(self.track.duration_s, 2),
                             "committed": [w.as_dict() for w in upd.committed],
                             "partial": " ".join(w.text for w in upd.partial)})
            await self._send_hints()

    async def _send_hints(self) -> None:
        """Rhymes for the last content word heard (partial words too, so hints keep up with the rapper)."""
        if not self.cfg.assist or not self.listening:
            return
        heard = [w for w in lyrics.words(" ".join(w.text for w in self._turn_words() + self.partial))
                 if w not in lyrics.STOPWORDS and len(w) > 1]
        if heard and heard[-1] != self._hinted:
            self._hinted = heard[-1]
            await self.send({"type": "hints", "word": heard[-1],
                             "rhymes": hints.rhymes(heard[-1], explicit=self.mc.explicit)})

    async def _metrics_loop(self) -> None:
        while True:
            await asyncio.sleep(self.s.metrics_interval_s)
            if self.track.duration_s < self.turn_start_s + 0.5:
                continue
            t = self.track.duration_s
            win = metrics.compute(self.track, max(self.turn_start_s, t - 6.0), t, self.beat)
            turn = metrics.compute(self.track, self.turn_start_s, t, self.beat)
            await self.send({"type": "metrics", "t": round(t, 2), "window": win.as_dict(), "turn": turn.as_dict(),
                             "lyrics": self._lyrics().as_dict(),
                             "silence_s": round(metrics.trailing_silence_s(self.track), 2)})

    async def _chunk_loop(self) -> None:
        """Cut the live transcript into chunks and rate each one as soon as it is cut."""
        while True:
            await asyncio.sleep(0.1)
            t = self.track.duration_s
            if t < self.turn_start_s + 0.5 or (self._inflight and not self._inflight.done()):
                continue
            pending = self._pending_words(include_partial=True)
            silence = metrics.trailing_silence_s(self.track)
            if len(pending) >= CHUNK_WORDS or (len(pending) >= MIN_CHUNK_WORDS and silence >= PAUSE_S):
                self._inflight = asyncio.create_task(self._rate_user_chunk(pending))
            elif not pending and silence >= SILENCE_CHUNK_S and t - self._last_chunk_t >= SILENCE_CHUNK_S:
                self._inflight = asyncio.create_task(self._rate_user_chunk([]))

    # ---- rating --------------------------------------------------------
    def _pending_words(self, include_partial: bool) -> list[Word]:
        ws = self.words + (self.partial if include_partial else [])
        return [w for w in ws if w.start > self._cursor + 0.02 and w.start >= self.turn_start_s - 0.3]

    def _turn_words(self) -> list[Word]:
        return [w for w in self.words if w.start >= self.turn_start_s - 0.3]

    def _turn_text(self) -> str:
        return " ".join(w.text for w in self._turn_words())

    def _bar_endings(self, ws: list[Word]) -> list[str]:
        """Last word of each bar (by the beat grid, or by pauses when there is no beat)."""
        out, key = [], None
        for i, w in enumerate(ws):
            k = self.beat.bar_index(w.start) if self.beat else (key or 0) + (i > 0 and w.start - ws[i - 1].end > PAUSE_S)
            if k != key and i:
                out.append(ws[i - 1].text)
            key = k
        if ws:
            out.append(ws[-1].text)
        return [lyrics.words(x)[-1] for x in out if lyrics.words(x)]

    def _lyrics(self, extra: list[Word] = ()) -> lyrics.LyricMetrics:
        ws = self._turn_words() + [w for w in extra if w not in self.words]
        return lyrics.compute(lyrics.words(" ".join(w.text for w in ws)), self._bar_endings(ws))

    async def _rate_both(self, inp: RatingInput) -> tuple[CrowdRating, OpponentRating]:
        async def guarded(primary, backup):
            try:
                return await asyncio.wait_for(primary(inp), RATER_TIMEOUT_S)
            except Exception as e:
                log.warning("rater failed, using heuristic: %r", e)
                r = await backup(inp)
                r.source = "heuristic-fallback"
                return r
        return await asyncio.gather(guarded(self.rater.rate_crowd, self.fallback.rate_crowd),
                                    guarded(self.rater.rate_opponent, self.fallback.rate_opponent))

    async def _rate_user_chunk(self, chunk: list[Word]) -> None:
        now = self.track.duration_s
        start = max(self._last_chunk_t, self.turn_start_s)
        if chunk:
            start = min(start, chunk[0].start)
            self._cursor = chunk[-1].end
        self._last_chunk_t = now
        flow = metrics.compute(self.track, max(self.turn_start_s, min(start, now - 2.0)), now, self.beat)
        text = " ".join(w.text for w in chunk) or "(silence)"
        inp = RatingInput(text, (self._turn_text() + " " + " ".join(w.text for w in chunk if w not in self.words)).strip(),
                          flow.as_dict(), self._lyrics(chunk).as_dict(), "user", self.crowd_level, dict(self.dims),
                          self.opponent_verse, self.round, now - self.turn_start_s,
                          opponent_profile=self._profile(), sore_spots=list(self.mc.sore_spots) if chunk else [],
                          user_outfit=list(self.outfit), user_profile=self.profile)
        crowd, opp = await self._rate_both(inp)
        dropped = [d for d in self.drops if d not in self.drops_used and hints.said(d, lyrics.words(text))]
        self.drops_used.update(dropped)
        await self._send_rating("user", self.round, {"text": text, "start": round(start, 2), "end": round(now, 2)},
                                crowd, opp, stall=not chunk, word_drops=dropped)

    def _profile(self) -> dict:
        """The MC's character sheet as the rater sees it."""
        mc = self.mc
        return {"name": mc.name, "persona": mc.persona, "traits": list(mc.traits), "outfit": list(mc.outfit),
                "sore_spots": [s.spot for s in mc.sore_spots]}

    async def _send_rating(self, performer: str, rnd: int, chunk: dict, crowd: CrowdRating, opp: OpponentRating,
                           stall: bool = False, word_drops: list[str] | None = None) -> None:
        await self.send({
            "type": "rating", "performer": performer, "round": rnd, "t": round(self.track.duration_s, 2),
            "chunk": chunk, "stall": stall, "crowd": crowd.as_dict(), "opponent": opp.as_dict(),
            "word_drops": word_drops or [],
        })

    async def close(self) -> None:
        if self.ended:
            return
        self.ended = True
        self._turn_over.set()
        if hasattr(self.transcriber, "close"):
            self.transcriber.close()
        for t in self.tasks + ([self._inflight] if self._inflight else []):
            t.cancel()
        await asyncio.gather(*self.tasks, return_exceptions=True)
