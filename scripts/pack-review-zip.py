#!/usr/bin/env python3
"""打包 LiveAssistant + 酷狗 KgDesktop + 抖音弹幕 源码与 sidecar 供审查。"""

from __future__ import annotations

import os
import sys
import time
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
KUGOU = Path(r"E:\我的源码目录\酷狗协议\KgDesktop")
DOUYIN = Path(r"E:\我的源码目录\抖音网页弹幕")

SKIP_DIRS = {".git", "bin", "obj", "node_modules", "__pycache__", ".vs", "publish"}
SKIP_SUFFIX = {".zip", ".pdb"}
SKIP_NAMES = {
    "admin-accounts.json",
    "DeployCredentials.local.json",
    "appsettings.json.bak-publish",
}


def should_skip(path: Path, base: Path) -> bool:
    rel = path.relative_to(base)
    if any(part in SKIP_DIRS for part in rel.parts):
        return True
    if not path.is_file():
        return False
    if path.name in SKIP_NAMES or "admin-accounts" in path.name:
        return True
    return path.suffix.lower() in SKIP_SUFFIX


def add_tree(zf: zipfile.ZipFile, base: Path, arc_prefix: str) -> int:
    if not base.is_dir():
        print(f"SKIP missing: {base}", file=sys.stderr)
        return 0
    count = 0
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        dp = Path(dirpath)
        for fn in filenames:
            fp = dp / fn
            if should_skip(fp, base):
                continue
            arc = f"{arc_prefix}/{fp.relative_to(base).as_posix()}"
            zf.write(fp, arc)
            count += 1
    return count


def main() -> int:
    stamp = time.strftime("%Y%m%d-%H%M")
    out = ROOT / f"LiveAssistant-FullPackage-{stamp}.zip"
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as zf:
        n1 = add_tree(zf, ROOT, "LiveAssistant")
        n2 = add_tree(zf, KUGOU, "KgDesktop")
        n3 = add_tree(zf, DOUYIN, "DouyinDanmaku")
    size_mb = out.stat().st_size / 1024 / 1024
    print(f"files: LiveAssistant={n1}, KgDesktop={n2}, DouyinDanmaku={n3}")
    print(f"OK: {out}")
    print(f"SIZE_MB: {size_mb:.1f}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
