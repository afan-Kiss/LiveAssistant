#!/usr/bin/env python3
"""Seed repo data/ from publish DB and report legacy score counts."""

from __future__ import annotations

import shutil
import sqlite3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TARGET = ROOT / "data"
PUBLISH_DB = ROOT / "publish" / "LiveAssistant-one" / "data" / "liveassistant.db"
LEGACY_DBS = [
    ROOT / "LiveAssistant" / "bin" / "Debug" / "net8.0-windows" / "data" / "liveassistant.db",
    PUBLISH_DB,
]


def count_scores(db: Path) -> tuple[int, int]:
    if not db.exists():
        return 0, 0
    conn = sqlite3.connect(db)
    cur = conn.cursor()
    cur.execute("SELECT COUNT(*) FROM movie_score_totals")
    totals = int(cur.fetchone()[0])
    cur.execute("SELECT COUNT(*) FROM movie_score_events")
    events = int(cur.fetchone()[0])
    conn.close()
    return totals, events


def main() -> int:
    TARGET.mkdir(parents=True, exist_ok=True)
    target_db = TARGET / "liveassistant.db"

    if PUBLISH_DB.exists():
        shutil.copy2(PUBLISH_DB, target_db)
        print(f"seeded {target_db} from publish")
    elif not target_db.exists():
        print("no publish DB to seed; LiveAssistant will create a fresh database")
        return 0

    for legacy in LEGACY_DBS:
        totals, events = count_scores(legacy)
        print(f"legacy {legacy}: totals={totals} events={events}")

    totals, events = count_scores(target_db)
    print(f"target before restart: totals={totals} events={events}")
    print(f"unified data dir: {TARGET}")
    print("restart LiveAssistant with LA_DATA_DIR pointing here to merge legacy scores")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
