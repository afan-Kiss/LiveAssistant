#!/usr/bin/env python3
"""Deploy cloud admin: static UI + always-on auth + API tunnel."""

from __future__ import annotations

import json
import sys
import time
from pathlib import Path

import paramiko

ROOT = Path(__file__).resolve().parents[1]
CRED = ROOT / "Config" / "DeployCredentials.local.json"
ADMIN_INDEX_LOCAL = ROOT / "LiveAssistant" / "Admin" / "wwwroot" / "index.html"
HEALTH_OK_LOCAL = ROOT / "deploy" / "www" / "health-ok.json"
ACCOUNTS_LOCAL = ROOT / "deploy" / "www" / "admin-accounts.json"
AUTH_SERVER_LOCAL = ROOT / "scripts" / "admin-auth-server.py"
NGINX_SNIPPET = "/etc/nginx/snippets/liveassistant-diangexitong.conf"
TUNNEL_PORT = 15088
AUTH_PORT = 15089
TUNNEL_SECRET = "la-tunnel-9f3c2e8b7a1d4c6e"


def build_snippet(admin_dir: str, health_ok: str) -> str:
    admin_dir = admin_dir.rstrip("/") + "/"
    return f"""# LiveAssistant admin — cloud login always; API via reverse tunnel :{TUNNEL_PORT}
location = /diangexitong {{
    return 301 /diangexitong/;
}}

location = /diangexitong/health {{
    auth_request /diangexitong/health-upstream;
    error_page 401 403 500 502 503 = @diangexitong_health_down;
    default_type application/json;
    charset utf-8;
    add_header Cache-Control "no-store" always;
    alias {health_ok};
}}

location = /diangexitong/health-upstream {{
    internal;
    proxy_pass http://127.0.0.1:{TUNNEL_PORT}/diangexitong/api/ping;
    proxy_pass_request_body off;
    proxy_set_header Content-Length "";
    proxy_set_header Host $host;
    proxy_connect_timeout 1s;
    proxy_read_timeout 2s;
}}

location @diangexitong_health_down {{
    default_type application/json;
    charset utf-8;
    add_header Cache-Control "no-store" always;
    return 503 '{{"ok":false}}';
}}

location @diangexitong_api_down {{
    default_type application/json;
    charset utf-8;
    add_header Cache-Control "no-store" always;
    return 503 '{{"ok":false,"message":"24小时直播助手没运行"}}';
}}

location @diangexitong_unauthorized {{
    default_type application/json;
    charset utf-8;
    add_header Cache-Control "no-store" always;
    return 401 '{{"ok":false,"message":"未登录"}}';
}}

# Cloud auth — works even when Windows client is offline.
location = /diangexitong/api/auth/login {{
    proxy_pass http://127.0.0.1:{AUTH_PORT}/login;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header Content-Type $content_type;
    proxy_connect_timeout 3s;
    proxy_read_timeout 10s;
}}

location = /diangexitong/auth-verify {{
    internal;
    proxy_pass http://127.0.0.1:{AUTH_PORT}/verify;
    proxy_pass_request_body off;
    proxy_set_header Content-Length "";
    proxy_set_header Authorization $http_authorization;
    proxy_connect_timeout 2s;
    proxy_read_timeout 5s;
}}

location ^~ /diangexitong/api/ {{
    auth_request /diangexitong/auth-verify;
    error_page 401 = @diangexitong_unauthorized;
    proxy_pass http://127.0.0.1:{TUNNEL_PORT}/diangexitong/api/;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-LiveAssistant-Tunnel "{TUNNEL_SECRET}";
    proxy_set_header Authorization "";
    proxy_connect_timeout 2s;
    proxy_read_timeout 600s;
    client_max_body_size 20m;
    proxy_intercept_errors on;
    error_page 502 503 504 = @diangexitong_api_down;
}}

location ^~ /diangexitong/ {{
    alias {admin_dir};
    index index.html;
    charset utf-8;
    add_header Cache-Control "no-store";
}}
"""


