"""HTTP + WebSocket entry point.

    uvicorn freestyle.server:app --host 0.0.0.0 --port 8000

Web GUI:   GET /        (backend/web)
WebSocket: /ws/battle   (protocol in PROTOCOL.md)
Health:    GET /health
"""
from __future__ import annotations

import asyncio
import json
import logging
import os
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import Body, FastAPI, HTTPException, Query, Request, Response, WebSocket, WebSocketDisconnect
from fastapi.staticfiles import StaticFiles

from . import config, dj, hints, interview, lipsync, mcs, songs, transcribe, voice, watchdog
from .auth import PasswordGate
from .session import ClipStore, Session

# Less fragmentation in PyTorch's GPU allocator; must be set before torch initializes CUDA.
os.environ.setdefault("PYTORCH_CUDA_ALLOC_CONF", "expandable_segments:True")

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")
log = logging.getLogger("freestyle.server")

settings = config.load()


clips = ClipStore()
song_list: list[dict] = []
qwen = None
_voice, _why = settings.voice_decision()
log.info("opponent voice: %s (%s)", {"qwen": "Qwen3-TTS", "breeze": "Breeze TTS 2", "indextts": "IndexTTS-2.5",
                                     "openrouter": f"{settings.tts_model} (OpenRouter)"}.get(
    _voice, "off, the page voices the opponent"),
         _why)
if _voice == "qwen":
    from .qwen_voice import QwenVoice
    qwen = QwenVoice(settings.qwen_tts_model, settings.qwen_tts_speaker, settings.qwen_dtype, settings.resolved_low_vram())
elif _voice == "breeze":   # same interface as QwenVoice; the model runs in the Breeze service
    from .breeze_voice import BreezeVoice
    qwen = BreezeVoice(settings.breeze_url, settings.breeze_cfg)
elif _voice == "openrouter":   # hosted TTS: no GPU, voices from the character sheets' descriptions
    from .or_voice import OpenRouterVoice
    qwen = OpenRouterVoice(settings.openrouter_api_key, settings.tts_model)
elif _voice == "indextts":   # clones the voices Breeze designed (backend/tools/make_voice_refs.py)
    from .breeze_voice import BreezeVoice
    qwen = BreezeVoice(settings.indextts_url, can_design=False, name="IndexTTS-2.5")


def _load_qwen() -> None:
    log.info("loading the %s voice in the background; /health says voice=loading until ready",
             {"breeze": "Breeze TTS 2", "indextts": "IndexTTS-2.5", "openrouter": settings.tts_model}.get(
                 _voice, "Qwen3-TTS (first run downloads ~4.5 GB)"))
    try:
        qwen.load(voices=mcs.ROSTER)
    except Exception:
        log.exception("qwen tts failed to load; the page will voice the opponent itself")


@asynccontextmanager
async def lifespan(_app: FastAPI):
    """Load Whisper up front so the first battle doesn't wait ~20 s for it. Qwen TTS loads in the
    background (~40 s the first time); battles started before it is ready use the page's voice."""
    loop = asyncio.get_running_loop()
    watchdog.start(loop)   # logs what the loop is doing whenever it stops answering for over a second
    loop.run_in_executor(None, hints.warm)  # rhyme index for assist mode (~1 s)
    song_list[:] = await loop.run_in_executor(None, songs.catalog)  # tempo/bar grid per beat, cached (~1 s/new song)
    log.info("beats: %s", ", ".join(f"{s['title']} {s['bpm']:g} BPM" for s in song_list) or "none in backend/songs")
    if qwen:
        loop.run_in_executor(None, _load_qwen)
    log.info("mouth cues: %s", f"Rhubarb ({lipsync.RHUBARB})" if lipsync.available else "off (no Rhubarb; ./setup.sh --rhubarb)")
    log.info("speech-to-text: %s", {
        "deepgram": f"Deepgram {settings.deepgram_model}, streaming" + ("" if settings.deepgram_api_key else " (no DEEPGRAM_API_KEY!)"),
        "openrouter": f"{settings.stt_model} on OpenRouter, a phrase at a time",
        "whisper": f"faster-whisper {settings.whisper_model}, local"}.get(settings.transcriber, settings.transcriber))
    if settings.transcriber == "whisper":
        await loop.run_in_executor(None, transcribe.make, settings, settings.sample_rate)
        log.info("whisper model %s loaded (%s)", settings.whisper_model, settings.resolved_whisper_compute())
    yield


