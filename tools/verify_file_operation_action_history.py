#!/usr/bin/env python3
"""Zero-Actions checks for FileOp durable file-operation action history."""
from __future__ import annotations

import argparse
import random
import sqlite3
import sys
from dataclasses import dataclass
from enum import IntEnum
from pathlib import Path


class EntryState(IntEnum):
    PENDING = 0
    MUTATION_STARTED = 1
    COMMITTED = 2
    SKIPPED = 3
    FAILED = 4
    RECOVERY_REQUIRED = 5


class TerminalState(IntEnum):
    SUCCEEDED = 0
    FAILED = 1
    CANCELLED = 2
    RECOVERY_REQUIRED = 3


class UndoKind(IntEnum):
    NONE = 0
    DELETE_CREATED_DESTINATION = 1


SCHEMA = """
PRAGMA foreign_keys = ON;
CREATE TABLE actions(
    operation_id TEXT PRIMARY KEY,
    kind INTEGER NOT NULL,
    terminal_state INTEGER NULL
) WITHOUT ROWID;
CREATE TABLE entries(
    operation_id TEXT NOT NULL,
    ordinal INTEGER NOT NULL,
    is_directory INTEGER NOT NULL,
    state INTEGER NOT NULL,
    mutation_started INTEGER NULL,
    completed INTEGER NULL,
    destination_identity TEXT NULL,
    undo_kind INTEGER NOT NULL,
    failure_code TEXT NULL,
    PRIMARY KEY(operation_id, ordinal),
    FOREIGN KEY(operation_id) REFERENCES actions(operation_id) ON DELETE CASCADE
) WITHOUT ROWID;
"""


def db() -> sqlite3.Connection:
    connection = sqlite3.connect(":memory:")
    connection.executescript(SCHEMA)
    return connection


def begin(
    connection: sqlite3.Connection,
    operation_id: str,
    decisions: list[str],
    directories: list[bool] | None = None,
) -> None:
    directories = directories or [False] * len(decisions)
    if len(directories) != len(decisions):
        raise ValueError("directory flags must match decisions")
    if any(is_directory and decision == "ready"
           for decision, is_directory in zip(decisions, directories, strict=True)):
        raise ValueError("action-history schema v1 does not support directory mutation")

    with connection:
        connection.execute(
            "INSERT INTO actions(operation_id, kind, terminal_state) VALUES (?, 0, NULL)",
            (operation_id,),
        )
        for ordinal, (decision, is_directory) in enumerate(
            zip(decisions, directories, strict=True)
        ):
            if decision == "ready":
                state, completed = EntryState.PENDING, None
            elif decision == "skip":
                state, completed = EntryState.SKIPPED, 1
            else:
                raise ValueError("begin requires ready/skip decisions")
            connection.execute(
                """
                INSERT INTO entries(
                    operation_id, ordinal, is_directory, state, mutation_started, completed,
                    destination_identity, undo_kind, failure_code)
                VALUES (?, ?, ?, ?, NULL, ?, NULL, ?, NULL)
                """,
                (operation_id, ordinal, int(is_directory), int(state), completed, int(UndoKind.NONE)),
            )


def start_mutation(connection: sqlite3.Connection, operation_id: str, ordinal: int) -> bool:
    with connection:
        changed = connection.execute(
            """
            UPDATE entries
            SET state = ?, mutation_started = 1
            WHERE operation_id = ? AND ordinal = ? AND state = ? AND is_directory = 0
              AND EXISTS(
                  SELECT 1 FROM actions
                  WHERE operation_id = ? AND terminal_state IS NULL
              )
            """,
            (int(EntryState.MUTATION_STARTED), operation_id, ordinal,
             int(EntryState.PENDING), operation_id),
        ).rowcount
    return changed == 1


def commit_copy(connection: sqlite3.Connection, operation_id: str, ordinal: int, identity: str) -> bool:
    with connection:
        changed = connection.execute(
            """
            UPDATE entries
            SET state = ?, completed = 1, destination_identity = ?, undo_kind = ?
            WHERE operation_id = ? AND ordinal = ? AND state = ? AND is_directory = 0
              AND EXISTS(
                  SELECT 1 FROM actions
                  WHERE operation_id = ? AND terminal_state IS NULL AND kind = 0
              )
            """,
            (int(EntryState.COMMITTED), identity, int(UndoKind.DELETE_CREATED_DESTINATION),
             operation_id, ordinal, int(EntryState.MUTATION_STARTED), operation_id),
        ).rowcount
    return changed == 1


