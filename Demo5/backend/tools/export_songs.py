"""Analyse songs offline, for clients that bring their own tracks (the headset's bundled songs).

    python tools/export_songs.py                          # backend/songs -> songs.json
    python tools/export_songs.py track1.mp3 more/ -o ../unity/Assets/Songs/songs.json

Writes a JSON list in the same format as `GET /songs` (without `url`): tempo, downbeat, drop, loop, breaks and
phrases for each file; songs marked up in the web GUI (songs/markup.json) use their marks. In Unity, put it in the coordinator's Local Song Info, and the tracks themselves (same file names)
in Local Songs. Re-run it when a track changes or the analysis does (songs.VERSION).
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from freestyle import songs  # noqa: E402


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("paths", nargs="*", type=Path, help="audio files or folders (default: backend/songs)")
    ap.add_argument("-o", "--out", type=Path, default=Path("songs.json"))
    a = ap.parse_args()
    files: list[Path] = []
    for p in a.paths or [songs.SONGS]:
        files += sorted(f for f in p.iterdir() if f.suffix.lower() in songs.EXTS) if p.is_dir() else [p]
    out, markup = [], songs.load_markup()
    for f in files:
        print(f"analysing {f.name} ...", file=sys.stderr)
        try:
            out.append(songs.apply_markup(songs.describe(f, songs.analyse(f)), markup.get(f.name)))
        except Exception as e:   # unreadable file: skip it, keep the rest
            print(f"  skipped: {e!r}", file=sys.stderr)
            continue
        s = out[-1]
        print(f"  {s['bpm']:g} BPM, drop {s['drop_s']:.2f} s, {s['loop_bars']} bars, {len(s['breaks'])} breaks", file=sys.stderr)
    a.out.write_text(json.dumps(out, indent=1))
    print(f"wrote {a.out} ({len(out)} songs)", file=sys.stderr)


if __name__ == "__main__":
    main()