app = FastAPI(title="Freestyle Simulator backend", lifespan=lifespan)
app.add_middleware(PasswordGate, password=settings.password)
log.info("password: %s", "required (FREESTYLE_PASSWORD)" if settings.password
         else "none, anyone who can reach this server can use it (set FREESTYLE_PASSWORD)")
WEB_DIR = Path(__file__).resolve().parents[1] / "web"


@app.get("/health")
async def health() -> dict:
    return {
        "ok": True,
        "transcriber": settings.transcriber,
        "rater": settings.resolved_rater(),
        "opponent": settings.resolved_opponent(),
        "voice": "qwen" if qwen and qwen.ready else "loading" if qwen else "client",
        "voice_engine": _voice,   # qwen | indextts | breeze | openrouter | none ("voice" stays "qwen" for any server-rendered voice)
        "mouth": "rhubarb" if lipsync.available else None,   # mouth cues with each opponent_line, and POST /lipsync
    }


@app.get("/tts")
async def tts(text: str = Query(..., max_length=400), bpm: float = Query(0, ge=0, le=300),
              seconds: float = Query(0, ge=0, le=20)) -> Response:
    """Opponent voice (espeak-ng).

    bpm: one bar, words placed on the 16th grid. seconds: the whole line fitted to that length.
    Neither: plain speech (ad-libs).
    """
    if not voice.available:
        raise HTTPException(404, "espeak-ng not installed")
    if bpm:
        wav = await voice.rap_bar(text, bpm)
    elif seconds:
        wav = await voice.fit_line(text, seconds)
    else:
        wav = await voice.say(text)
    return Response(wav, media_type="audio/wav")


@app.post("/lipsync")
async def lipsync_cues(request: Request, text: str = Query("", max_length=400), accurate: bool = Query(False)) -> dict:
    """Mouth cues for a WAV (the request body): {"mouth": [[start_s, shape], ...]}, Rhubarb's shapes A-H and X
    (lipsync.py). For clips the server didn't voice (ad-libs, the headset's own). accurate: Rhubarb's English
    recognizer, using text if given; better, but slower than real time."""
    if not lipsync.available:
        raise HTTPException(404, "Rhubarb not installed (./setup.sh --rhubarb)")
    wav = await request.body()
    if not 44 < len(wav) <= 10_000_000:
        raise HTTPException(400, "send a WAV (up to 10 MB) as the body")
    cues = await lipsync.mouth_cues(wav, text or None, "pocketSphinx" if accurate else "phonetic")
    if cues is None:
        raise HTTPException(422, "Rhubarb couldn't read that audio")
    return {"mouth": cues}


@app.get("/clip/{key}")
async def clip(key: str) -> Response:
    wav = clips.get(key)
    if wav is None:
        raise HTTPException(404, "clip expired")
    return Response(wav, media_type="audio/wav")


@app.get("/adlib/{mc_id}/{i}")
async def adlib(mc_id: str, i: int) -> Response:
    clips = list((qwen.adlibs.get(qwen.key_for(mcs.get(mc_id)), {}) if qwen and qwen.ready else {}).values())
    if not 0 <= i < len(clips):
        raise HTTPException(404, "no such ad-lib")
    return Response(clips[i], media_type="audio/wav", headers={"Cache-Control": "max-age=3600"})


@app.get("/songs")
async def list_songs() -> list[dict]:
    """Backing beats with their tempo, bar grid and drop (start drop_s - count-in bars before it); audio at
    songs/files/<file>."""
    return [{**s, "url": f"songs/files/{s['file']}"} for s in song_list]


