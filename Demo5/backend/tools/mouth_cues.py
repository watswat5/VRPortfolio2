"""Bake Rhubarb mouth cues for WAV files (for clips that ship with the client, e.g. the Unity lip sync test).

    python tools/mouth_cues.py clip.wav [more.wav ...] [--text "what is said"] [--fast]

Writes clip.json next to each: {"mouth": [[start_s, shape], ...]}, as the server's opponent_line "mouth" and
POST /lipsync. Uses Rhubarb's English recognizer (better, especially with --text) unless --fast.
"""
from __future__ import annotations

import argparse
import asyncio
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from freestyle import lipsync  # noqa: E402


async def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("wavs", nargs="+", type=Path)
    ap.add_argument("--text", help="what the clips say (helps the English recognizer)")
    ap.add_argument("--fast", action="store_true", help="the phonetic recognizer, as the server uses live")
    args = ap.parse_args()
    if not lipsync.available:
        sys.exit("Rhubarb not found: ./setup.sh --rhubarb (or set RHUBARB)")
    for wav in args.wavs:
        cues = await lipsync.mouth_cues(wav.read_bytes(), args.text, "phonetic" if args.fast else "pocketSphinx",
                                        timeout_s=300)   # the English recognizer is slower than real time
        if cues is None:
            print(f"{wav}: failed", file=sys.stderr)
            continue
        out = wav.with_suffix(".json")
        out.write_text(json.dumps({"mouth": cues}))
        print(f"{wav} -> {out.name}: {len(cues)} cues, {''.join(c[1] for c in cues)}")


if __name__ == "__main__":
    asyncio.run(main())
