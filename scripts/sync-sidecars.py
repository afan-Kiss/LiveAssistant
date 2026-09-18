#!/usr/bin/env python3
"""把抖音 / 酷狗 sidecar 同步到本仓库 sidecars/ 及发布目录。

关键：发布目录 (publish/LiveAssistant-one) 同步时不得覆盖用户运行时数据
（尤其 cookies.json），否则更新/重启后会把新登录账号静默回滚成旧账号。
"""

from __future__ import annotations

import argparse
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SIDEcars = ROOT / "sidecars"
PUBLISH = ROOT / "publish" / "LiveAssistant-one"

DOUYIN_SRC = Path(r"E:\我的源码目录\抖音网页弹幕\build")
KUGOU_SRC = Path(r"E:\我的源码目录\酷狗协议\KgDesktop\build\bin")

DOUYIN_EXE = "抖音直播弹幕助手.exe"
KUGOU_EXE = "酷狗api_v1.5.exe"

# 用户运行时文件：目标已存在时一律跳过，禁止用开发机 build/data 覆盖。
PROTECTED_FILES = {
    "cookies.json",
    "diag_install_id.txt",
    "diag_remote.json",
    "rooms.json",
    "rooms.json.bak",
    "settings.json",
    "runtime.log",
    "session.json",
    "liveassistant.db",
    "appsettings.json",
    "appsettings.json.bak",
    "ReplyTemplates.json",
    "ReplyTemplates.json.bak",
    "device.json",
    "client_id.txt",
}
PROTECTED_TOP_DIRS = {
    "browser-profiles",
    "diag",
    "history",
    "risk",
    "webview",
    "fudai",
    "ai-speech",
    "AiSpeech",
}


def copy_file(src: Path, dst: Path) -> None:
    dst.parent.mkdir(parents=True, exist_ok=True)
    try:
        shutil.copy2(src, dst)
        print(f"  {src.name} -> {dst}")
    except PermissionError:
        tmp = dst.with_suffix(dst.suffix + ".tmp")
        shutil.copy2(src, tmp)
        tmp.replace(dst)
        print(f"  {src.name} -> {dst} (replaced locked file)")


def copy_tree(src: Path, dst: Path) -> None:
    if dst.exists():
        shutil.rmtree(dst)
    shutil.copytree(src, dst)
    print(f"  {src.name}/ -> {dst}/")


def merge_data(src: Path, dst: Path, *, preserve_user_data: bool) -> None:
    if not src.is_dir():
        return
    dst.mkdir(parents=True, exist_ok=True)
    copied = 0
    skipped = 0
    protected = 0
    for item in src.rglob("*"):
        if item.is_dir():
            continue
        rel = item.relative_to(src)
        target = dst / rel
        if preserve_user_data and target.exists():
            top = rel.parts[0] if rel.parts else ""
            if top in PROTECTED_TOP_DIRS or item.name in PROTECTED_FILES or str(rel).replace("\\", "/") in PROTECTED_FILES:
                protected += 1
                continue
        target.parent.mkdir(parents=True, exist_ok=True)
        try:
            shutil.copy2(item, target)
            copied += 1
        except OSError:
            skipped += 1
    note_parts = []
    if protected:
        note_parts.append(f"{protected} user-data preserved")
    if skipped:
        note_parts.append(f"{skipped} locked, kept existing")
    note = f" ({', '.join(note_parts)})" if note_parts else ""
    mode = "preserve" if preserve_user_data else "overwrite"
    print(f"  merged[{mode}] {src}/ -> {dst}/ ({copied} files{note})")


def sync_to(target: Path, douyin_src: Path, kugou_src: Path, *, preserve_user_data: bool) -> None:
    target.mkdir(parents=True, exist_ok=True)

    douyin_exe = douyin_src / DOUYIN_EXE
    kugou_exe = kugou_src / KUGOU_EXE
    kgapijs = kugou_src / "kgapijs"

    if not douyin_exe.is_file():
        raise FileNotFoundError(f"缺少抖音 sidecar: {douyin_exe}")
    if not kugou_exe.is_file():
        raise FileNotFoundError(f"缺少酷狗 sidecar: {kugou_exe}")
    if not kgapijs.is_dir():
        raise FileNotFoundError(f"缺少酷狗 kgapijs: {kgapijs}")

    print(f"sync -> {target} (preserve_user_data={preserve_user_data})")
    errors: list[str] = []
    for label, action, required in (
        ("douyin", lambda: copy_file(douyin_exe, target / DOUYIN_EXE), target / DOUYIN_EXE),
        ("kugou", lambda: copy_file(kugou_exe, target / KUGOU_EXE), target / KUGOU_EXE),
        ("kgapijs", lambda: copy_tree(kgapijs, target / "kgapijs"), target / "kgapijs"),
    ):
        try:
            action()
        except OSError as exc:
            if required.exists():
                print(f"  WARN {label}: {exc} (kept existing)")
            else:
                errors.append(f"{label}: {exc}")
    if errors:
        raise OSError("; ".join(errors))

    data_dst = target / "data"
    merge_data(douyin_src / "data", data_dst, preserve_user_data=preserve_user_data)
    merge_data(kugou_src / "data", data_dst, preserve_user_data=preserve_user_data)


def main() -> int:
    parser = argparse.ArgumentParser(description="同步 sidecar 到 sidecars/ 与发布目录")
    parser.add_argument("--douyin-src", type=Path, default=DOUYIN_SRC)
    parser.add_argument("--kugou-src", type=Path, default=KUGOU_SRC)
    parser.add_argument("--skip-publish", action="store_true", help="只更新 sidecars/，不复制到 publish")
    args = parser.parse_args()

    try:
        # sidecars 是打包暂存区，允许覆盖；publish 必须保护用户 cookies。
        sync_to(SIDEcars, args.douyin_src, args.kugou_src, preserve_user_data=False)
        if not args.skip_publish and PUBLISH.is_dir():
            sync_to(PUBLISH, args.douyin_src, args.kugou_src, preserve_user_data=True)
    except FileNotFoundError as exc:
        print(str(exc), file=sys.stderr)
        return 1

    print("OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
