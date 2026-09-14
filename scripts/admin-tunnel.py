#!/usr/bin/env python3
"""Reverse-tunnel Windows LiveAssistant admin (5088) to cloud :15088."""

from __future__ import annotations

import json
import socket
import threading
from pathlib import Path

import paramiko

ROOT = Path(__file__).resolve().parents[1]
CRED = ROOT / "Config" / "DeployCredentials.local.json"


def pump(src, dst):
    try:
        while True:
            data = src.recv(65535)
            if not data:
                break
            dst.sendall(data)
    except Exception:
        pass
    finally:
        try:
            src.close()
        except Exception:
            pass
        try:
            dst.close()
        except Exception:
            pass


def handle(channel, local_port: int):
    try:
        sock = socket.create_connection(("127.0.0.1", local_port), timeout=5)
    except Exception as exc:
        print(f"local connect failed: {exc}")
        channel.close()
        return
    threading.Thread(target=pump, args=(channel, sock), daemon=True).start()
    threading.Thread(target=pump, args=(sock, channel), daemon=True).start()


def main() -> int:
    cred = json.loads(CRED.read_text(encoding="utf-8-sig"))
    host = cred["server"]["host"]
    user = cred["server"].get("user", "root")
    password = cred["server"]["password"]
    remote_port = int(cred["server"].get("adminTunnelPort") or 15088)
    local_port = int(cred["server"].get("adminLocalPort") or 5088)

    print(f"Tunnel {user}@{host} :{remote_port} -> 127.0.0.1:{local_port}")
    print("Public: https://xiangyuzhubao.xyz/diangexitong/")
    print("Keep this process running. Ctrl+C to stop.")

    client = paramiko.SSHClient()
    client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    client.connect(
        host,
        username=user,
        password=password,
        allow_agent=False,
        look_for_keys=False,
        timeout=20,
    )
    transport = client.get_transport()
    assert transport is not None
    transport.request_port_forward("", remote_port)
    try:
        while True:
            chan = transport.accept(timeout=1.0)
            if chan is None:
                if not transport.is_active():
                    break
                continue
            threading.Thread(target=handle, args=(chan, local_port), daemon=True).start()
    except KeyboardInterrupt:
        print("stopped")
    finally:
        try:
            transport.cancel_port_forward("", remote_port)
        except Exception:
            pass
        client.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
