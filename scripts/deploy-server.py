#!/usr/bin/env python3
"""Deploy LiveAssistant source + nginx /diangexitong reverse-proxy to Aliyun server."""

from __future__ import annotations

import json
import subprocess
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

import paramiko

ROOT = Path(__file__).resolve().parents[1]
CRED = ROOT / "Config" / "DeployCredentials.local.json"
REMOTE_ROOT = "/www/wwwroot/liveassistant"
NGINX_SNIPPET = "/etc/nginx/snippets/liveassistant-diangexitong.conf"
PORTAL_SSL = "/etc/nginx/conf.d/xiangyu-portal-ssl.conf"
PORTAL_HTTP = "/etc/nginx/conf.d/xiangyu-portal.conf"
TUNNEL_PORT = 15088
ADMIN_INDEX_LOCAL = ROOT / "LiveAssistant" / "Admin" / "wwwroot" / "index.html"
HEALTH_OK_LOCAL = ROOT / "deploy" / "www" / "health-ok.json"


def load_cred() -> dict:
    return json.loads(CRED.read_text(encoding="utf-8-sig"))


def build_archive(out_path: Path) -> None:
    subprocess.run(
        ["git", "-C", str(ROOT), "archive", "--format=tar", "-o", str(out_path), "HEAD"],
        check=True,
    )


def write_remote_text(sftp: paramiko.SFTPClient, path: str, content: str) -> None:
    with sftp.file(path, "w") as f:
        f.write(content)


def ensure_admin_web(client: paramiko.SSHClient, sftp: paramiko.SFTPClient, deploy_path: str) -> tuple[str, str]:
    root = deploy_path.rstrip("/")
    remote_admin_dir = f"{root}/www/admin"
    remote_admin = f"{remote_admin_dir}/index.html"
    remote_health = f"{root}/www/health-ok.json"
    client.exec_command(f"mkdir -p {remote_admin_dir}")[1].channel.recv_exit_status()
    if not ADMIN_INDEX_LOCAL.exists():
        raise FileNotFoundError(f"missing {ADMIN_INDEX_LOCAL}")
    if not HEALTH_OK_LOCAL.exists():
        raise FileNotFoundError(f"missing {HEALTH_OK_LOCAL}")
    sftp.put(str(ADMIN_INDEX_LOCAL), remote_admin)
    sftp.put(str(HEALTH_OK_LOCAL), remote_health)
    return remote_admin_dir, remote_health


def ensure_nginx(client: paramiko.SSHClient, sftp: paramiko.SFTPClient, admin_dir: str, health_ok: str) -> None:
    admin_dir = admin_dir.rstrip("/") + "/"
    snippet = f"""# LiveAssistant admin — UI always online; API via reverse tunnel :{TUNNEL_PORT}
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

location ^~ /diangexitong/api/ {{
    proxy_pass http://127.0.0.1:{TUNNEL_PORT}/diangexitong/api/;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
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
    write_remote_text(sftp, NGINX_SNIPPET, snippet)

    include_line = f"include {NGINX_SNIPPET};"
    marker = "liveassistant-diangexitong.conf"
    for conf in (PORTAL_SSL, PORTAL_HTTP):
        script = f"""
from pathlib import Path
p = Path({conf!r})
if not p.exists():
    print('skip-missing')
    raise SystemExit(0)
text = p.read_text(encoding='utf-8')
if {marker!r} in text:
    print('already')
    raise SystemExit(0)
inc = '    {include_line}\\n'
needle = 'include /etc/nginx/snippets/wecom-send.conf;'
if needle in text:
    p.write_text(text.replace(needle, inc + needle, 1), encoding='utf-8')
    print('inserted-wecom')
else:
    key = 'server {{\\n'
    idx = text.find(key)
    if idx < 0:
        raise SystemExit('no-server-block')
    cut = idx + len(key)
    p.write_text(text[:cut] + inc + text[cut:], encoding='utf-8')
    print('inserted-server')
"""
        _, stdout, stderr = client.exec_command("python3 -")
        stdout.channel.sendall(script.encode("utf-8"))
        stdout.channel.shutdown_write()
        out = stdout.read().decode("utf-8", "ignore")
        err = stderr.read().decode("utf-8", "ignore")
        code = stdout.channel.recv_exit_status()
        print(f"nginx-edit {conf}: {out.strip()} code={code}")
        if code != 0:
            raise RuntimeError(err or out)


def main() -> int:
    if not CRED.exists():
        print("Missing DeployCredentials.local.json", file=sys.stderr)
        return 1

    cred = load_cred()
    host = cred["server"]["host"]
    user = cred["server"].get("user", "root")
    password = cred["server"]["password"]
    deploy_path = cred["server"].get("deployPath") or REMOTE_ROOT
    stamp = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
    release_dir = f"{deploy_path}/releases/{stamp}"

    with tempfile.TemporaryDirectory() as td:
        tar_path = Path(td) / "liveassistant-src.tar"
        build_archive(tar_path)

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
                raise RuntimeError(f"cmd failed ({code}): {cmd}\n{err or out}")
            return out

        run(f"mkdir -p {release_dir} {deploy_path}/releases /etc/nginx/snippets")
        remote_tar = f"/tmp/liveassistant-{stamp}.tar"
        sftp.put(str(tar_path), remote_tar)
        run(f"tar -xf {remote_tar} -C {release_dir} && rm -f {remote_tar}")
        run(f"ln -sfn {release_dir} {deploy_path}/current")
        write_remote_text(
            sftp,
            f"{deploy_path}/DEPLOY.txt",
            "\n".join(
                [
                    "LiveAssistant source deploy",
                    f"release={stamp}",
                    f"path={deploy_path}/current",
                    f"public=https://xiangyuzhubao.xyz/diangexitong/",
                    f"tunnel=scripts/admin-tunnel.py -> server :{TUNNEL_PORT}",
                    "Admin API runs inside Windows LiveAssistant on 127.0.0.1:5088",
                    "",
                ]
            ),
        )

        admin_index, health_ok = ensure_admin_web(client, sftp, deploy_path)
        ensure_nginx(client, sftp, admin_index, health_ok)
        run("nginx -t")
        run("systemctl reload nginx")

        cred.setdefault("server", {})
        cred["server"]["host"] = host
        cred["server"]["user"] = user
        cred["server"]["deployPath"] = deploy_path
        cred["server"]["adminTunnelPort"] = TUNNEL_PORT
        cred["server"]["adminLocalPort"] = 5088
        CRED.write_text(json.dumps(cred, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

        sftp.close()
        client.close()

    print("OK")
    print(f"host={host}")
    print(f"path={deploy_path}/current")
    print(f"release={release_dir}")
    print(f"url=https://xiangyuzhubao.xyz/diangexitong/")
    print(f"tunnel_port={TUNNEL_PORT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
