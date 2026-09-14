#!/usr/bin/env python3
"""Always-on cloud admin auth for /diangexitong (login without Windows client)."""

from __future__ import annotations

import json
import secrets
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlparse

HOST = "127.0.0.1"
PORT = 15089
ACCOUNTS_PATH = Path("/opt/liveassistant/www/admin-accounts.json")
SESSION_HOURS = 12


class SessionStore:
    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._sessions: dict[str, float] = {}

    def create(self) -> str:
        token = secrets.token_hex(32)
        exp = time.time() + SESSION_HOURS * 3600
        with self._lock:
            self._sessions[token] = exp
            self._gc_locked()
        return token

    def valid(self, token: str | None) -> bool:
        if not token:
            return False
        now = time.time()
        with self._lock:
            self._gc_locked(now)
            exp = self._sessions.get(token)
            return bool(exp and exp >= now)

    def _gc_locked(self, now: float | None = None) -> None:
        now = time.time() if now is None else now
        dead = [k for k, exp in self._sessions.items() if exp < now]
        for k in dead:
            self._sessions.pop(k, None)


SESSIONS = SessionStore()


def load_accounts() -> list[dict]:
    data = json.loads(ACCOUNTS_PATH.read_text(encoding="utf-8"))
    return list(data.get("accounts") or [])


def check_password(username: str, password: str) -> bool:
    for row in load_accounts():
        if str(row.get("username") or "") == username and str(row.get("password") or "") == password:
            return True
    return False


class Handler(BaseHTTPRequestHandler):
    server_version = "LiveAssistantAuth/1.0"

    def log_message(self, fmt: str, *args) -> None:
        return

    def _json(self, code: int, payload: dict) -> None:
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self) -> None:  # noqa: N802
        path = urlparse(self.path).path
        if path != "/login":
            self._json(404, {"ok": False, "message": "not found"})
            return
        length = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(length) if length > 0 else b"{}"
        try:
            data = json.loads(raw.decode("utf-8"))
        except Exception:
            self._json(400, {"ok": False, "message": "invalid json"})
            return
        username = str(data.get("username") or "").strip()
        password = str(data.get("password") or "")
        try:
            if not check_password(username, password):
                self._json(200, {"ok": False, "message": "用户名或密码错误"})
                return
        except Exception:
            self._json(500, {"ok": False, "message": "账号配置读取失败"})
            return
        token = SESSIONS.create()
        self._json(200, {"ok": True, "token": token})

    def do_GET(self) -> None:  # noqa: N802
        path = urlparse(self.path).path
        if path == "/healthz":
            self._json(200, {"ok": True})
            return
        if path != "/verify":
            self._json(404, {"ok": False, "message": "not found"})
            return
        auth = self.headers.get("Authorization") or ""
        token = auth[7:].strip() if auth.lower().startswith("bearer ") else ""
        if SESSIONS.valid(token):
            self.send_response(200)
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        self.send_response(401)
        self.send_header("Content-Length", "0")
        self.end_headers()


def main() -> int:
    server = ThreadingHTTPServer((HOST, PORT), Handler)
    server.serve_forever()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
