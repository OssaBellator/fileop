#!/usr/bin/env python3
"""Zero-Actions semantic checks for FileOp aggregate storage history."""
from __future__ import annotations

import argparse
import random
import sqlite3
import sys
from pathlib import Path

SCHEMA = """
PRAGMA foreign_keys = ON;
CREATE TABLE IF NOT EXISTS storage_history_schema_info(
    id INTEGER PRIMARY KEY CHECK(id = 1),
    version INTEGER NOT NULL
);
INSERT OR IGNORE INTO storage_history_schema_info(id, version) VALUES (1, 1);
CREATE TABLE IF NOT EXISTS storage_history_snapshots(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    root_path TEXT NOT NULL COLLATE NOCASE,
    captured_utc_ticks INTEGER NOT NULL,
    logical_bytes INTEGER NOT NULL,
    allocated_bytes INTEGER NULL,
    file_count INTEGER NOT NULL,
    hard_link_alias_count INTEGER NOT NULL,
    type_count INTEGER NOT NULL,
    UNIQUE(root_path, captured_utc_ticks)
);
CREATE INDEX IF NOT EXISTS ix_storage_history_root_captured
    ON storage_history_snapshots(root_path COLLATE NOCASE, captured_utc_ticks DESC);
CREATE TABLE IF NOT EXISTS storage_history_categories(
    snapshot_id INTEGER NOT NULL,
    category INTEGER NOT NULL,
    logical_bytes INTEGER NOT NULL,
    allocated_bytes INTEGER NULL,
    file_count INTEGER NOT NULL,
    hard_link_alias_count INTEGER NOT NULL,
    type_count INTEGER NOT NULL,
    PRIMARY KEY(snapshot_id, category),
    FOREIGN KEY(snapshot_id) REFERENCES storage_history_snapshots(id) ON DELETE CASCADE
) WITHOUT ROWID;
"""

CATEGORY_COUNT = 12


def _db() -> sqlite3.Connection:
    connection = sqlite3.connect(":memory:")
    connection.executescript(SCHEMA)
    return connection


def _save(
    connection: sqlite3.Connection,
    root: str,
    ticks: int,
    logical: int,
    allocated: int | None,
    files: int,
    aliases: int,
    type_count: int,
    categories: list[tuple[int, int, int | None, int, int, int]],
) -> int:
    snapshot_id = connection.execute(
        """
        INSERT INTO storage_history_snapshots(
            root_path, captured_utc_ticks, logical_bytes, allocated_bytes,
            file_count, hard_link_alias_count, type_count)
        VALUES (?, ?, ?, ?, ?, ?, ?)
        ON CONFLICT(root_path, captured_utc_ticks) DO UPDATE SET
            logical_bytes = excluded.logical_bytes,
            allocated_bytes = excluded.allocated_bytes,
            file_count = excluded.file_count,
            hard_link_alias_count = excluded.hard_link_alias_count,
            type_count = excluded.type_count
        RETURNING id;
        """,
        (root, ticks, logical, allocated, files, aliases, type_count),
    ).fetchone()[0]
    connection.execute(
        "DELETE FROM storage_history_categories WHERE snapshot_id = ?;",
        (snapshot_id,),
    )
    connection.executemany(
        """
        INSERT INTO storage_history_categories(
            snapshot_id, category, logical_bytes, allocated_bytes,
            file_count, hard_link_alias_count, type_count)
        VALUES (?, ?, ?, ?, ?, ?, ?);
        """,
        [(snapshot_id, *row) for row in categories],
    )
    connection.commit()
    return int(snapshot_id)


def check_sqlite_history() -> int:
    connection = _db()
    first_id = _save(
        connection,
        r"C:\Data",
        100,
        600,
        640,
        3,
        0,
        3,
        [(2, 400, 448, 2, 0, 2), (1, 200, 192, 1, 0, 1)],
    )
    second_id = _save(
        connection,
        r"C:\Data",
        200,
        900,
        1024,
        5,
        1,
        4,
        [(2, 500, 512, 3, 1, 2), (1, 250, 256, 1, 0, 1), (5, 150, 256, 1, 0, 1)],
    )
    assert first_id != second_id
    rows = connection.execute(
        """
        SELECT id, logical_bytes
        FROM storage_history_snapshots
        WHERE root_path = ? COLLATE NOCASE
        ORDER BY captured_utc_ticks DESC;
        """,
        (r"c:\data",),
    ).fetchall()
    assert rows == [(second_id, 900), (first_id, 600)]

    replacement_id = _save(
        connection,
        r"c:\data",
        100,
        700,
        768,
        4,
        0,
        4,
        [(2, 500, 576, 3, 0, 3), (1, 200, 192, 1, 0, 1)],
    )
    assert replacement_id == first_id
    assert connection.execute(
        "SELECT COUNT(*) FROM storage_history_snapshots;"
    ).fetchone()[0] == 2
    assert connection.execute(
        "SELECT COUNT(*) FROM storage_history_categories WHERE snapshot_id = ?;",
        (first_id,),
    ).fetchone()[0] == 2

    # Simulate the existing namespace rebuild clear. History is deliberately not cleared.
    connection.executescript(
        """
        CREATE TABLE IF NOT EXISTS files(path TEXT PRIMARY KEY);
        CREATE TABLE IF NOT EXISTS source_checkpoints(source_key TEXT PRIMARY KEY);
        DELETE FROM files;
        DELETE FROM source_checkpoints;
        """
    )
    assert connection.execute(
        "SELECT COUNT(*) FROM storage_history_snapshots;"
    ).fetchone()[0] == 2

    connection.execute(
        "DELETE FROM storage_history_snapshots WHERE captured_utc_ticks < ?;",
        (150,),
    )
    connection.commit()
    assert connection.execute(
        "SELECT COUNT(*) FROM storage_history_snapshots;"
    ).fetchone()[0] == 1
    assert connection.execute(
        "SELECT COUNT(*) FROM storage_history_categories;"
    ).fetchone()[0] == 3
    connection.close()
    return 8


