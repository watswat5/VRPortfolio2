"""Pre-renders the interviewer's lines (freestyle/interview.py LINES) for the headset: one WAV per line, with Rhubarb
mouth cues beside it, in unity/Assets/Characters/Interviewer/Lines (<id>.wav, <id>.json) plus lines.json, the script
(id -> text) the game reads to know what each clip says. Run once; rerun after changing the lines.

    .venv/bin/python backend/tools/render_interviewer.py              # the cloud voice (VOICE=openrouter's TTS model)
    .venv/bin/python backend/tools/render_interviewer.py --espeak     # free placeholder lines (espeak-ng)
    ... --only q_name,ok_2                                            # just these lines

The cloud voice is billed per line (about 14 short lines). Every line must be the same voice, so one is always fixed:
[interviewer] in backend/voices.toml (an ElevenLabs or Fish voice ID for the TTS model's provider), else ElevenLabs
v3's "Rachel" (when the server's model has no voice for her, e.g. Fish without one, her lines are made on ElevenLabs).
    ... --model elevenlabs/eleven-v3                                  # a TTS model of your choosing
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

from freestyle import config, interview, lipsync, mcs, voice   # noqa: E402

OUT = ROOT / "unity" / "Assets" / "Characters" / "Interviewer" / "Lines"
DIRECTION = ("a poised, professional broadcast interviewer in a business suit, courteous and composed, clear and "
             "measured, polite but a little dry")
FALLBACK = ("elevenlabs/eleven-v3", "21m00Tcm4TlvDq8ikWAM")   # ElevenLabs "Rachel": a known voice, always the same


def cloud(settings, model=None):
    """A render function with her voice fixed: without an ID the provider picks a new voice every line."""
    from freestyle.or_voice import OpenRouterVoice
    from freestyle.qwen_voice import to_wav, trim
    model = model or settings.tts_model
    v = OpenRouterVoice(settings.openrouter_api_key, model)
    vid = mcs.load_voice_ids().get("interviewer", {}).get(v.family)
    if not vid:
        if v.family == "elevenlabs":
            vid = FALLBACK[1]
        else:
            print(f"no [interviewer] {v.family} voice in voices.toml: using {FALLBACK[0]} (Rachel) so every line is one voice")
            v = OpenRouterVoice(settings.openrouter_api_key, FALLBACK[0])
            vid = FALLBACK[1]
    # ElevenLabs v3 only knows short audio tags ("[calm]"): a longer direction gets read out. Fish takes a description.
    direction = "calm" if v.family == "elevenlabs" else DIRECTION
    print(f"voice: {v.model}, {vid}, [{direction[:40]}]")

    def render(text: str) -> bytes:
        x, sr = v._speech(direction, text, vid)
        return to_wav(trim(x, sr=sr), sr)
    return render


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--espeak", action="store_true", help="free placeholder lines (espeak-ng)")
    ap.add_argument("--only", default="", help="comma-separated line ids")
    ap.add_argument("--model", default="", help="the TTS model (default: the server's TTS_MODEL)")
    a = ap.parse_args()
    config.load_env_file()   # backend/.env: the API keys
    settings = config.Settings()
    only = {x.strip() for x in a.only.split(",") if x.strip()}
    lines = {k: t for k, t in interview.LINES.items() if not only or k in only}
    if not a.espeak and not settings.openrouter_api_key:
        sys.exit("no OPENROUTER_API_KEY (backend/.env): use --espeak for placeholder lines")
    if a.espeak and not voice.available:
        sys.exit("espeak-ng isn't installed")
    render = None if a.espeak else cloud(settings, a.model or None)
    OUT.mkdir(parents=True, exist_ok=True)
    for key, text in lines.items():
        wav = asyncio.run(voice.say(text)) if a.espeak else render(text)
        (OUT / f"{key}.wav").write_bytes(wav)
        cues = asyncio.run(lipsync.mouth_cues(wav, text, "pocketSphinx")) if lipsync.available else None
        (OUT / f"{key}.json").write_text(json.dumps({"text": text, "mouth": cues or []}))
        with wave.open(io.BytesIO(wav)) as w:
            secs = w.getnframes() / w.getframerate()
        print(f"{key}: {secs:.1f} s{'' if cues else ' (no mouth cues: Rhubarb not installed)'}  {text}")
    (OUT / "lines.json").write_text(json.dumps({
        "intro": list(interview.INTRO), "questions": list(interview.QUESTIONS),
        "affirmations": list(interview.AFFIRMATIONS), "outro": list(interview.OUTRO),
        "text": interview.LINES}, indent=1))
    print("->", OUT)


if __name__ == "__main__":
    main()