@app.post("/songs/{song_id}/markup")
async def save_song_markup(song_id: str, body: dict = Body(...)) -> dict:
    """Hand-marked phrases and breaks (the GUI's /markup.html): they replace the analysed ones in /songs."""
    song = next((s for s in song_list if s["id"] == song_id), None)
    if song is None:
        raise HTTPException(404, "no such song")
    try:
        segments = [{"kind": str(s["kind"]), "start": round(float(s["start"]), 4), "end": round(float(s["end"]), 4)}
                    for s in body.get("segments", [])]
        first = body.get("first_downbeat_s")
        markup = {"segments": segments, **({"first_downbeat_s": round(float(first), 4)} if first is not None else {})}
    except (KeyError, TypeError, ValueError):
        raise HTTPException(400, "segments: [{kind: phrase|break, start, end}] in seconds")
    if not any(s["kind"] == "phrase" and s["end"] > s["start"] for s in segments):
        raise HTTPException(400, "mark at least one phrase")
    if any(s["kind"] not in ("phrase", "break") or not 0 <= s["start"] < s["end"] <= song["duration_s"] + 0.5 for s in segments):
        raise HTTPException(400, "each segment needs a kind (phrase or break) and start < end within the song")
    songs.save_markup(song["file"], markup)
    song_list[:] = await asyncio.get_running_loop().run_in_executor(None, songs.catalog)
    log.info("marked %s: %d phrases", song["file"], sum(s["kind"] == "phrase" for s in segments))
    return next({**s, "url": f"songs/files/{s['file']}"} for s in song_list if s["id"] == song_id)


@app.delete("/songs/{song_id}/markup")
async def clear_song_markup(song_id: str) -> dict:
    """Back to the analysed phrases and breaks."""
    song = next((s for s in song_list if s["id"] == song_id), None)
    if song is None:
        raise HTTPException(404, "no such song")
    songs.save_markup(song["file"], None)
    song_list[:] = await asyncio.get_running_loop().run_in_executor(None, songs.catalog)
    return next({**s, "url": f"songs/files/{s['file']}"} for s in song_list if s["id"] == song_id)


@app.get("/mcs")
async def list_mcs() -> list[dict]:
    """The opponents, easiest first, for the difficulty dropdown."""
    return [m.public() for m in mcs.ROSTER]


@app.get("/mcs/{mc_id}/tip")
async def tip(mc_id: str, known: list[str] = Query([]), avoid: list[str] = Query([])) -> dict:
    """One of the MC's sore spots the player hasn't been told yet (the dressing room's sticky note after a loss).
    `known`: the ones already told, never again; `avoid`: ones the player found in the battle, only once the rest
    are told. `spot` is null when every one is known."""
    mc = mcs.BY_ID.get(mc_id)
    if mc is None:
        raise HTTPException(404, f"no MC {mc_id!r}")
    left = [s.spot for s in mc.sore_spots if s.spot not in known]
    pick = next((s for s in left if s not in avoid), left[0] if left else None)
    return {"mc": mc.id, "name": mc.name, "spot": pick, "left": max(0, len(left) - 1), "total": len(mc.sore_spots)}


@app.post("/interview/transcribe")
async def interview_transcribe(request: Request) -> dict:
    """One spoken interview answer, a WAV (mono or stereo, 16-bit) as the body: {"text": what they said}."""
    import io
    import wave
    import numpy as np
    body = await request.body()
    if not 44 < len(body) <= 10_000_000:
        raise HTTPException(400, "send a WAV (up to 10 MB) as the body")
    try:
        with wave.open(io.BytesIO(body)) as w:
            if w.getsampwidth() != 2:
                raise ValueError("16-bit only")
            sr, ch = w.getframerate(), w.getnchannels()
            x = np.frombuffer(w.readframes(w.getnframes()), "<i2").astype(np.float32) / 32768
    except Exception as e:
        raise HTTPException(400, f"not a 16-bit WAV: {e}")
    if ch > 1:
        x = x.reshape(-1, ch).mean(axis=1)
    x = x[: sr * 60]   # a minute at most
    target = settings.sample_rate
    if sr != target and len(x):
        x = np.interp(np.linspace(0, len(x) - 1, int(round(len(x) * target / sr))), np.arange(len(x)), x).astype(np.float32)
    return {"text": await interview.transcribe(settings, x, target)}


