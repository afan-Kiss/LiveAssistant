#!/usr/bin/env python3
"""把抖音 / 酷狗 sidecar 同步到本仓库 sidecars/ 及发布目录。"""

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


def copy_file(src: Path, dst: Path) -> None:
    dst.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(src, dst)
    print(f"  {src.name} -> {dst}")


def copy_tree(src: Path, dst: Path) -> None:
    if dst.exists():
        shutil.rmtree(dst)
    shutil.copytree(src, dst)
    print(f"  {src.name}/ -> {dst}/")


def merge_data(src: Path, dst: Path) -> None:
    if not src.is_dir():
        return
    dst.mkdir(parents=True, exist_ok=True)
    for item in src.rglob("*"):
        if item.is_dir():
            continue
        rel = item.relative_to(src)
        target = dst / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(item, target)
    print(f"  merged {src}/ -> {dst}/")


def sync_to(target: Path, douyin_src: Path, kugou_src: Path) -> None:
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

    print(f"sync -> {target}")
    copy_file(douyin_exe, target / DOUYIN_EXE)
    copy_file(kugou_exe, target / KUGOU_EXE)
    copy_tree(kgapijs, target / "kgapijs")

    data_dst = target / "data"
    merge_data(douyin_src / "data", data_dst)
    merge_data(kugou_src / "data", data_dst)


def main() -> int:
    parser = argparse.ArgumentParser(description="同步 sidecar 到 sidecars/ 与发布目录")
    parser.add_argument("--douyin-src", type=Path, default=DOUYIN_SRC)
    parser.add_argument("--kugou-src", type=Path, default=KUGOU_SRC)
    parser.add_argument("--skip-publish", action="store_true", help="只更新 sidecars/，不复制到 publish")
    args = parser.parse_args()

    try:
        sync_to(SIDEcars, args.douyin_src, args.kugou_src)
        if not args.skip_publish and PUBLISH.is_dir():
            sync_to(PUBLISH, args.douyin_src, args.kugou_src)
    except FileNotFoundError as exc:
        print(str(exc), file=sys.stderr)
        return 1

    print("OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