def _allocated_or_zero(item: tuple[int, int | None, int, int, int] | None) -> int | None:
    return 0 if item is None else item[1]


def _delta(
    older: dict[int, tuple[int, int | None, int, int, int]],
    newer: dict[int, tuple[int, int | None, int, int, int]],
) -> dict[int, tuple[int, int | None, int, int, int]]:
    result = {}
    for category in range(CATEGORY_COUNT):
        old = older.get(category)
        new = newer.get(category)
        old_allocated = _allocated_or_zero(old)
        new_allocated = _allocated_or_zero(new)
        allocated_delta = (
            new_allocated - old_allocated
            if old_allocated is not None and new_allocated is not None
            else None
        )
        row = (
            (new[0] if new else 0) - (old[0] if old else 0),
            allocated_delta,
            (new[2] if new else 0) - (old[2] if old else 0),
            (new[3] if new else 0) - (old[3] if old else 0),
            (new[4] if new else 0) - (old[4] if old else 0),
        )
        if row != (0, 0, 0, 0, 0):
            result[category] = row
    return result


def check_delta_properties(cases: int = 1000) -> int:
    rng = random.Random(20260808)
    for _ in range(cases):
        snapshots = []
        for _snapshot in range(2):
            rows: dict[int, tuple[int, int | None, int, int, int]] = {}
            physical_complete = rng.random() < 0.7
            for category in range(CATEGORY_COUNT):
                if rng.random() < 0.45:
                    continue
                logical = rng.randint(0, 10**9)
                files = rng.randint(0, 5000)
                aliases = rng.randint(0, files) if files else 0
                types = rng.randint(1, 40)
                allocated = rng.randint(0, 10**9)
                if not physical_complete and rng.random() < 0.3:
                    allocated = None
                rows[category] = (logical, allocated, files, aliases, types)
            snapshots.append(rows)

        older, newer = snapshots
        deltas = _delta(older, newer)
        assert sum(row[0] for row in deltas.values()) == (
            sum(row[0] for row in newer.values()) - sum(row[0] for row in older.values())
        )
        assert sum(row[2] for row in deltas.values()) == (
            sum(row[2] for row in newer.values()) - sum(row[2] for row in older.values())
        )
        assert sum(row[3] for row in deltas.values()) == (
            sum(row[3] for row in newer.values()) - sum(row[3] for row in older.values())
        )
        assert sum(row[4] for row in deltas.values()) == (
            sum(row[4] for row in newer.values()) - sum(row[4] for row in older.values())
        )

        older_known = all(row[1] is not None for row in older.values())
        newer_known = all(row[1] is not None for row in newer.values())
        if older_known and newer_known:
            assert all(row[1] is not None for row in deltas.values())
            assert sum(row[1] for row in deltas.values()) == (
                sum(row[1] for row in newer.values()) - sum(row[1] for row in older.values())
            )
    return cases


def check_repository(repo_root: Path) -> int:
    history = repo_root / "src/FileOp.Core/Storage/StorageHistory.cs"
    store = repo_root / "src/FileOp.Core/Storage/SqliteStorageHistoryStore.cs"
    index = repo_root / "src/FileOp.Core/Search/SqliteFileIndex.cs"
    tests = repo_root / "tests/FileOp.Windows.Tests/StorageHistoryTests.cs"
    missing = [str(path) for path in (history, store, index, tests) if not path.is_file()]
    if missing:
        raise FileNotFoundError("Missing repository files: " + ", ".join(missing))

    history_text = history.read_text(encoding="utf-8")
    store_text = store.read_text(encoding="utf-8")
    index_text = index.read_text(encoding="utf-8")
    tests_text = tests.read_text(encoding="utf-8")

    required = [
        (history_text, "IStorageHistoryStore"),
        (history_text, "StorageHistoryDelta Between"),
        (store_text, "storage_history_schema_info"),
        (store_text, "UNIQUE(root_path, captured_utc_ticks)"),
        (store_text, "ON DELETE CASCADE"),
        (store_text, "ValidateAnalysis(analysis)"),
        (tests_text, "NamespaceClearLeavesHistoricalSnapshotsIntact"),
        (tests_text, "StoreRejectsUnknownHistorySchemaVersion"),
        (tests_text, "DeltaHandlesMissingCategoriesAndUnknownPhysicalAllocation"),
    ]
    for text, needle in required:
        assert needle in text, f"required source invariant missing: {needle}"

    assert "DELETE FROM files; DELETE FROM source_checkpoints;" in index_text
    assert "storage_history_snapshots" not in index_text, (
        "Core namespace ClearAsync must not silently erase historical observations"
    )
    assert "storage_history_schema_info" not in index_text, (
        "History sub-schema versioning must remain independent of the core index schema"
    )
    return len(required) + 3


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--self-test-only", action="store_true")
    parser.add_argument("--cases", type=int, default=1000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error("--cases must be greater than zero")

    sqlite_checks = check_sqlite_history()
    delta_cases = check_delta_properties(args.cases)
    print(f"PASS storage history SQLite semantics: {sqlite_checks} checks")
    print(f"PASS storage history delta properties: {delta_cases} randomized cases")

    if not args.self_test_only:
        source_checks = check_repository(args.repo_root.resolve())
        print(f"PASS storage history source wiring: {source_checks} checks")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, sqlite3.Error) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
