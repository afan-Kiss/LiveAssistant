#!/usr/bin/env python3
"""Regression: publish data merge must not overwrite existing cookies.json."""

from __future__ import annotations

import importlib.util
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("sync_sidecars", SCRIPTS / "sync-sidecars.py")
ss = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(ss)


class MergeDataPreserveTest(unittest.TestCase):
    def test_preserve_existing_cookies(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            src = root / "src"
            dst = root / "dst"
            src.mkdir()
            dst.mkdir()
            (src / "cookies.json").write_text('{"active":"old","profiles":{}}', encoding="utf-8")
            (src / "diag_remote.example.json").write_text("{}", encoding="utf-8")
            (dst / "cookies.json").write_text('{"active":"new","profiles":{"钢铁侠":{}}}', encoding="utf-8")

            ss.merge_data(src, dst, preserve_user_data=True)

            text = (dst / "cookies.json").read_text(encoding="utf-8")
            self.assertIn("钢铁侠", text)
            self.assertNotIn('"active":"old"', text)
            self.assertTrue((dst / "diag_remote.example.json").exists())

    def test_overwrite_when_not_preserving(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            src = root / "src"
            dst = root / "dst"
            src.mkdir()
            dst.mkdir()
            (src / "cookies.json").write_text('{"active":"old"}', encoding="utf-8")
            (dst / "cookies.json").write_text('{"active":"new"}', encoding="utf-8")
            ss.merge_data(src, dst, preserve_user_data=False)
            self.assertIn('"active":"old"', (dst / "cookies.json").read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
