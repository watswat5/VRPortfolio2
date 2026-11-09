"""Pre-renders the DJ's lines (freestyle/dj.py LINES) for the headset: one WAV per line, with Rhubarb mouth cues beside
it, in unity/Assets/Characters/DJ/Lines (<id>.wav, <id>.json) plus lines.json, the script (each kind's ids, and id ->
text) that Freestyle > Set Up DJ reads. Run once; rerun after changing the lines. (The player's rapper name is the one
live line: the server's GET /dj/name, in the same voice.)

    .venv/bin/python backend/tools/render_dj.py              # the cloud voice (the server's TTS_MODEL on OpenRouter)
    .venv/bin/python backend/tools/render_dj.py --espeak     # free placeholder lines (espeak-ng)
    ... --only greet_1,count_4                               # just these lines

The cloud voice is billed per line (about 25 short lines). Every line must be the same voice, so one is always fixed:
[dj] in backend/voices.toml (an ElevenLabs or Fish voice ID for the TTS model's provider), else ElevenLabs v3's "Josh".
    ... --model elevenlabs/eleven-v3                         # a TTS model of your choosing (the server's /dj/name
                                                             # uses TTS_MODEL: keep them the same)
"""
import argparse
import asyncio
import io
import json
import sys
import wave
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "backend"))

from freestyle import config, dj, lipsync, voice   # noqa: E402

OUT = ROOT / "unity" / "Assets" / "Characters" / "DJ" / "Lines"


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--espeak", action="store_true", help="free placeholder lines (espeak-ng)")
    ap.add_argument("--only", default="", help="comma-separated line ids")
    ap.add_argument("--model", default="", help="the TTS model (default: the server's TTS_MODEL)")
    a = ap.parse_args()
    config.load_env_file()   # backend/.env: the API keys
    settings = config.Settings()
    only = {x.strip() for x in a.only.split(",") if x.strip()}
    lines = {k: t for k, t in dj.LINES.items() if not only or k in only}
    if not a.espeak and not settings.openrouter_api_key:
        sys.exit("no OPENROUTER_API_KEY (backend/.env): use --espeak for placeholder lines")
    if a.espeak and not voice.available:
        sys.exit("espeak-ng isn't installed")
    if not a.espeak:
        v, vid, d, _ = dj.voice_for(settings, a.model or None)
        print(f"voice: {v.model}, {vid}, [{d[:40]}]")
    OUT.mkdir(parents=True, exist_ok=True)
    for key, text in lines.items():
        wav = dj._wav_level(asyncio.run(voice.say(text))) if a.espeak else dj.render_cloud(settings, text, model=a.model or None)
        (OUT / f"{key}.wav").write_bytes(wav)
        cues = asyncio.run(lipsync.mouth_cues(wav, text, "pocketSphinx")) if lipsync.available else None
        (OUT / f"{key}.json").write_text(json.dumps({"text": text, "mouth": cues or []}))
        with wave.open(io.BytesIO(wav)) as w:
            secs = w.getnframes() / w.getframerate()
        print(f"{key}: {secs:.1f} s{'' if cues else ' (no mouth cues: Rhubarb not installed)'}  {text}")
    (OUT / "lines.json").write_text(json.dumps({**dj.SCRIPT, "text": dj.LINES}, indent=1))
    print("->", OUT)


if __name__ == "__main__":
    main()
