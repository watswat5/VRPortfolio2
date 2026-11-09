"""Mouth cues for the opponent's lines: Rhubarb Lip Sync (MIT, a command-line tool) on the rendered WAV.

Rhubarb hears the line and gives timed mouth shapes, Preston Blair's set:
  A closed (P B M)   B teeth together (K S T, EE)   C open (EH AE)   D wide open (AA)
  E rounded (AO ER)  F puckered (UW OW W)           G F/V            H L (tongue up)   X rest
Clients map them onto their avatar's visemes (Rocketbox: A PP, B SS, C E, D aa, E O, F U, G FF, H nn, X rest).

Cues go out as [[start_s, shape], ...]: each shape holds until the next one starts; the last is X.

The phonetic recognizer is quick (about a sixth of the line's length) and fine for any language; pocketSphinx
(English) is a little better, especially given the line's text, but slower than real time, so it's for baking
cues offline (tools/mouth_cues.py), not live. Without Rhubarb (third_party/, ./setup.sh --rhubarb) everything
still works: cues are None and clients fall back to their own lip sync.
"""
from __future__ import annotations

import asyncio
import json
import logging
import os
import shutil
import tempfile
from pathlib import Path

log = logging.getLogger("freestyle.lipsync")

ROOT = Path(__file__).resolve().parents[2]
TIMEOUT_S = 10.0


def find_rhubarb() -> str | None:
    """RHUBARB (a path), else third_party/Rhubarb-Lip-Sync-*/rhubarb, else rhubarb on the PATH."""
    env = os.environ.get("RHUBARB", "").strip()
    if env:
        return env if Path(env).is_file() else None
    for p in sorted((ROOT / "third_party").glob("Rhubarb-Lip-Sync-*/rhubarb"), reverse=True):
        return str(p)
    return shutil.which("rhubarb")


RHUBARB = find_rhubarb()
available = RHUBARB is not None


def _args(path: str, recognizer: str, dialog: str | None) -> list[str]:
    args = [RHUBARB, "-q", "-f", "json", "-r", recognizer, "--extendedShapes", "GHX", "--threads", "1", path]
    if dialog:
        args[1:1] = ["-d", dialog]
    return args


def _cues(out: bytes) -> list[list]:
    return [[round(c["start"], 3), c["value"]] for c in json.loads(out)["mouthCues"]]


_slots: dict[int, asyncio.Semaphore] = {}   # per event loop (the render tools run one loop per line)


def _cores() -> int:
    """CPUs this process may really use: RHUBARB_JOBS if set, else the container's CPU quota (Cloud Run limits
    CPU by quota, so the machine's core count would overstate it), else the cores it may run on."""
    if os.environ.get("RHUBARB_JOBS", "").strip().isdigit():
        return max(1, int(os.environ["RHUBARB_JOBS"]))
    try:
        quota, period = Path("/sys/fs/cgroup/cpu.max").read_text().split()[:2]
        if quota != "max":
            return max(1, int(-(-int(quota) // int(period))))
    except (OSError, ValueError):
        pass
    try:
        return max(1, len(os.sched_getaffinity(0)))
    except AttributeError:
        return max(1, os.cpu_count() or 1)


async def mouth_cues(wav: bytes, text: str | None = None, recognizer: str = "phonetic",
                     timeout_s: float = TIMEOUT_S) -> list[list] | None:
    """[[start_s, shape], ...] for this WAV, or None (no Rhubarb, or it failed).

    One Rhubarb (single-threaded) per CPU core at a time, the rest wait their turn: a verse's lines all at once
    on a 1-vCPU cloud instance had each one run past the timeout, and starved the server of the CPU it needs to
    answer requests. The timeout counts a run, not the wait."""
    if not available:
        return None
    loop = id(asyncio.get_running_loop())
    if loop not in _slots:
        _slots.clear()
        _slots[loop] = asyncio.Semaphore(_cores())
    async with _slots[loop]:
        return await _run(wav, text, recognizer, timeout_s)


async def _run(wav: bytes, text: str | None, recognizer: str, timeout_s: float) -> list[list] | None:
    with tempfile.TemporaryDirectory() as d:
        path = os.path.join(d, "line.wav")
        Path(path).write_bytes(wav)
        dialog = None
        if text and recognizer == "pocketSphinx":   # the phonetic recognizer ignores the text
            dialog = os.path.join(d, "line.txt")
            Path(dialog).write_text(text)
        try:
            proc = await asyncio.create_subprocess_exec(*_args(path, recognizer, dialog), stdout=asyncio.subprocess.PIPE,
                                                        stderr=asyncio.subprocess.PIPE)
            out, err = await asyncio.wait_for(proc.communicate(), timeout_s)
        except asyncio.TimeoutError:
            proc.kill()
            await proc.wait()
            log.warning("rhubarb took over %.0f s", timeout_s)
            return None
        except OSError as e:
            log.warning("rhubarb failed: %r", e)
            return None
        if proc.returncode:
            log.warning("rhubarb failed (%d): %s", proc.returncode, err.decode(errors="replace")[-300:])
            return None
        return _cues(out)