SYSTEMD_UNIT = f"""[Unit]
Description=LiveAssistant cloud admin auth
After=network.target

[Service]
Type=simple
ExecStart=/usr/bin/python3 /opt/liveassistant/www/admin-auth-server.py
Restart=always
RestartSec=2
User=root

[Install]
WantedBy=multi-user.target
"""


def main() -> int:
    needed = [CRED, ADMIN_INDEX_LOCAL, HEALTH_OK_LOCAL, ACCOUNTS_LOCAL, AUTH_SERVER_LOCAL]
    if any(not p.exists() for p in needed):
        print("missing deploy files", file=sys.stderr)
        return 1

    # keep tunnel secret in accounts file aligned
    accounts = json.loads(ACCOUNTS_LOCAL.read_text(encoding="utf-8"))
    accounts["tunnelSecret"] = TUNNEL_SECRET
    if not accounts.get("accounts"):
        print("accounts empty", file=sys.stderr)
        return 1

    cred = json.loads(CRED.read_text(encoding="utf-8-sig"))
    host = cred["server"]["host"]
    user = cred["server"].get("user", "root")
    password = cred["server"]["password"]
    deploy_path = (cred["server"].get("deployPath") or "/opt/liveassistant").rstrip("/")
    remote_admin_dir = f"{deploy_path}/www/admin"
    remote_www = f"{deploy_path}/www"
    remote_admin = f"{remote_admin_dir}/index.html"
    remote_health_ok = f"{remote_www}/health-ok.json"
    remote_accounts = f"{remote_www}/admin-accounts.json"
    remote_auth = f"{remote_www}/admin-auth-server.py"
    snippet = build_snippet(remote_admin_dir, remote_health_ok)

    client = paramiko.SSHClient()
    client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    client.connect(
        host,
        username=user,
        password=password,
        timeout=20,
        allow_agent=False,
        look_for_keys=False,
    )
    sftp = client.open_sftp()

    def run(cmd: str) -> str:
        _, stdout, stderr = client.exec_command(cmd)
        out = stdout.read().decode("utf-8", "ignore")
        err = stderr.read().decode("utf-8", "ignore")
        code = stdout.channel.recv_exit_status()
        if code != 0:
            raise RuntimeError(f"{cmd}\n{err or out}")
        return out

    run(f"mkdir -p {remote_admin_dir}")
    sftp.put(str(ADMIN_INDEX_LOCAL), remote_admin)
    sftp.put(str(HEALTH_OK_LOCAL), remote_health_ok)
    sftp.put(str(AUTH_SERVER_LOCAL), remote_auth)
    with sftp.file(remote_accounts, "w") as f:
        f.write(json.dumps(accounts, ensure_ascii=False, indent=2) + "\n")
    # auth server hardcodes /opt/liveassistant/www path — ensure symlink/copy if deploy_path differs
    if deploy_path != "/opt/liveassistant":
        run("mkdir -p /opt/liveassistant/www")
        run(f"cp -f {remote_auth} /opt/liveassistant/www/admin-auth-server.py")
        run(f"cp -f {remote_accounts} /opt/liveassistant/www/admin-accounts.json")

    with sftp.file(NGINX_SNIPPET, "w") as f:
        f.write(snippet)
    with sftp.file("/etc/systemd/system/liveassistant-admin-auth.service", "w") as f:
        f.write(SYSTEMD_UNIT)

    run("nginx -t")
    run("systemctl daemon-reload")
    run("systemctl enable --now liveassistant-admin-auth.service")
    run("systemctl restart liveassistant-admin-auth.service")
    run("systemctl reload nginx")
    time.sleep(1)
    try:
        out = run(
            f"curl -sS -o /tmp/la-login.json -w '%{{http_code}}' -X POST http://127.0.0.1:{AUTH_PORT}/login "
            f"-H 'Content-Type: application/json' "
            f"-d '{{\"username\":\"fanfan\",\"password\":\"fanfan9724\"}}'"
        )
        print("login_smoke", out.strip(), run("cat /tmp/la-login.json").strip())
    except Exception as ex:
        print("login_smoke_warn", ex)

    sftp.close()
    client.close()
    print("OK")
    print("url=https://xiangyuzhubao.xyz/diangexitong/")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
