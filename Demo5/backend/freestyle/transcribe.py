"""Streaming transcription behind one small interface.

`WhisperTranscriber` runs faster-whisper locally (free, GPU optional) using the
LocalAgreement trick from whisper_streaming: re-transcribe the unconfirmed tail
of the audio every step, and commit only the words two consecutive passes agree
on. Committed words never change; partial words may.
"""
from __future__ import annotations

import asyncio
import json
from dataclasses import dataclass
from typing import Protocol

import numpy as np

NO_SPEECH_PROB = 0.6
OVERLAP_S = 4.0  # how far back a re-transcribed word may drift and still be matched against the committed tail


@dataclass
class Word:
    text: str
    start: float  # seconds on the session audio timeline
    end: float
    prob: float = 1.0

    def as_dict(self) -> dict:
        return {"w": self.text, "s": round(self.start, 2), "e": round(self.end, 2)}


@dataclass
class TranscriptUpdate:
    committed: list[Word]  # newly final words, in order
    partial: list[Word]    # current guess for the words after them


class Transcriber(Protocol):
    def push(self, samples: np.ndarray) -> None: ...
    def begin_turn(self, t: float) -> None: ...
    async def step(self, final: bool = False) -> TranscriptUpdate: ...


def _norm(t: str) -> str:
    return "".join(ch for ch in t.lower() if ch.isalnum() or ch == "'")


class WhisperTranscriber:
    _models: dict[tuple, object] = {}

    def __init__(self, sample_rate: int, model: str, device: str = "auto", compute_type: str = "default",
                 min_step_s: float = 1.0, max_buffer_s: float = 12.0):
        from faster_whisper import WhisperModel

        key = (model, device, compute_type)
        if key not in self._models:  # load once per process, share across sessions
            self._models[key] = WhisperModel(model, device=device, compute_type=compute_type)
        self.model = self._models[key]
        self.sr = sample_rate
        self.min_step = int(min_step_s * sample_rate)
        self.max_buffer = max_buffer_s
        self.buffer = np.zeros(0, dtype=np.float32)
        self.buffer_start = 0.0
        self.committed: list[Word] = []
        self.prev_hyp: list[Word] = []
        self._new = 0
        self._lock = asyncio.Lock()

    def push(self, samples: np.ndarray) -> None:
        self.buffer = np.concatenate([self.buffer, samples])
        self._new += len(samples)

    def begin_turn(self, t: float) -> None:
        """Drop buffered audio (silence fed during the other side's turn) so it is never transcribed."""
        self.buffer_start += len(self.buffer) / self.sr
        self.buffer = np.zeros(0, dtype=np.float32)
        self.prev_hyp = []
        self._new = 0

    def _run(self, audio: np.ndarray, prompt: str) -> list[Word]:
        segments, _ = self.model.transcribe(
            audio, language="en", beam_size=1, word_timestamps=True,
            condition_on_previous_text=False, initial_prompt=prompt or None, vad_filter=False,
        )
        out = []
        for seg in segments:
            if seg.no_speech_prob > NO_SPEECH_PROB:  # Whisper invents "you" / "thank you" in silence
                continue
            for w in seg.words or []:
                if _norm(w.word):
                    out.append(Word(w.word.strip(), self.buffer_start + w.start, self.buffer_start + w.end, w.probability))
        return out

    def _new_words(self, hyp: list[Word]) -> list[Word]:
        """Words after the committed ones.

        Whisper's word times are unreliable around the commit point (a word can be
        stretched and its neighbours squashed earlier), so align on text first:
        find the committed tail inside the new pass and keep what follows it.
        Times are only the fallback.
        """
        if not self.committed:
            return hyp
        last_end = self.committed[-1].end
        hyp = [w for w in hyp if w.end > last_end - OVERLAP_S]
        tail = [_norm(w.text) for w in self.committed[-10:]]
        head = [_norm(w.text) for w in hyp]
        for n in range(min(len(tail), len(head)), 0, -1):
            for p in range(len(head) - n, -1, -1):  # latest occurrence of the longest match
                if head[p:p + n] == tail[-n:] and (n >= 2 or p == 0):
                    return hyp[p + n:]
        new = [w for w in hyp if w.end > last_end + 0.01 or w.start >= last_end - 0.05]
        if new and _norm(new[0].text) == tail[-1] and new[0].start < last_end + 0.2:
            new = new[1:]  # the last committed word, re-heard with a later timestamp
        return new

    async def step(self, final: bool = False) -> TranscriptUpdate:
        if self._new < self.min_step and not final:
            return TranscriptUpdate([], [])
        async with self._lock:
            self._new = 0
            # Prompt only with words whose audio was already trimmed off: prompting with words
            # still in the buffer makes Whisper think it said them and skip ahead.
            prompt = " ".join(w.text for w in self.committed[-30:] if w.end <= self.buffer_start + 0.05)
            audio = self.buffer.copy()
            hyp = await asyncio.get_running_loop().run_in_executor(None, self._run, audio, prompt)
            hyp = self._new_words(hyp)

            if final:
                agreed = hyp
            else:
                agreed = []
                for a, b in zip(self.prev_hyp, hyp):
                    if _norm(a.text) != _norm(b.text):
                        break
                    agreed.append(b)
            self.committed.extend(agreed)
            partial = hyp[len(agreed):]
            self.prev_hyp = partial

            buf_s = len(self.buffer) / self.sr
            if agreed and buf_s > self.max_buffer:
                cut = self.committed[-1].end
            elif buf_s > 2 * self.max_buffer:  # nobody agreed for too long: force progress
                self.committed.extend(partial)
                agreed, partial, self.prev_hyp = agreed + partial, [], []
                cut = self.committed[-1].end if self.committed else self.buffer_start + buf_s - self.max_buffer
            else:
                cut = None
            if cut is not None:
                drop = int((cut - self.buffer_start) * self.sr)
                if drop > 0:
                    self.buffer = self.buffer[drop:]
                    self.buffer_start += drop / self.sr
            return TranscriptUpdate(agreed, partial)


