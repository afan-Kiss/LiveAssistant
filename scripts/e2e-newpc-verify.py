#!/usr/bin/env python3
"""End-to-end verify single-file EXE like a brand-new PC."""

from __future__ import annotations

import json
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
EXE_SRC = ROOT / "publish" / "LiveAssistant-one" / "LiveAssistant.exe"
TEST_DIR = ROOT / "publish" / "_e2e-newpc"


def http_json(url: str, method: str = "GET", body: dict | None = None, headers: dict | None = None, timeout: int = 10):
    data = None if body is None else json.dumps(body).encode("utf-8")
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Content-Type", "application/json")
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read().decode("utf-8", "ignore")
            try:
                parsed = json.loads(raw) if raw else {}
            except Exception:
                parsed = {"_raw": raw}
            return resp.status, parsed
    except urllib.error.HTTPError as e:
        raw = e.read().decode("utf-8", "ignore")
        try:
            parsed = json.loads(raw) if raw else {}
        except Exception:
            parsed = {"_raw": raw}
        return e.code, parsed


def wait_until(pred, seconds: float, step: float = 0.5) -> bool:
    deadline = time.time() + seconds
    while time.time() < deadline:
        if pred():
            return True
        time.sleep(step)
    return False


def main() -> int:
    failures: list[str] = []

    if not EXE_SRC.exists():
        print(f"FAIL missing {EXE_SRC}")
        return 1

    if TEST_DIR.exists():
        shutil.rmtree(TEST_DIR, ignore_errors=True)
    TEST_DIR.mkdir(parents=True)

    shutil.copy2(EXE_SRC, TEST_DIR / "LiveAssistant.exe")
    print(f"TEST_DIR={TEST_DIR}")

    # offline health before start
    code, body = http_json("https://xiangyuzhubao.xyz/diangexitong/health")
    if code != 503 or body.get("ok") is not False:
        # may still be online from leftover process; kill leftovers
        subprocess.run(["taskkill", "/F", "/IM", "LiveAssistant.exe"], capture_output=True)
        time.sleep(2)
        code, body = http_json("https://xiangyuzhubao.xyz/diangexitong/health")
    print(f"pre_health={code} {body}")

    proc = subprocess.Popen(
        [str(TEST_DIR / "LiveAssistant.exe")],
        cwd=str(TEST_DIR),
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    try:
        # extract check
        if not wait_until(lambda: (TEST_DIR / "Config" / "appsettings.json").exists(), 8):
            failures.append("extract appsettings timeout")
        if not (TEST_DIR / "Config" / "DeployCredentials.local.json").exists():
            failures.append("missing extracted DeployCredentials.local.json")
        if not (TEST_DIR / "Admin" / "wwwroot" / "index.html").exists():
            failures.append("missing extracted admin index.html")
        else:
            html = (TEST_DIR / "Admin" / "wwwroot" / "index.html").read_text(encoding="utf-8", errors="ignore")
            if "bindLoginKeys" not in html or "keydown" not in html:
                failures.append("admin html missing Enter-to-login")
            if "refreshRuntimeStatus" not in html:
                failures.append("admin html missing runtime status")

        # local ping
        if not wait_until(lambda: http_json("http://127.0.0.1:5088/diangexitong/api/ping", timeout=2)[0] == 200, 15):
            failures.append("local /api/ping not 200")
        else:
            print("local_ping=OK")

        # cloud health online via tunnel
        if not wait_until(lambda: http_json("https://xiangyuzhubao.xyz/diangexitong/health", timeout=5)[0] == 200, 25):
            failures.append("cloud health not online while client running")
        else:
            print("cloud_health_online=OK")

        # cloud login without depending on windows auth
        code, body = http_json(
            "https://xiangyuzhubao.xyz/diangexitong/api/auth/login",
            method="POST",
            body={"username": "fanfan", "password": "fanfan9724"},
        )
        if code != 200 or not body.get("ok") or not body.get("token"):
            failures.append(f"cloud login failed: {code} {body}")
        else:
            print("cloud_login=OK")
            token = body["token"]
            # status through tunnel
            sc, sb = http_json(
                "https://xiangyuzhubao.xyz/diangexitong/api/status",
                headers={"Authorization": f"Bearer {token}"},
            )
            if sc != 200:
                failures.append(f"cloud status failed: {sc} {sb}")
            else:
                print("cloud_status=OK")

        # close
        t0 = time.time()
        subprocess.run(["taskkill", "/IM", "LiveAssistant.exe"], capture_output=True)
        # prefer graceful: already started; use taskkill without /F first then force
        try:
            proc.wait(timeout=8)
        except Exception:
            proc.kill()
            failures.append("process did not exit within 8s after close request")
        print(f"close_ms={int((time.time() - t0) * 1000)}")

        time.sleep(2)
        code, body = http_json("https://xiangyuzhubao.xyz/diangexitong/health")
        if code != 503:
            failures.append(f"cloud health should be offline after close, got {code} {body}")
        else:
            print("cloud_health_offline=OK")

    finally:
        if proc.poll() is None:
            proc.kill()
        subprocess.run(["taskkill", "/F", "/IM", "LiveAssistant.exe"], capture_output=True)

    if failures:
        print("E2E_FAIL")
        for f in failures:
            print(" -", f)
        return 1

    print("E2E_PASS")
    print(f"EXE={EXE_SRC}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