def fail_before_mutation(connection: sqlite3.Connection, operation_id: str, ordinal: int) -> bool:
    with connection:
        changed = connection.execute(
            """
            UPDATE entries
            SET state = ?, completed = 1, failure_code = 'BeforeMutation'
            WHERE operation_id = ? AND ordinal = ? AND state = ? AND is_directory = 0
            """,
            (int(EntryState.FAILED), operation_id, ordinal, int(EntryState.PENDING)),
        ).rowcount
    return changed == 1


def require_recovery(connection: sqlite3.Connection, operation_id: str, ordinal: int) -> bool:
    with connection:
        changed = connection.execute(
            """
            UPDATE entries
            SET state = ?, completed = 1, failure_code = 'MutationUncertain'
            WHERE operation_id = ? AND ordinal = ? AND state = ? AND is_directory = 0
            """,
            (int(EntryState.RECOVERY_REQUIRED), operation_id, ordinal,
             int(EntryState.MUTATION_STARTED)),
        ).rowcount
    return changed == 1


def states(connection: sqlite3.Connection, operation_id: str) -> list[EntryState]:
    return [
        EntryState(row[0])
        for row in connection.execute(
            "SELECT state FROM entries WHERE operation_id = ? ORDER BY ordinal",
            (operation_id,),
        )
    ]


def requires_recovery(connection: sqlite3.Connection, operation_id: str) -> bool:
    terminal = connection.execute(
        "SELECT terminal_state FROM actions WHERE operation_id = ?",
        (operation_id,),
    ).fetchone()[0]
    return terminal == int(TerminalState.RECOVERY_REQUIRED) or any(
        state in {EntryState.MUTATION_STARTED, EntryState.RECOVERY_REQUIRED}
        for state in states(connection, operation_id)
    )


def complete(connection: sqlite3.Connection, operation_id: str, terminal: TerminalState) -> bool:
    current = states(connection, operation_id)
    ambiguous = any(
        state in {EntryState.MUTATION_STARTED, EntryState.RECOVERY_REQUIRED}
        for state in current
    )
    if terminal == TerminalState.RECOVERY_REQUIRED:
        if not ambiguous:
            return False
        with connection:
            connection.execute(
                "UPDATE entries SET state = ?, completed = COALESCE(completed, 1) "
                "WHERE operation_id = ? AND state = ?",
                (int(EntryState.RECOVERY_REQUIRED), operation_id,
                 int(EntryState.MUTATION_STARTED)),
            )
    elif ambiguous:
        return False
    elif terminal == TerminalState.SUCCEEDED and any(
        state not in {EntryState.COMMITTED, EntryState.SKIPPED} for state in current
    ):
        return False

    with connection:
        changed = connection.execute(
            "UPDATE actions SET terminal_state = ? "
            "WHERE operation_id = ? AND terminal_state IS NULL",
            (int(terminal), operation_id),
        ).rowcount
    return changed == 1


def undo_candidates(connection: sqlite3.Connection, operation_id: str) -> list[int]:
    return [
        row[0]
        for row in connection.execute(
            """
            SELECT ordinal
            FROM entries
            WHERE operation_id = ? AND is_directory = 0 AND state = ? AND undo_kind = ?
              AND destination_identity IS NOT NULL
            ORDER BY ordinal
            """,
            (operation_id, int(EntryState.COMMITTED), int(UndoKind.DELETE_CREATED_DESTINATION)),
        )
    ]


