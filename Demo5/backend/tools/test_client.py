"""Stand-in for the Quest client: streams a WAV (or a laptop mic) to the server and runs the game.

    python tools/test_client.py --wav out/tight.wav --bpm 90 --bars 8
    python tools/test_client.py --mic --bpm 90            # needs `pip install sounddevice`

Behaves like the headset: the mic stream never stops; the client decides the turns (the user's bars start on a
bar line, after a count-in in round one) and runs the game rules (freestyle/game.py) on the server's ratings;
the opponent's verse is "played" for its bar length, and its ratings are applied as its lines finish.
"""
from __future__ import annotations

import argparse
import asyncio
import json
import math
import sys
import time
import wave
from pathlib import Path

import numpy as np
import websockets

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from freestyle import mcs  # noqa: E402
from freestyle.game import DEAD_AIR_S, Game  # noqa: E402

CHUNK_S = 0.02
COUNT_IN_BARS = 2


def load_wav(path: str) -> tuple[np.ndarray, int]:
    with wave.open(path) as f:
        assert f.getsampwidth() == 2, "16-bit PCM WAV only"
        sr, ch = f.getframerate(), f.getnchannels()
        x = np.frombuffer(f.readframes(f.getnframes()), dtype="<i2")
    if ch > 1:
        x = x.reshape(-1, ch).mean(axis=1).astype("<i2")
    return x, sr


def bar(v, width=20) -> str:
    v = 0 if v is None else max(0.0, min(1.0, v))
    return "#" * int(v * width) + "." * (width - int(v * width))