class ScriptedTranscriber:
    """Replays a known word timing file as if it were recognised live. For tests.

    Word times in the file are relative to the start of the clip; each user turn
    replays the clip from that turn's start.
    """

    def __init__(self, sample_rate: int, path: str, lag_s: float = 0.6):
        with open(path) as f:
            data = json.load(f)
        self.script = [Word(w["w"], w["s"], w["e"]) for w in data["words"]]
        self.words: list[Word] = []
        self.sr = sample_rate
        self.t = 0.0
        self.lag = lag_s
        self.i = 0

    def push(self, samples: np.ndarray) -> None:
        self.t += len(samples) / self.sr

    def begin_turn(self, t: float) -> None:
        self.words = [Word(w.text, w.start + t, w.end + t) for w in self.script]
        self.i = 0

    async def step(self, final: bool = False) -> TranscriptUpdate:
        horizon = float("inf") if final else self.t - self.lag
        new = []
        while self.i < len(self.words) and self.words[self.i].end <= horizon:
            new.append(self.words[self.i])
            self.i += 1
        partial = [w for w in self.words[self.i:] if w.start <= self.t] if not final else []
        return TranscriptUpdate(new, partial)


class OpenRouterTranscriber:
    """Hosted speech-to-text through OpenRouter (/api/v1/audio/transcriptions), one phrase at a time.

    The endpoint isn't streaming, so audio is cut into phrases: at a pause (PAUSE_S of quiet after at least
    MIN_SPEECH_S of speech), or after MAX_SEG_S of nonstop rapping at the quietest moment of the last
    CUT_SEARCH_S (between words). Measured: words arrive ~1.1-2.0 s after their phrase ends.
    Each phrase is sent once (so audio is never billed twice) with word timestamps; up to MAX_INFLIGHT
    requests run at once and are committed in order. Silent stretches (the opponent's turn) aren't sent.
    No live partial words: a phrase's words arrive ~1 s after it ends.
    """

    PAUSE_S = 0.35
    MIN_SPEECH_S = 0.5
    MAX_SEG_S = 4.0                  # nonstop rapping: cut by then, at the quietest point of the last CUT_SEARCH_S
    CUT_SEARCH_S = 1.5
    MAX_INFLIGHT = 2
    URL = "https://openrouter.ai/api/v1/audio/transcriptions"

    def __init__(self, sample_rate: int, api_key: str, model: str = "openai/whisper-large-v3-turbo",
                 timeout_s: float = 15.0, transport=None):
        import httpx
        self.sr = sample_rate
        self.model = model
        self.client = httpx.AsyncClient(timeout=timeout_s, transport=transport,
                                        headers={"Authorization": f"Bearer {api_key}"})
        self.buffer = np.zeros(0, dtype=np.float32)
        self.buffer_start = 0.0          # session time of buffer[0]
        self.inflight: list[asyncio.Task] = []
        self.noise_db = -60.0

    def push(self, samples: np.ndarray) -> None:
        self.buffer = np.concatenate([self.buffer, samples])

    def begin_turn(self, t: float) -> None:
        self.buffer_start += len(self.buffer) / self.sr
        self.buffer = np.zeros(0, dtype=np.float32)

    def _frames_db(self, x: np.ndarray) -> np.ndarray:
        win = self.sr // 100
        n = len(x) // win
        rms = np.sqrt((x[: n * win].reshape(n, win) ** 2).mean(axis=1) + 1e-12)
        return 20 * np.log10(rms)

    def _cut_point(self, final: bool) -> int | None:
        """Sample index to cut the buffer at, or None to keep collecting."""
        db = self._frames_db(self.buffer)
        if not len(db):
            return None
        self.noise_db = min(max(float(np.percentile(db, 10)), -80.0), -30.0) if len(db) > 50 else self.noise_db
        voiced = db > max(self.noise_db + 12.0, -50.0)
        if final:
            return len(self.buffer)
        if voiced.sum() * 0.01 < self.MIN_SPEECH_S:
            if len(db) * 0.01 > 2 * self.MAX_SEG_S:          # long silence: drop it, never send it
                return -int(len(self.buffer) - self.sr * 0.3)
            return None
        quiet = int(self.PAUSE_S / 0.01)
        if len(voiced) > quiet and not voiced[-quiet:].any():
            return len(self.buffer) - (quiet // 2) * (self.sr // 100)   # cut inside the pause
        if len(db) * 0.01 >= self.MAX_SEG_S:
            # Quietest 30 ms (smoothed, so a single dip inside a word doesn't win) in the search window:
            # the gap between two words, so neither phrase gets half a word.
            n = int(self.CUT_SEARCH_S / 0.01)
            tail = np.convolve(db[-n:], np.ones(3) / 3, mode="same")
            return (len(db) - n + int(np.argmin(tail))) * (self.sr // 100)
        return None

    async def _send(self, seg: np.ndarray, start: float) -> list[Word]:
        import base64
        import io
        import wave
        buf = io.BytesIO()
        with wave.open(buf, "wb") as w:
            w.setnchannels(1); w.setsampwidth(2); w.setframerate(self.sr)
            w.writeframes((np.clip(seg, -1, 1) * 32767).astype("<i2").tobytes())
        body = {"model": self.model, "language": "en", "response_format": "verbose_json",
                "timestamp_granularities": ["word"],
                "input_audio": {"data": base64.b64encode(buf.getvalue()).decode(), "format": "wav"}}
        try:
            r = await self.client.post(self.URL, json=body)
            r.raise_for_status()
            d = r.json()
        except Exception as e:
            import logging
            logging.getLogger("freestyle.transcribe").warning("openrouter stt failed for a %.1f s phrase: %r",
                                                              len(seg) / self.sr, e)
            return []
        words = d.get("words") or [w for s in d.get("segments", []) for w in s.get("words", [])]
        return [Word(w["word"].strip(), start + float(w["start"]), start + float(w["end"]), float(w.get("probability", 1.0)))
                for w in words if _norm(w.get("word", ""))]

    async def step(self, final: bool = False) -> TranscriptUpdate:
        cut = self._cut_point(final) if len(self.inflight) < self.MAX_INFLIGHT or final else None
        if cut is not None and cut < 0:                          # drop leading silence
            drop = -cut
            self.buffer, self.buffer_start = self.buffer[drop:], self.buffer_start + drop / self.sr
        elif cut:
            seg, start = self.buffer[:cut], self.buffer_start
            self.buffer, self.buffer_start = self.buffer[cut:], self.buffer_start + cut / self.sr
            if (self._frames_db(seg) > max(self.noise_db + 12.0, -50.0)).sum() * 0.01 >= 0.15:
                self.inflight.append(asyncio.create_task(self._send(seg, start)))
        if final and self.inflight:
            await asyncio.gather(*self.inflight)
        committed: list[Word] = []
        while self.inflight and self.inflight[0].done():         # in order: a slow phrase holds later ones
            committed += self.inflight.pop(0).result()
        return TranscriptUpdate(committed, [])


class DeepgramTranscriber:
    """Deepgram streaming speech-to-text (TRANSCRIBER=deepgram): partial words within a few hundred ms.

    One WebSocket per user turn: opened when the turn starts and closed when it ends, so only the turn's audio is
    streamed (Deepgram bills the audio sent). Interim results become the partial words, final ones the committed
    words, on the session timeline. `keyterms` (set by the session before a turn: the word drops, the MC's name)
    are boosted with Nova-3's keyterm prompting.

    If Deepgram can't be reached or drops out, the battle carries on with `fallback()` (OpenRouter's phrase-at-a-time
    transcription, when there's a key for it), from the end of the last word Deepgram committed.

    Words over silence are dropped: Nova-3 sometimes "hears" an earlier word again in the quiet after the rapper stops
    (measured: "rhymes" from 4 s of silence after "...my rhymes cut deep like the cold in december").
    """

    URL = "wss://api.deepgram.com/v1/listen"
    CONNECT_TIMEOUT_S = 5.0
    FINALIZE_TIMEOUT_S = 2.0

    def __init__(self, sample_rate: int, api_key: str, model: str = "nova-3", use_keyterms: bool = True,
                 fallback=None, url: str | None = None):
        self.sr = sample_rate
        self.key = api_key
        self.model = model
        self.use_keyterms = use_keyterms
        self.make_fallback = fallback          # () -> Transcriber, or None
        self.url = url or self.URL
        self.keyterms: list[str] = []
        self.t = 0.0                           # session time of the end of the audio pushed so far
        self.fallback = None                   # the transcriber in use once Deepgram has failed
        self.failed = False
        self._reset_turn(None)

    def _reset_turn(self, start: float | None) -> None:
        self.turn_start = start                # None: not in a user turn, nothing is streamed
        self.offset: float | None = None       # session time of the first sample streamed this turn
        self.turn_audio: list[np.ndarray] = []
        self.pending: list[bytes] = []         # audio waiting for the connection
        self.ws = None
        self.queue: asyncio.Queue | None = None
        self.tasks: list[asyncio.Task] = []
        self.new: list[Word] = []              # committed since the last step
        self.partial: list[Word] = []
        self.last_end = start or 0.0           # end of the last committed word
        self.finalized: asyncio.Event | None = None

    # ---- the Transcriber interface ----
    def push(self, samples: np.ndarray) -> None:
        t0, self.t = self.t, self.t + len(samples) / self.sr
        if self.fallback:
            self.fallback.push(samples)
            return
        if self.turn_start is None or self.t <= self.turn_start:
            return
        if t0 < self.turn_start:               # only the turn's own audio is streamed
            samples = samples[int(round((self.turn_start - t0) * self.sr)):]
            t0 = self.turn_start
        if self.offset is None:
            self.offset = t0
        self.turn_audio.append(samples)
        data = (np.clip(samples, -1, 1) * 32767).astype("<i2").tobytes()
        if self.queue is not None:
            self.queue.put_nowait(data)
        else:
            self.pending.append(data)

    def begin_turn(self, t: float) -> None:
        if self.fallback:
            self.fallback.begin_turn(t)
            return
        self._close()
        self._reset_turn(t)

    async def step(self, final: bool = False) -> TranscriptUpdate:
        if self.fallback:
            return await self.fallback.step(final)
        if self.turn_start is None or self.failed:
            return TranscriptUpdate([], [])
        if self.ws is None:
            await self._open()
            if self.fallback:
                return await self.fallback.step(final)
        if final:
            await self._finish()
        new, self.new = self.new, []
        return TranscriptUpdate(new, [] if final else list(self.partial))

    def close(self) -> None:
        self._close()

    # ---- the stream ----
    async def _open(self) -> None:
        from urllib.parse import urlencode

        from websockets.asyncio.client import connect

        query = [("model", self.model), ("language", "en"), ("encoding", "linear16"), ("sample_rate", self.sr),
                 ("channels", 1), ("interim_results", "true"), ("punctuate", "false"), ("smart_format", "false")]
        if self.use_keyterms and self.model.startswith("nova-3"):   # keyterm prompting is a Nova-3 feature
            query += [("keyterm", k) for k in dict.fromkeys(k.strip() for k in self.keyterms) if k][:50]
        try:
            self.ws = await asyncio.wait_for(
                connect(f"{self.url}?{urlencode(query)}", additional_headers={"Authorization": f"Token {self.key}"},
                        max_size=None, open_timeout=self.CONNECT_TIMEOUT_S),
                self.CONNECT_TIMEOUT_S + 1)
        except Exception as e:
            self._fail(f"couldn't connect: {e!r}")
            return
        self.finalized = asyncio.Event()
        self.queue = asyncio.Queue()
        for data in self.pending:
            self.queue.put_nowait(data)
        self.pending = []
        self.tasks = [asyncio.create_task(self._send_loop(self.ws, self.queue)),
                      asyncio.create_task(self._receive_loop(self.ws))]

    async def _send_loop(self, ws, queue: asyncio.Queue) -> None:
        try:
            while (item := await queue.get()) is not None:
                await ws.send(item)            # bytes: audio; str: a control message
        except asyncio.CancelledError:
            raise
        except Exception as e:
            self._fail(f"sending failed: {e!r}")

    async def _receive_loop(self, ws) -> None:
        try:
            async for raw in ws:
                if isinstance(raw, bytes):
                    continue
                d = json.loads(raw)
                if d.get("type") != "Results":
                    continue
                alt = (d.get("channel", {}).get("alternatives") or [{}])[0]
                words = self._heard([Word(w.get("punctuated_word") or w["word"], self.offset + float(w["start"]),
                                          self.offset + float(w["end"]), float(w.get("confidence", 1.0)))
                                     for w in alt.get("words", []) if _norm(w.get("word", ""))])
                if d.get("is_final"):
                    self.new += words
                    self.partial = []
                    if words:
                        self.last_end = words[-1].end
                    if d.get("from_finalize") and self.finalized:
                        self.finalized.set()
                else:
                    self.partial = words
        except asyncio.CancelledError:
            raise
        except Exception as e:
            if self.turn_start is not None:
                self._fail(f"the stream broke: {e!r}")

    def _heard(self, words: list[Word]) -> list[Word]:
        """The words with sound under them: louder than the turn's quiet by 12 dB somewhere in their span (the same
        test the OpenRouter transcriber uses for speech)."""
        if not words or not self.turn_audio:
            return words
        audio, win = np.concatenate(self.turn_audio), self.sr // 100
        n = len(audio) // win
        if n < 20:
            return words
        db = 20 * np.log10(np.sqrt((audio[: n * win].reshape(n, win) ** 2).mean(axis=1) + 1e-12))
        threshold = max(max(float(np.percentile(db, 10)), -80.0) + 12.0, -50.0)
        keep = []
        for w in words:
            a = int((w.start - self.offset) * 100)
            seg = db[max(a, 0): min(max(int((w.end - self.offset) * 100), a + 1), n)]
            if not len(seg) or seg.max() > threshold:   # (audio not here yet: can't tell, keep it)
                keep.append(w)
        return keep

    async def _finish(self) -> None:
        """End of the turn: Deepgram finalizes what it has heard, then the stream closes."""
        if self.queue is not None and not self.failed:
            self.queue.put_nowait(json.dumps({"type": "Finalize"}))
            try:
                await asyncio.wait_for(self.finalized.wait(), self.FINALIZE_TIMEOUT_S)
            except asyncio.TimeoutError:
                self.new += self.partial          # no answer in time: keep what it had guessed
        self.partial = []
        if self.fallback:                         # it failed while finishing: the fallback has the rest
            return
        self._close()
        self.turn_start = None

    def _close(self) -> None:
        ws, queue, tasks = self.ws, self.queue, self.tasks
        self.ws, self.queue, self.tasks = None, None, []
        if queue is not None:
            queue.put_nowait(json.dumps({"type": "CloseStream"}))
            queue.put_nowait(None)

        async def shut() -> None:
            try:
                await asyncio.wait_for(asyncio.gather(tasks[0], return_exceptions=True), 1.0) if tasks else None
            except Exception:
                pass
            for t in tasks:
                t.cancel()
            if ws is not None:
                try:
                    await ws.close()
                except Exception:
                    pass

        if ws is not None or tasks:
            try:
                asyncio.get_running_loop().create_task(shut())
            except RuntimeError:                  # no loop (tests, shutdown): nothing left to tidy
                pass

    def _fail(self, why: str) -> None:
        """Carry on without Deepgram: the fallback gets the turn's audio after the last committed word."""
        import logging
        log = logging.getLogger("freestyle.transcribe")
        if self.failed:
            return
        self.failed = True
        self._close()
        fb = self.make_fallback() if self.make_fallback else None
        log.warning("deepgram %s; %s", why, "falling back to %s" % type(fb).__name__ if fb else "no transcript this battle")
        if fb is None:
            return
        if self.offset is None:                   # nothing streamed yet: the fallback takes the audio from here on
            fb.buffer_start, fb.buffer = self.t, np.zeros(0, dtype=np.float32)
        else:                                     # the turn so far, after the last word Deepgram committed
            audio = np.concatenate(self.turn_audio)
            skip = min(len(audio), int(max(0.0, self.last_end - self.offset) * self.sr))
            fb.buffer_start, fb.buffer = self.offset + skip / self.sr, audio[skip:].astype(np.float32)
        self.partial = []
        self.fallback = fb


class NullTranscriber:
    def push(self, samples: np.ndarray) -> None:
        pass

    def begin_turn(self, t: float) -> None:
        pass

    async def step(self, final: bool = False) -> TranscriptUpdate:
        return TranscriptUpdate([], [])


def make(settings, sample_rate: int) -> Transcriber:
    kind = settings.transcriber
    if kind == "whisper":
        return WhisperTranscriber(sample_rate, settings.whisper_model, settings.whisper_device, settings.resolved_whisper_compute())
    if kind == "openrouter":
        return OpenRouterTranscriber(sample_rate, settings.openrouter_api_key, settings.stt_model)
    if kind == "deepgram":
        fallback = ((lambda: OpenRouterTranscriber(sample_rate, settings.openrouter_api_key, settings.stt_model))
                    if settings.openrouter_api_key else None)
        if not settings.deepgram_api_key:
            import logging
            logging.getLogger("freestyle.transcribe").warning(
                "TRANSCRIBER=deepgram needs DEEPGRAM_API_KEY; using %s", "openrouter" if fallback else "none")
            return fallback() if fallback else NullTranscriber()
        return DeepgramTranscriber(sample_rate, settings.deepgram_api_key, settings.deepgram_model,
                                   settings.deepgram_keyterms, fallback=fallback, url=settings.deepgram_url or None)
    if kind == "scripted":
        return ScriptedTranscriber(sample_rate, settings.scripted_transcript)
    return NullTranscriber()
