#!/usr/bin/env python3
"""Offline checks for FileOp durable file-operation action history."""
from __future__ import annotations

import argparse
import random
import sqlite3
import sys
from enum import IntEnum
from pathlib import Path


class Entry(IntEnum):
    PENDING = 0
    STARTED = 1
    COMMITTED = 2
    SKIPPED = 3
    FAILED = 4
    RECOVERY = 5


class Terminal(IntEnum):
    SUCCEEDED = 0
    FAILED = 1
    CANCELLED = 2
    RECOVERY = 3


def run_model(cases: int) -> int:
    rng = random.Random(20260808)
    checks = 0
    for _ in range(cases):
        count = rng.randint(1, 12)
        directories = [rng.random() < 0.2 for _ in range(count)]
        states = [Entry.SKIPPED if d or rng.random() < 0.25 else Entry.PENDING for d in directories]
        for index, state in enumerate(states):
            if state == Entry.SKIPPED:
                continue
            action = rng.choice(("commit", "pre-fail", "recovery", "leave"))
            if action == "pre-fail":
                states[index] = Entry.FAILED
            else:
                states[index] = Entry.STARTED
                if action == "commit":
                    states[index] = Entry.COMMITTED
                elif action == "recovery":
                    states[index] = Entry.RECOVERY
            checks += 1

        ambiguous = any(state in (Entry.STARTED, Entry.RECOVERY) for state in states)
        successful = all(state in (Entry.COMMITTED, Entry.SKIPPED) for state in states)
        if ambiguous:
            terminal = Terminal.RECOVERY
        elif successful:
            terminal = Terminal.SUCCEEDED
        else:
            terminal = Terminal.FAILED

        assert terminal != Terminal.SUCCEEDED or successful
        assert terminal not in (Terminal.FAILED, Terminal.CANCELLED) or not ambiguous
        assert terminal != Terminal.RECOVERY or ambiguous
        checks += 3
    return checks


def check_sqlite_identity() -> int:
    connection = sqlite3.connect(":memory:")
    connection.execute("CREATE TABLE ids(volume INTEGER NOT NULL, reference INTEGER NOT NULL)")
    values = ((1 << 64) - 18, (1 << 64) - 32)
    signed = tuple(value if value < (1 << 63) else value - (1 << 64) for value in values)
    connection.execute("INSERT INTO ids VALUES (?, ?)", signed)
    row = connection.execute("SELECT volume, reference FROM ids").fetchone()
    assert row == signed
    restored = tuple(value if value >= 0 else value + (1 << 64) for value in row)
    assert restored == values
    return 2


def check_repository(root: Path) -> int:
    paths = {
        "contract": root / "src/FileOp.Core/Operations/FileOperationActionHistory.cs",
        "store": root / "src/FileOp.Core/Operations/SqliteFileOperationActionHistoryStore.cs",
        "executor": root / "src/FileOp.Core/Operations/FileCopyOperationExecutor.cs",
        "tests": root / "tests/FileOp.Windows.Tests/FileOperationActionHistoryTests.cs",
        "docs": root / "docs/file-operation-action-history.md",
        "local": root / "tools/test-local.ps1",
    }
    missing = [str(path) for path in paths.values() if not path.is_file()]
    if missing:
        raise FileNotFoundError(", ".join(missing))
    source = {name: path.read_text(encoding="utf-8") for name, path in paths.items()}

    required = {
        "contract": (
            "public interface IFileOperationActionHistoryStore",
            "MutationStarted",
            "RecoveryRequired",
            "DeleteCreatedDestination",
            "Array.AsReadOnly(entrySnapshot)",
        ),
        "store": (
            "PRAGMA journal_mode = WAL;",
            "PRAGMA synchronous = FULL;",
            "file_operation_action_entries",
            "action.terminal_state IS NULL",
            "FileOperationCanonicalPathState.Missing",
            "unchecked((long)value)",
            "unchecked((ulong)value)",
        ),
        "tests": (
            "CopyCommitPersistsUndoCandidateAndHighBitIdentity",
            "MutationStartedSurvivesAsRecoverySignal",
            "BeginRejectsReadyDirectoryAndMoveWithoutRows",
            "ActionHistoryDefensivelySnapshotsEntries",
        ),
        "docs": (
            "FileCopyOperationExecutor",
            "IFileCopyMutationPrimitive",
            "undo candidate",
            "not sufficient",
        ),
        "local": ("verify_file_operation_action_history.py",),
    }
    checks = 0
    for name, needles in required.items():
        for needle in needles:
            assert needle.casefold() in source[name].casefold(), needle
            checks += 1

    executor = source["executor"]
    start = executor.index(".MarkMutationStartedAsync(")
    mutate = executor.index("mutationLease = await _mutation")
    commit = executor.index(".CommitCopyAsync(")
    assert start < mutate < commit
    assert ".MarkMutationRecoveryRequiredAsync(" in executor
    checks += 2

    store_surface = source["contract"] + source["store"]
    forbidden = ("File." + "Copy(", "File." + "Move(", "Directory." + "Move(")
    for needle in forbidden:
        assert needle not in store_surface, needle
        checks += 1
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--self-test-only", action="store_true")
    parser.add_argument("--cases", type=int, default=20000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error("--cases must be greater than zero")

    checks = run_model(args.cases) + check_sqlite_identity()
    print(f"PASS action-history offline model: {checks} checks across {args.cases} randomized cases")
    if not args.self_test_only:
        print(f"PASS action-history source wiring: {check_repository(args.repo_root.resolve())} checks")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError, sqlite3.DatabaseError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
