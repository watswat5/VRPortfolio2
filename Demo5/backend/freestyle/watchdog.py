"""Event-loop stall watchdog: says what the server was doing when it stopped answering.

A task on the event loop ticks a heartbeat every 0.1 s; a thread checks it. When the heartbeat is older than
STALL_S (the loop is blocked: nothing is served, the websocket and /clip requests wait), the thread logs the loop
thread's stack, the top of every other thread's (the executor's voice renders, ...) and the CPU the container was
throttled for meanwhile (cgroup cpu.stat: Cloud Run limits CPU by quota). When the loop comes back it logs how long
the stall lasted. Logging only; WATCHDOG=0 turns it off.
"""

from __future__ import annotations

import asyncio
import logging
import os
import sys
import threading
import time
import traceback
from pathlib import Path

log = logging.getLogger("freestyle.watchdog")

TICK_S = 0.1
STALL_S = 1.0          # the heartbeat this old: a stall
CPU_STAT = Path("/sys/fs/cgroup/cpu.stat")


def _throttled_us() -> int | None:
    try:
        for line in CPU_STAT.read_text().splitlines():
            k, v = line.split()
            if k == "throttled_usec":
                return int(v)
    except (OSError, ValueError):
        pass
    return None


def _where(frame, depth: int) -> str:
    return " <- ".join(f"{Path(f.filename).name}:{f.lineno} {f.name}"
                       for f in reversed(traceback.extract_stack(frame)[-depth:]))


def start(loop: asyncio.AbstractEventLoop) -> None:
    """Starts the heartbeat (on `loop`) and the watching thread. Call from the loop's thread."""
    if os.environ.get("WATCHDOG", "1").strip() == "0":
        return
    loop_thread = threading.get_ident()
    beat = [time.monotonic()]

    async def heartbeat() -> None:
        while True:
            beat[0] = time.monotonic()
            await asyncio.sleep(TICK_S)

    def watch() -> None:
        stalled_at, throttled0, cpu0 = None, None, 0.0
        while True:
            time.sleep(TICK_S)
            age = time.monotonic() - beat[0]
            if stalled_at is None and age > STALL_S:
                stalled_at, throttled0, cpu0 = beat[0], _throttled_us(), time.process_time()
                frames = sys._current_frames()
                main = frames.get(loop_thread)
                others = []
                for t in threading.enumerate():
                    f = frames.get(t.ident)
                    if f is None or t.ident in (loop_thread, threading.get_ident()):
                        continue
                    if Path(f.f_code.co_filename).name in ("threading.py", "queue.py", "selectors.py"):
                        continue   # idle: waiting for work
                    others.append(f"{t.name}: {_where(f, 4)}")
                log.warning("event loop stalled %.1f s; the loop is at:\n%s\nother threads:\n  %s",
                            age, "".join(traceback.format_stack(main)[-12:]) if main else "?",
                            "\n  ".join(others) or "(none busy)")
            elif stalled_at is not None and age < STALL_S:
                took = beat[0] - stalled_at
                t1 = _throttled_us()
                throttled = f", CPU throttled {(t1 - throttled0) / 1e6:.1f} s" if t1 is not None and throttled0 is not None else ""
                log.warning("event loop back after %.1f s (process CPU %.1f s%s)",
                            took, time.process_time() - cpu0, throttled)
                stalled_at = None

    loop.create_task(heartbeat())
    threading.Thread(target=watch, name="watchdog", daemon=True).start()
    log.info("watchdog on: logs event-loop stalls over %.1f s", STALL_S)