async def run(a) -> None:
    clip, sr = (load_wav(a.wav) if a.wav else (None, 16000))
    mic_q: asyncio.Queue[bytes] = asyncio.Queue()
    if a.mic:
        import sounddevice as sd
        loop = asyncio.get_running_loop()
        stream = sd.InputStream(samplerate=sr, channels=1, dtype="int16", blocksize=int(sr * CHUNK_S),
                                callback=lambda d, *_: loop.call_soon_threadsafe(mic_q.put_nowait, bytes(d)))
        stream.start()

    bar_s = 4 * 60 / a.bpm
    clip_at: list[int] = []  # sample positions where the clip should start
    sent = 0                 # samples sent: the mic timeline is sent / sr
    done = asyncio.Event()
    inbox: dict[str, asyncio.Queue] = {k: asyncio.Queue() for k in ("ready", "listen_done", "opponent_verse")}
    opp_ratings: list[dict] = []
    game: Game | None = None
    last_metrics = time.perf_counter()

    auth = {"Authorization": f"Bearer {a.password}"} if a.password else None
    async with websockets.connect(a.url, max_size=None, additional_headers=auth) as ws:
        send = lambda m: ws.send(json.dumps(m))  # noqa: E731
        await send({"type": "start", "sample_rate": sr, "mc": a.mc, "assist": a.assist,
                    "beat": {"bpm": a.bpm, "offset_ms": 0, "beats_per_bar": 4}})

        async def stream_audio() -> None:
            nonlocal sent
            n = int(sr * CHUNK_S)
            t0 = time.perf_counter()
            while not done.is_set():
                if a.mic:
                    data = await mic_q.get()
                else:
                    buf = np.zeros(n, dtype="<i2")
                    for start in clip_at:
                        lo, hi = max(sent, start), min(sent + n, start + len(clip))
                        if lo < hi:
                            buf[lo - sent: hi - sent] = clip[lo - start: hi - start]
                    data = buf.tobytes()
                await ws.send(data)
                sent += len(data) // 2
                lag = sent / sr / a.speed - (time.perf_counter() - t0)   # real time (or faster with --speed)
                if lag > 0:
                    await asyncio.sleep(lag)

        def show_rating(m: dict, points: float) -> None:
            c, st = m["crowd"], game.state()
            who = "you" if m["performer"] == "user" else "opp"
            print(f"  [{who}] \"{m['chunk']['text']}\"" + (f"  WORD DROP: {', '.join(m['word_drops'])}" if m.get("word_drops") else ""))
            print(f"      crowd {st['crowd']['level']:5.2f}/11 {bar(st['crowd']['level'] / 11, 11)} {st['crowd']['mood']:14} "
                  f"(pop {c['reaction']:.1f}, base {st['crowd']['base']:.1f}, punch {c['punchline']:.2f}) +{points:.0f} pts"
                  f" | opp {st['opponent']['mood']} {m['opponent']['gesture']} intim {st['opponent']['intimidated']:.1f} "
                  f"conf {st['opponent']['confident']:.1f} | {st['score']['user']:.0f}-{st['score']['opponent']:.0f}"
                  f" ({c['source']}, {c['latency_ms']}ms)")

        async def read() -> None:
            nonlocal last_metrics
            async for raw in ws:
                m = json.loads(raw)
                k = m["type"]
                if k in inbox:
                    inbox[k].put_nowait(m)
                if k == "word_drops":
                    print("  word drops:", ", ".join(m["words"]))
                elif k == "hints" and a.verbose:
                    print(f"  rhymes for {m['word']}: {', '.join(m['rhymes'][:6])}")
                elif k == "transcript" and m["committed"]:
                    print("  words:", " ".join(w["w"] for w in m["committed"]))
                elif k == "metrics":
                    now = time.perf_counter()
                    if game:   # dead air drains the room; the pop fades
                        game.tick((now - last_metrics) * a.speed, m["silence_s"] >= DEAD_AIR_S)
                    last_metrics = now
                    if a.verbose:
                        w = m["window"]
                        print(f"  t={m['t']:6.2f} syl/s={w['syllable_rate']:.1f} pocket {bar(w['pocket'])} "
                              f"on_beat={w['on_beat']} rhyme={m['lyrics']['rhyme_density']:.2f}")
                elif k == "rating":
                    if m["performer"] == "user":
                        show_rating(m, game.apply_rating(m, bar_s))
                        await send(game.context())
                    else:
                        opp_ratings.append(m)   # applied as its lines finish playing
                elif k == "error":
                    print("  ERROR:", m["message"])

        reader = asyncio.create_task(read())
        ready = await inbox["ready"].get()
        print(f"ready: rater={ready['rater']} opponent={ready['opponent']} transcriber={ready['transcriber']}")
        game = Game(mcs.get(ready["mc"]["id"]), assist=a.assist)
        streamer = asyncio.create_task(stream_audio())

        async def wait_mic(t: float) -> None:   # until t seconds of mic audio are sent
            while sent / sr < t:
                await asyncio.sleep(0.01)

        for rnd in range(1, a.rounds + 1):
            game.round, game.turn = rnd, "user"
            # the user's bars start on a bar line: after the count-in in round one, else on the next one
            now = sent / sr
            t = COUNT_IN_BARS * bar_s if rnd == 1 and now + 0.5 <= COUNT_IN_BARS * bar_s \
                else math.ceil((now + 0.5) / bar_s) * bar_s
            print(f"\n=== round {rnd}: user turn starts at t={t:.2f}s")
            await send({"type": "listen", "t": t, "round": rnd, **{k: v for k, v in game.context().items() if k != "type"}})
            if clip is not None:
                clip_at.append(int(round(t * sr)))
            await wait_mic(t + a.bars * bar_s + 0.5)
            await send({"type": "listen_end"})
            game.turn = "judging"
            print(f"  verse: {(await inbox['listen_done'].get())['verse']}")

            print(f"\n=== round {rnd}: opponent turn")
            game.turn = "opponent"
            opp_ratings.clear()
            await send(game.verse_request(a.bars))
            v = await inbox["opponent_verse"].get()
            print(f"  opponent ({v['source']}): {v['interpretation']}")
            for line in v["lines"]:
                print("    >", line)
            n, applied = len(v["lines"]), set()

            def release(lines_done: int) -> None:
                for m in opp_ratings:
                    if id(m) not in applied and m["chunk"]["lines"][1] <= lines_done:
                        applied.add(id(m))
                        show_rating(m, game.apply_rating(m, bar_s))
            for i in range(n):   # "play" the verse a bar per line, applying ratings as their lines finish
                await asyncio.sleep(bar_s / a.speed)
                release(i + 1)
            deadline = time.perf_counter() + 5
            while len(applied) < math.ceil(n / 2) and time.perf_counter() < deadline:
                await asyncio.sleep(0.05)
                release(n)

        game.turn = "idle"
        s = game.state()
        print(f"\nRESULT: {game.winner} wins, {s['score']['user']:.1f} to {s['score']['opponent']:.1f}, crowd {s['crowd']['mood']}")
        done.set()
        streamer.cancel()
        reader.cancel()


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", default="ws://localhost:8000/ws/battle")
    ap.add_argument("--wav")
    ap.add_argument("--mic", action="store_true")
    ap.add_argument("--mc", default=None, help="opponent id (GET /mcs); default MC Static")
    ap.add_argument("--bpm", type=float, default=90)
    ap.add_argument("--bars", type=int, default=8)
    ap.add_argument("--rounds", type=int, default=2)
    ap.add_argument("--speed", type=float, default=1.0, help=">1 streams faster than real time")
    ap.add_argument("--assist", action="store_true", help="rhyme hints, word drops, a kinder crowd")
    ap.add_argument("--password", help="the server's FREESTYLE_PASSWORD; '-' asks for it")
    ap.add_argument("-v", "--verbose", action="store_true")
    a = ap.parse_args()
    assert a.wav or a.mic, "pass --wav FILE or --mic"
    if a.password == "-":
        import getpass
        a.password = getpass.getpass("Server password: ")
    asyncio.run(run(a))


if __name__ == "__main__":
    main()