def check_fixed() -> int:
    connection = db()
    begin(connection, "copy", ["ready", "skip"])
    assert states(connection, "copy") == [EntryState.PENDING, EntryState.SKIPPED]
    assert start_mutation(connection, "copy", 0)
    assert requires_recovery(connection, "copy")
    assert not complete(connection, "copy", TerminalState.SUCCEEDED)
    assert commit_copy(connection, "copy", 0, "FFFFFFFFFFFFFFFF:FFFFFFFFFFFFFFFE")
    assert not requires_recovery(connection, "copy")
    assert undo_candidates(connection, "copy") == [0]
    assert complete(connection, "copy", TerminalState.SUCCEEDED)
    assert not start_mutation(connection, "copy", 0)

    begin(connection, "recovery", ["ready"])
    assert start_mutation(connection, "recovery", 0)
    assert require_recovery(connection, "recovery", 0)
    assert not complete(connection, "recovery", TerminalState.FAILED)
    assert complete(connection, "recovery", TerminalState.RECOVERY_REQUIRED)
    assert requires_recovery(connection, "recovery")
    assert undo_candidates(connection, "recovery") == []

    begin(connection, "pre-fail", ["ready"])
    assert fail_before_mutation(connection, "pre-fail", 0)
    assert not requires_recovery(connection, "pre-fail")
    assert complete(connection, "pre-fail", TerminalState.FAILED)

    try:
        begin(connection, "directory-ready", ["ready"], [True])
    except ValueError:
        pass
    else:
        raise AssertionError("ready directory mutation must be rejected")
    assert connection.execute(
        "SELECT COUNT(*) FROM actions WHERE operation_id = 'directory-ready'"
    ).fetchone()[0] == 0

    begin(connection, "directory-skip", ["skip"], [True])
    assert states(connection, "directory-skip") == [EntryState.SKIPPED]
    assert undo_candidates(connection, "directory-skip") == []
    return 22


@dataclass
class ModelEntry:
    state: EntryState
    is_directory: bool = False
    undo: UndoKind = UndoKind.NONE
    identity: str | None = None


def check_randomized(cases: int) -> int:
    rng = random.Random(20260808)
    checks = 0
    for case in range(cases):
        count = rng.randint(1, 12)
        directories = [rng.random() < 0.2 for _ in range(count)]
        decisions = [
            "skip" if is_directory or rng.random() < 0.25 else "ready"
            for is_directory in directories
        ]
        model = [
            ModelEntry(
                EntryState.SKIPPED if decision == "skip" else EntryState.PENDING,
                is_directory=is_directory,
            )
            for decision, is_directory in zip(decisions, directories, strict=True)
        ]
        connection = db()
        operation_id = f"op-{case}"
        begin(connection, operation_id, decisions, directories)

        for ordinal, entry in enumerate(model):
            if entry.state is EntryState.SKIPPED:
                continue
            assert not entry.is_directory
            action = rng.choice(["commit", "pre-fail", "recovery", "leave"])
            if action == "pre-fail":
                assert fail_before_mutation(connection, operation_id, ordinal)
                entry.state = EntryState.FAILED
            elif action in {"commit", "recovery", "leave"}:
                assert start_mutation(connection, operation_id, ordinal)
                entry.state = EntryState.MUTATION_STARTED
                if action == "commit":
                    identity = f"1:{case * 100 + ordinal}"
                    assert commit_copy(connection, operation_id, ordinal, identity)
                    entry.state = EntryState.COMMITTED
                    entry.undo = UndoKind.DELETE_CREATED_DESTINATION
                    entry.identity = identity
                elif action == "recovery":
                    assert require_recovery(connection, operation_id, ordinal)
                    entry.state = EntryState.RECOVERY_REQUIRED
            checks += 1

        actual = states(connection, operation_id)
        assert actual == [entry.state for entry in model]
        expected_recovery = any(
            entry.state in {EntryState.MUTATION_STARTED, EntryState.RECOVERY_REQUIRED}
            for entry in model
        )
        assert requires_recovery(connection, operation_id) == expected_recovery
        expected_undo = [
            ordinal
            for ordinal, entry in enumerate(model)
            if not entry.is_directory
            and entry.state is EntryState.COMMITTED
            and entry.undo is UndoKind.DELETE_CREATED_DESTINATION
            and entry.identity is not None
        ]
        assert undo_candidates(connection, operation_id) == expected_undo
        checks += 3

        if expected_recovery:
            assert not complete(connection, operation_id, TerminalState.FAILED)
            assert complete(connection, operation_id, TerminalState.RECOVERY_REQUIRED)
            checks += 2
        else:
            successful = all(
                entry.state in {EntryState.COMMITTED, EntryState.SKIPPED}
                for entry in model
            )
            assert complete(
                connection,
                operation_id,
                TerminalState.SUCCEEDED if successful else TerminalState.FAILED,
            )
            checks += 1
    return checks