@app.post("/interview/summary")
async def interview_summary(payload: dict = Body(...)) -> dict:
    """The interview's answers ({"answers": [{"question", "answer"}]}) summed up for the opponents:
    {"name", "summary", "source"}. The headset keeps it and sends it with each battle (start.profile)."""
    return await interview.summarize(settings, payload.get("answers"))


@app.get("/interview/lines")
async def interview_lines() -> dict:
    """The interviewer's script: {"intro", "questions", "affirmations", "outro"}, each id -> text (her lines are
    pre-rendered from it by tools/render_interviewer.py)."""
    return {"intro": interview.INTRO, "questions": interview.QUESTIONS, "affirmations": interview.AFFIRMATIONS,
            "outro": interview.OUTRO}


@app.get("/dj/name")
async def dj_name(name: str = Query(..., min_length=1, max_length=80)) -> Response:
    """The player's rapper name (from the headset's save), announced in the DJ's voice: a WAV (dj.py; the DJ's other
    lines are pre-rendered by tools/render_dj.py). Cached per name. 404 with no voice to say it in."""
    wav = await dj.name_clip(settings, name)
    if wav is None:
        raise HTTPException(404, "no DJ voice (no OpenRouter key and no espeak-ng), or no name")
    return Response(wav, media_type="audio/wav", headers={"Cache-Control": "max-age=86400"})


@app.get("/rhymes")
async def rhymes(word: str = Query(..., min_length=1, max_length=40), n: int = Query(6, ge=1, le=20),
                 explicit: bool = False) -> dict:
    """Common words that rhyme with `word`, best first (the assist's rhymes): the headset pairs each floating
    keyword with one."""
    found = await asyncio.get_running_loop().run_in_executor(None, hints.rhymes, word, n, explicit)
    return {"word": word, "rhymes": found}


@app.websocket("/ws/battle")
async def battle(ws: WebSocket) -> None:
    await ws.accept()
    outbox: asyncio.Queue[dict | None] = asyncio.Queue()

    async def send(msg: dict) -> None:
        await outbox.put(msg)

    async def sender() -> None:
        while (msg := await outbox.get()) is not None:
            await ws.send_text(json.dumps(msg, allow_nan=False, default=str))

    session = Session(settings, send, voice=qwen, clips=clips)
    out = asyncio.create_task(sender())
    try:
        while True:
            msg = await ws.receive()
            if msg["type"] == "websocket.disconnect":
                break
            if msg.get("bytes") is not None:
                session.on_audio(msg["bytes"])
            elif msg.get("text") is not None:
                try:
                    await session.on_json(json.loads(msg["text"]))
                except json.JSONDecodeError:
                    await send({"type": "error", "message": "text frames must be JSON"})
            if session.ended:
                break
    except WebSocketDisconnect:
        pass
    finally:
        await session.close()
        await outbox.put(None)
        try:
            await asyncio.wait_for(out, 2)
        except Exception:
            out.cancel()
        log.info("session closed")


if songs.SONGS.is_dir():
    app.mount("/songs/files", StaticFiles(directory=songs.SONGS), name="songs")
class NoCacheStatic(StaticFiles):
    """The GUI's files: browsers must re-check them on every load (the Quest Browser otherwise keeps
    running a cached app.js after an update)."""

    async def get_response(self, path, scope):
        resp = await super().get_response(path, scope)
        resp.headers["Cache-Control"] = "no-cache"
        return resp


app.mount("/", NoCacheStatic(directory=WEB_DIR, html=True), name="web")
