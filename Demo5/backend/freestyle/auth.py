"""One shared password in front of the whole server (FREESTYLE_PASSWORD; unset = open, for local use).

The password is typed in each time and never stored by a client:

- Browser: GET / without a session redirects to /login, a form; the right password sets a session cookie
  (no expiry, so it ends when the browser closes) holding a token derived from the password, not the
  password itself. The cookie rides along on every fetch and on the battle WebSocket, so the GUI needs no
  changes.
- Headset and tools: `Authorization: Bearer <password>` on every request, WebSocket handshake included.

Anything else gets 401 (HTTP) or a refused WebSocket handshake (403). Five different wrong passwords from
one address within a minute lock that address out for a minute (a client repeating the same typo on each of
its requests counts once). Changing the password ends every session.
"""
from __future__ import annotations

import hashlib
import hmac
import html
import time
from http.cookies import SimpleCookie
from urllib.parse import parse_qs

COOKIE = "fs_session"
LOGIN = "/login"
MAX_FAILS = 5
LOCKOUT_S = 60.0

ERRORS = {"1": "Wrong password.", "2": "Too many wrong passwords. Wait a minute and try again."}

PAGE = """<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Freestyle Simulator</title>
<style>
  body {{ margin: 0; min-height: 100vh; display: grid; place-items: center; background: #14121a; color: #eee;
         font: 18px system-ui, sans-serif; }}
  form {{ display: grid; gap: 14px; width: min(320px, 90vw); }}
  h1 {{ font-size: 24px; margin: 0 0 6px; }}
  input, button {{ font: inherit; padding: 12px; border-radius: 8px; border: 1px solid #444; }}
  input {{ background: #221f2b; color: #eee; }}
  button {{ background: #e94f37; color: #fff; border: 0; cursor: pointer; }}
  .err {{ color: #ff8c7a; margin: 0; }}
</style></head>
<body><form method="post" action="/login">
  <h1>Freestyle Simulator</h1>
  {error}
  <input type="password" name="password" placeholder="Password" autocomplete="off" autofocus required>
  <button type="submit">Enter</button>
</form></body></html>
"""


async def _respond(send, status: int, body: bytes = b"", headers: list[tuple[bytes, bytes]] | None = None) -> None:
    await send({"type": "http.response.start", "status": status,
                "headers": [(b"content-length", str(len(body)).encode()), *(headers or [])]})
    await send({"type": "http.response.body", "body": body})


class PasswordGate:
    """ASGI middleware (it sees WebSockets too, which FastAPI's HTTP middleware doesn't)."""

    def __init__(self, app, password: str):
        self.app = app
        self.password = password
        self.token = hmac.new(password.encode(), b"freestyle session", hashlib.sha256).hexdigest() if password else ""
        self.fails: dict[str, dict[str, float]] = {}   # address -> {hash of a wrong password: when last tried}

    async def __call__(self, scope, receive, send):
        if not self.password or scope["type"] not in ("http", "websocket"):
            return await self.app(scope, receive, send)
        headers = {k.decode("latin-1").lower(): v.decode("latin-1") for k, v in scope["headers"]}
        if scope["type"] == "http" and scope["path"] == LOGIN:
            return await self._login(scope, receive, send, headers)
        if self._authorized(scope, headers):
            return await self.app(scope, receive, send)
        if scope["type"] == "websocket":
            await receive()   # websocket.connect; closing before accepting refuses the handshake
            return await send({"type": "websocket.close", "code": 1008})
        if scope["method"] == "GET" and scope["path"] == "/":
            return await _respond(send, 303, headers=[(b"location", LOGIN.encode())])
        return await _respond(send, 401, b"password required\n",
                              [(b"content-type", b"text/plain"), (b"www-authenticate", b"Bearer")])

    def _authorized(self, scope, headers: dict[str, str]) -> bool:
        cookie = SimpleCookie(headers.get("cookie", "")).get(COOKIE)
        if cookie and hmac.compare_digest(cookie.value, self.token):
            return True
        auth = headers.get("authorization", "")
        if not auth.startswith("Bearer "):
            return False
        return self._check(self._address(scope, headers), auth[len("Bearer "):])

    def _recent(self, addr: str) -> dict[str, float]:
        now = time.monotonic()
        recent = {h: t for h, t in self.fails.get(addr, {}).items() if now - t < LOCKOUT_S}
        if recent:
            self.fails[addr] = recent
        else:
            self.fails.pop(addr, None)
        return recent

    def locked(self, addr: str) -> bool:
        return len(self._recent(addr)) >= MAX_FAILS

    def _check(self, addr: str, password: str) -> bool:
        """The password, unless this address is locked out; wrong ones count toward the lockout."""
        if self.locked(addr):
            return False
        if hmac.compare_digest(password.encode(), self.password.encode()):
            self.fails.pop(addr, None)
            return True
        wrong = hashlib.sha256(password.encode()).hexdigest()
        self.fails.setdefault(addr, {})[wrong] = time.monotonic()
        return False

    @staticmethod
    def _address(scope, headers: dict[str, str]) -> str:
        # Behind the Cloudflare tunnel every request comes from localhost; the real client is in these headers.
        return (headers.get("cf-connecting-ip") or headers.get("x-forwarded-for", "").split(",")[0].strip()
                or (scope.get("client") or ("?",))[0])

    async def _login(self, scope, receive, send, headers: dict[str, str]) -> None:
        if scope["method"] != "POST":
            err = parse_qs(scope.get("query_string", b"").decode()).get("e", [""])[0]
            msg = f'<p class="err">{html.escape(ERRORS[err])}</p>' if err in ERRORS else ""
            return await _respond(send, 200, PAGE.format(error=msg).encode(),
                                  [(b"content-type", b"text/html; charset=utf-8"), (b"cache-control", b"no-store")])
        body = b""
        while True:   # a small form; anything bigger isn't ours
            msg = await receive()
            body += msg.get("body", b"")
            if len(body) > 4096:
                return await _respond(send, 413)
            if not msg.get("more_body"):
                break
        password = parse_qs(body.decode("utf-8", "replace")).get("password", [""])[0]
        addr = self._address(scope, headers)
        if self.locked(addr):
            return await _respond(send, 303, headers=[(b"location", f"{LOGIN}?e=2".encode())])
        if not self._check(addr, password):
            return await _respond(send, 303, headers=[(b"location", f"{LOGIN}?e=1".encode())])
        https = scope.get("scheme") == "https" or headers.get("x-forwarded-proto") == "https"
        cookie = f"{COOKIE}={self.token}; Path=/; HttpOnly; SameSite=Strict" + ("; Secure" if https else "")
        await _respond(send, 303, headers=[(b"location", b"/"), (b"set-cookie", cookie.encode())])
