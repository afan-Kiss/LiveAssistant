#!/usr/bin/env python3
"""Stage tunnel into appsettings, publish single-file EXE, restore sources."""

from __future__ import annotations

import json
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PROJ = ROOT / "LiveAssistant"
OUT = ROOT / "publish" / "LiveAssistant-one"
CRED = ROOT / "Config" / "DeployCredentials.local.json"
APPSETTINGS = PROJ / "Config" / "appsettings.json"
CRED_DST = PROJ / "Config" / "DeployCredentials.local.json"
BACKUP = PROJ / "Config" / "appsettings.json.bak-publish"


def main() -> int:
    if not CRED.exists():
        print(f"Missing {CRED}", file=sys.stderr)
        return 1

    cred = json.loads(CRED.read_text(encoding="utf-8-sig"))
    server = cred.get("server") or {}
    if not server.get("host") or not server.get("password"):
        print("server.host/password missing in DeployCredentials.local.json", file=sys.stderr)
        return 1

    original = APPSETTINGS.read_text(encoding="utf-8")
    BACKUP.write_text(original, encoding="utf-8")
    shutil.copy2(CRED, CRED_DST)

    try:
        settings = json.loads(original)
        tunnel = settings.setdefault("adminTunnel", {})
        tunnel["enabled"] = True
        tunnel["host"] = server["host"]
        tunnel["user"] = server.get("user") or "root"
        tunnel["password"] = server["password"]
        tunnel["remotePort"] = int(server.get("adminTunnelPort") or 15088)
        tunnel["localPort"] = int(server.get("adminLocalPort") or 5088)
        tunnel["credentialsFile"] = "DeployCredentials.local.json"
        tunnel["reconnectDelayMs"] = int(tunnel.get("reconnectDelayMs") or 5000)
        APPSETTINGS.write_text(
            json.dumps(settings, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )

        if OUT.exists():
            shutil.rmtree(OUT, ignore_errors=True)
        OUT.mkdir(parents=True, exist_ok=True)

        cmd = [
            "dotnet",
            "publish",
            str(PROJ / "LiveAssistant.csproj"),
            "-c",
            "Release",
            "-r",
            "win-x64",
            "--self-contained",
            "true",
            "-o",
            str(OUT),
            "-p:PublishSingleFile=true",
            "-p:IncludeNativeLibrariesForSelfExtract=true",
            "-p:EnableCompressionInSingleFile=true",
            "-p:IncludeAllContentForSelfExtract=true",
            "-p:DebugType=None",
            "-p:DebugSymbols=false",
            "--nologo",
        ]
        print(" ".join(cmd))
        subprocess.run(cmd, check=True)
    finally:
        if BACKUP.exists():
            APPSETTINGS.write_text(BACKUP.read_text(encoding="utf-8"), encoding="utf-8")
            BACKUP.unlink(missing_ok=True)
        if CRED_DST.exists():
            CRED_DST.unlink()

    exe = OUT / "LiveAssistant.exe"
    if not exe.exists():
        print("publish missing exe", file=sys.stderr)
        return 1
    api_cmd = ROOT / "启动抖音API.cmd"
    if api_cmd.exists():
        shutil.copy2(api_cmd, OUT / "启动抖音API.cmd")

    sync_sidecars = ROOT / "scripts" / "sync-sidecars.py"
    if sync_sidecars.exists():
        print("sync sidecars...")
        subprocess.run([sys.executable, str(sync_sidecars)], check=False)

    print(f"OK: {exe}")
    print(f"SIZE_MB: {exe.stat().st_size / 1024 / 1024:.1f}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
