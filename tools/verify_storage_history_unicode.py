#!/usr/bin/env python3
"""Zero-Actions regression for Unicode case-insensitive storage-history roots."""
from __future__ import annotations

import argparse
import sqlite3
import sys
from pathlib import Path

COLLATION = "FILEOP_ORDINAL_NOCASE"


def _compare_ordinal_ignore_case(left: str, right: str) -> int:
    # This verifier targets the non-ASCII casing regression (Ä/ä). The runtime
    # implementation remains authoritative and uses StringComparer.OrdinalIgnoreCase.
    left_key = left.upper()
    right_key = right.upper()
    return (left_key > right_key) - (left_key < right_key)


def check_sqlite_semantics() -> int:
    connection = sqlite3.connect(":memory:")

    # Prove the regression is meaningful: SQLite's built-in NOCASE does not
    # collapse this Unicode case pair.
    connection.execute(
        "CREATE TABLE builtin(root TEXT NOT NULL COLLATE NOCASE, ticks INTEGER NOT NULL, UNIQUE(root, ticks));"
    )
    connection.execute("INSERT INTO builtin(root, ticks) VALUES (?, ?);", (r"C:\Ärea", 1))
    connection.execute("INSERT INTO builtin(root, ticks) VALUES (?, ?);", (r"c:\ärea", 1))
    assert connection.execute("SELECT COUNT(*) FROM builtin;").fetchone()[0] == 2

    connection.create_collation(COLLATION, _compare_ordinal_ignore_case)
    connection.execute(
        f"""
        CREATE TABLE history(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            root_path TEXT NOT NULL COLLATE {COLLATION},
            ticks INTEGER NOT NULL,
            logical_bytes INTEGER NOT NULL,
            UNIQUE(root_path, ticks)
        );
        """
    )

    first_id = connection.execute(
        "INSERT INTO history(root_path, ticks, logical_bytes) VALUES (?, ?, ?) RETURNING id;",
        (r"C:\Ärea", 1, 100),
    ).fetchone()[0]
    replacement_id = connection.execute(
        """
        INSERT INTO history(root_path, ticks, logical_bytes)
        VALUES (?, ?, ?)
        ON CONFLICT(root_path, ticks) DO UPDATE SET logical_bytes = excluded.logical_bytes
        RETURNING id;
        """,
        (r"c:\ärea", 1, 200),
    ).fetchone()[0]

    assert replacement_id == first_id
    assert connection.execute("SELECT COUNT(*) FROM history;").fetchone()[0] == 1
    row = connection.execute(
        f"SELECT logical_bytes FROM history WHERE root_path = ? COLLATE {COLLATION};",
        (r"C:\ÄREA",),
    ).fetchone()
    assert row == (200,)
    connection.close()
    return 4


def check_repository(repo_root: Path) -> int:
    store = repo_root / "src/FileOp.Core/Storage/SqliteStorageHistoryStore.cs"
    test = repo_root / "tests/FileOp.Windows.Tests/StorageHistoryUnicodeRootTests.cs"
    if not store.is_file() or not test.is_file():
        raise FileNotFoundError("Storage-history Unicode regression files are missing")

    store_text = store.read_text(encoding="utf-8")
    test_text = test.read_text(encoding="utf-8")
    required = [
        (store_text, 'HistoryRootCollation = "FILEOP_ORDINAL_NOCASE"'),
        (store_text, "connection.CreateCollation("),
        (store_text, "StringComparer.OrdinalIgnoreCase.Compare"),
        (store_text, "root_path TEXT NOT NULL COLLATE FILEOP_ORDINAL_NOCASE"),
        (store_text, "WHERE root_path = @root_path COLLATE FILEOP_ORDINAL_NOCASE"),
        (test_text, "UnicodeCaseEquivalentRootsShareSnapshotIdentity"),
        (test_text, '@"C:\\Ärea"'),
        (test_text, '@"c:\\ärea\\"'),
    ]
    for text, needle in required:
        assert needle in text, f"required Unicode root invariant missing: {needle}"
    return len(required)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--self-test-only", action="store_true")
    args = parser.parse_args()

    sqlite_checks = check_sqlite_semantics()
    print(f"PASS storage history Unicode root SQLite semantics: {sqlite_checks} checks")
    if not args.self_test_only:
        source_checks = check_repository(args.repo_root.resolve())
        print(f"PASS storage history Unicode root source wiring: {source_checks} checks")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, sqlite3.Error) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
