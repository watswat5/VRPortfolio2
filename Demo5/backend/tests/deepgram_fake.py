"""A stand-in for Deepgram's streaming API (wss://api.deepgram.com/v1/listen), for tests.

It checks the auth header, records the query and the audio, and answers like Deepgram: interim results for the
words heard so far, a final result once a word is well behind, and the rest on Finalize. Word times are relative to
the start of the audio on the connection, as Deepgram's are. `script` is [(word, start_s, end_s), ...] per connection.
"""
from __future__ import annotations

import asyncio
import json
import threading
from urllib.parse import parse_qs, urlparse

from websockets.asyncio.server import serve

SR = 16000


class FakeDeepgram:
    def __init__(self, script, key="test-key"):
        self.script, self.key = script, key
        self.connections: list[dict] = []      # {"query", "auth", "audio_s", "messages"}
        self.port = None

    async def handler(self, ws):
        c = {"query": parse_qs(urlparse(ws.request.path).query), "auth": ws.request.headers.get("Authorization"),
             "audio_s": 0.0, "messages": []}
        self.connections.append(c)
        if c["auth"] != f"Token {self.key}":
            await ws.close(1008, "bad key")
            return
        sent_final = 0

        def result(words, final, from_finalize=False):
            return json.dumps({"type": "Results", "is_final": final, "from_finalize": from_finalize,
                               "channel": {"alternatives": [{"transcript": " ".join(w for w, _, _ in words),
                                                             "words": [{"word": w, "start": s, "end": e, "confidence": 0.9,
                                                                        "punctuated_word": w} for w, s, e in words]}]}})
        async for msg in ws:
            if isinstance(msg, bytes):
                c["audio_s"] += len(msg) / 2 / SR
                heard = [w for w in self.script if w[2] <= c["audio_s"]]
                done = [w for w in heard[sent_final:] if w[2] <= c["audio_s"] - 0.4]   # well behind: final
                if done:
                    await ws.send(result(done, True))
                    sent_final += len(done)
                elif len(heard) > sent_final:
                    await ws.send(result(heard[sent_final:], False))
            else:
                m = json.loads(msg)
                c["messages"].append(m["type"])
                if m["type"] == "Finalize":
                    await ws.send(result([w for w in self.script if w[2] <= c["audio_s"]][sent_final:], True, True))
                    sent_final = len(self.script)
                elif m["type"] == "CloseStream":
                    await ws.close()
                    return

    def start(self) -> "FakeDeepgram":
        ready = threading.Event()

        async def main():
            async with serve(self.handler, "127.0.0.1", 0) as server:
                self.port = server.sockets[0].getsockname()[1]
                ready.set()
                await asyncio.Future()
        threading.Thread(target=lambda: asyncio.run(main()), daemon=True).start()
        ready.wait(5)
        return self

    @property
    def url(self) -> str:
        return f"ws://127.0.0.1:{self.port}/v1/listen"