def check_repository(root: Path) -> int:
    files = {
        "contract": root / "src/FileOp.Core/Operations/FileOperationActionHistory.cs",
        "store": root / "src/FileOp.Core/Operations/SqliteFileOperationActionHistoryStore.cs",
        "validation": root / "src/FileOp.Core/Operations/FileOperationExecutionValidation.cs",
        "execution": root / "src/FileOp.Core/Operations/FileOperationExecution.cs",
        "tests": root / "tests/FileOp.Windows.Tests/FileOperationActionHistoryTests.cs",
        "docs": root / "docs/file-operation-action-history.md",
        "local": root / "tools/test-local.ps1",
    }
    missing = [str(path) for path in files.values() if not path.is_file()]
    if missing:
        raise FileNotFoundError(", ".join(missing))
    source = {name: path.read_text(encoding="utf-8") for name, path in files.items()}

    required_contract = [
        "public enum FileOperationActionEntryState",
        "MutationStarted",
        "RecoveryRequired",
        "public enum FileOperationUndoKind",
        "DeleteCreatedDestination",
        "public bool IsUndoCandidate",
        "UndoCandidateEntries",
        "var entrySnapshot = entries.ToArray();",
        "entry.Entry.IsDirectory &&",
        "entry.State != FileOperationActionEntryState.Skipped",
        "schema v1 supports mutation state only for files",
        "public bool RequiresRecovery",
        "public interface IFileOperationActionHistoryStore",
        "MarkMutationStartedAsync",
        "MarkEntryFailedBeforeMutationAsync",
        "CommitCopyAsync",
        "MarkMutationRecoveryRequiredAsync",
    ]
    for needle in required_contract:
        assert needle in source["contract"], needle

    required_store = [
        "PRAGMA synchronous = FULL;",
        "CREATE TABLE IF NOT EXISTS file_operation_actions(",
        "CREATE TABLE IF NOT EXISTS file_operation_action_entries(",
        "FOREIGN KEY(operation_id) REFERENCES file_operation_actions(operation_id) ON DELETE CASCADE",
        "validation.CanBeginMutation",
        "FileOperationCanonicalPathState.Missing",
        "FileOperationActionEntryState.MutationStarted",
        "FileOperationActionEntryState.RecoveryRequired",
        "FileOperationUndoKind.DeleteCreatedDestination",
        "destination_volume_serial",
        "destination_file_reference",
        "action.terminal_state IS NULL",
        "transaction.Commit();",
        "unchecked((long)value)",
        "unchecked((ulong)value)",
        "new FileOperationActionHistory(",
    ]
    for needle in required_store:
        assert needle in source["store"], needle

    for forbidden in [
        "File.Copy(", "File.Move(", "File.Delete(",
        "Directory.Move(", "Directory.Delete(", "File.OpenWrite(",
    ]:
        assert forbidden not in source["contract"] + source["store"], forbidden

    assert "public interface IFileOperationExecutionValidator" in source["validation"]
    assert "public interface IFileOperationExecutor" in source["execution"]
    assert "SqliteFileOperationActionHistoryStore : IFileOperationExecutor" not in source["store"]
    assert "CopyCommitBecomesDurableUndoCandidate" in source["tests"]
    assert "MutationStartedSurvivesAsRecoverySignal" in source["tests"]
    assert "BeginRejectsReadyDirectoryMutation" in source["tests"]
    assert "ActionHistoryDefensivelySnapshotsEntries" in source["tests"]
    assert "# Durable file-operation action history" in source["docs"]
    assert "commit barrier" in source["docs"].casefold()
    assert "undo candidate" in source["docs"].casefold()
    assert "not sufficient" in source["docs"].casefold()
    assert "verify_file_operation_action_history.py" in source["local"]
    return len(required_contract) + len(required_store) + 6 + 11


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--self-test-only", action="store_true")
    parser.add_argument("--cases", type=int, default=20000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error("--cases must be greater than zero")

    fixed = check_fixed()
    randomized = check_randomized(args.cases)
    print(
        f"PASS file operation action-history properties: {fixed + randomized} checks "
        f"across {args.cases} randomized cases"
    )
    if not args.self_test_only:
        print(
            "PASS file operation action-history source wiring: "
            f"{check_repository(args.repo_root.resolve())} checks"
        )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError, sqlite3.DatabaseError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
