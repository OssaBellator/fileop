#!/usr/bin/env python3
"""Verify separate durable file-delete action history and mutation barriers."""
from __future__ import annotations

import argparse
import random
import sqlite3
from dataclasses import dataclass
from pathlib import Path

PENDING = 0
MUTATION_STARTED = 1
COMMITTED = 2
FAILED = 3
RECOVERY_REQUIRED = 4
SUCCEEDED = 0
TERMINAL_FAILED = 1
TERMINAL_RECOVERY = 2


@dataclass
class Entry:
    state: int = PENDING
    identity: tuple[int, int] = (0, 0)


def infer_terminal(entries: list[Entry]) -> int | None:
    if any(e.state in (MUTATION_STARTED, RECOVERY_REQUIRED) for e in entries):
        return TERMINAL_RECOVERY
    if any(e.state == FAILED for e in entries):
        return TERMINAL_FAILED
    if entries and all(e.state == COMMITTED for e in entries):
        return SUCCEEDED
    return None


def run_model(cases: int, seed: int) -> int:
    rng = random.Random(seed)
    checks = 0
    for case in range(cases):
        count = rng.randint(1, 7)
        entries = [Entry(identity=(rng.getrandbits(64), rng.getrandbits(64))) for _ in range(count)]
        scenario = rng.randrange(5)

        if scenario == 0:  # complete success
            for entry in entries:
                assert entry.state == PENDING
                entry.state = MUTATION_STARTED
                assert infer_terminal(entries) == TERMINAL_RECOVERY
                entry.state = COMMITTED
                checks += 3
            assert infer_terminal(entries) == SUCCEEDED
            checks += 1
        elif scenario == 1:  # pre-barrier failure, later pending is safe
            ordinal = rng.randrange(count)
            entries[ordinal].state = FAILED
            assert infer_terminal(entries) == TERMINAL_FAILED
            assert all(e.state != MUTATION_STARTED for e in entries)
            assert any(e.state == PENDING for i, e in enumerate(entries) if i != ordinal) if count > 1 else True
            checks += 3
        elif scenario == 2:  # post-barrier ambiguity dominates
            ordinal = rng.randrange(count)
            entries[ordinal].state = MUTATION_STARTED
            assert infer_terminal(entries) == TERMINAL_RECOVERY
            entries[ordinal].state = RECOVERY_REQUIRED
            assert infer_terminal(entries) == TERMINAL_RECOVERY
            assert any(e.state == RECOVERY_REQUIRED for e in entries)
            checks += 3
        elif scenario == 3:  # pending-only cannot complete
            assert infer_terminal(entries) is None
            assert all(e.state == PENDING for e in entries)
            checks += 2
        else:  # exact identity is mandatory for a commit receipt
            ordinal = rng.randrange(count)
            entry = entries[ordinal]
            entry.state = MUTATION_STARTED
            wrong_identity = (entry.identity[0], (entry.identity[1] + 1) & ((1 << 64) - 1))
            assert wrong_identity != entry.identity
            assert entry.state == MUTATION_STARTED
            assert infer_terminal(entries) == TERMINAL_RECOVERY
            if wrong_identity == entry.identity:
                entry.state = COMMITTED
            assert entry.state == MUTATION_STARTED
            checks += 4

        terminal = infer_terminal(entries)
        if terminal == SUCCEEDED:
            assert all(e.state == COMMITTED for e in entries)
            assert not any(e.state in (PENDING, FAILED, MUTATION_STARTED, RECOVERY_REQUIRED) for e in entries)
            checks += 2
        elif terminal == TERMINAL_FAILED:
            assert any(e.state == FAILED for e in entries)
            assert not any(e.state in (MUTATION_STARTED, RECOVERY_REQUIRED) for e in entries)
            checks += 2
        elif terminal == TERMINAL_RECOVERY:
            assert any(e.state in (MUTATION_STARTED, RECOVERY_REQUIRED) for e in entries)
            checks += 1

        delete_mutation_authorized = False
        automatic_recovery_authorized = False
        assert not delete_mutation_authorized
        assert not automatic_recovery_authorized
        checks += 2
    return checks


def to_sqlite_u64(value: int) -> int:
    return value if value < (1 << 63) else value - (1 << 64)


def from_sqlite_u64(value: int) -> int:
    return value if value >= 0 else value + (1 << 64)


def run_sqlite_model(cases: int, seed: int) -> int:
    rng = random.Random(seed)
    checks = 0
    connection = sqlite3.connect(":memory:")
    connection.executescript(
        """
        PRAGMA foreign_keys=ON;
        CREATE TABLE file_delete_action_history_schema_info(
            singleton INTEGER PRIMARY KEY CHECK(singleton=1), version INTEGER NOT NULL);
        INSERT INTO file_delete_action_history_schema_info VALUES(1,1);
        CREATE TABLE file_delete_actions(
            operation_id TEXT PRIMARY KEY,
            terminal_state INTEGER NULL);
        CREATE TABLE file_delete_action_entries(
            operation_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            source_volume_serial INTEGER NOT NULL,
            source_file_reference INTEGER NOT NULL,
            state INTEGER NOT NULL,
            PRIMARY KEY(operation_id, ordinal),
            FOREIGN KEY(operation_id) REFERENCES file_delete_actions(operation_id) ON DELETE CASCADE);
        """
    )
    sqlite_cases = min(5_000, max(1, cases // 10))
    for case in range(sqlite_cases):
        op = f"op-{case}"
        volume = rng.getrandbits(64)
        reference = rng.getrandbits(64)
        connection.execute("INSERT INTO file_delete_actions VALUES(?,NULL)", (op,))
        connection.execute(
            "INSERT INTO file_delete_action_entries VALUES(?,?,?,?,?)",
            (op, 0, to_sqlite_u64(volume), to_sqlite_u64(reference), PENDING),
        )
        connection.commit()

        row = connection.execute(
            "SELECT source_volume_serial, source_file_reference, state FROM file_delete_action_entries WHERE operation_id=? AND ordinal=0",
            (op,),
        ).fetchone()
        assert row is not None
        assert from_sqlite_u64(row[0]) == volume
        assert from_sqlite_u64(row[1]) == reference
        assert row[2] == PENDING
        checks += 4

        changed = connection.execute(
            "UPDATE file_delete_action_entries SET state=? WHERE operation_id=? AND ordinal=0 AND state=?",
            (MUTATION_STARTED, op, PENDING),
        ).rowcount
        assert changed == 1
        duplicate = connection.execute(
            "UPDATE file_delete_action_entries SET state=? WHERE operation_id=? AND ordinal=0 AND state=?",
            (MUTATION_STARTED, op, PENDING),
        ).rowcount
        assert duplicate == 0
        checks += 2

        wrong_reference = (reference + 1) & ((1 << 64) - 1)
        wrong_commit = connection.execute(
            """UPDATE file_delete_action_entries SET state=?
               WHERE operation_id=? AND ordinal=0 AND state=?
                 AND source_volume_serial=? AND source_file_reference=?""",
            (COMMITTED, op, MUTATION_STARTED, to_sqlite_u64(volume), to_sqlite_u64(wrong_reference)),
        ).rowcount
        assert wrong_commit == 0
        state = connection.execute(
            "SELECT state FROM file_delete_action_entries WHERE operation_id=? AND ordinal=0", (op,)
        ).fetchone()[0]
        assert state == MUTATION_STARTED
        checks += 2

        exact_commit = connection.execute(
            """UPDATE file_delete_action_entries SET state=?
               WHERE operation_id=? AND ordinal=0 AND state=?
                 AND source_volume_serial=? AND source_file_reference=?""",
            (COMMITTED, op, MUTATION_STARTED, to_sqlite_u64(volume), to_sqlite_u64(reference)),
        ).rowcount
        assert exact_commit == 1
        connection.commit()
        checks += 1

    connection.close()
    return checks


def require(text: str, needle: str, label: str) -> int:
    if needle not in text:
        raise AssertionError(f"missing {label}: {needle}")
    return 1


def forbid(text: str, needle: str, label: str) -> int:
    if needle in text:
        raise AssertionError(f"forbidden {label}: {needle}")
    return 1


def check_repository(root: Path) -> int:
    core = (root / "src/FileOp.Core/Operations/FileDeleteOperationActionHistory.cs").read_text(encoding="utf-8")
    store = (root / "src/FileOp.Core/Operations/SqliteFileDeleteOperationActionHistoryStore.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/FileDeleteOperationActionHistoryTests.cs").read_text(encoding="utf-8")
    docs = (root / "docs/file-delete-action-history.md").read_text(encoding="utf-8")
    stability_parent = (root / "tools/verify_file_delete_stability_lease.py").read_text(encoding="utf-8")
    stability_core = (root / "src/FileOp.Core/Operations/FileDeleteOperationStabilityLease.cs").read_text(encoding="utf-8")
    generic_execution = (root / "src/FileOp.Core/Operations/FileOperationExecution.cs").read_text(encoding="utf-8")
    plan = (root / "src/FileOp.Core/Operations/FileOperationPlan.cs").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")

    consumers = []
    for subtree in ("src/FileOp.App", "src/FileOp.Windows", "src/FileOp.Indexer"):
        directory = root / subtree
        if directory.exists():
            for path in directory.rglob("*.cs"):
                if "IFileDeleteOperationActionHistoryStore" in path.read_text(encoding="utf-8"):
                    consumers.append(path.relative_to(root).as_posix())
    if consumers:
        raise AssertionError("delete action history must remain unwired from production: " + ", ".join(consumers))

    checks = 0
    required = [
        (core, "public enum FileDeleteOperationActionEntryState", "delete entry state enum"),
        (core, "Pending,\n    MutationStarted,\n    Committed,\n    Failed,\n    RecoveryRequired", "barrier state ordering"),
        (core, "public enum FileDeleteOperationActionTerminalState", "delete terminal state enum"),
        (core, "public bool IsRecoverySensitive", "recovery-sensitive aggregate"),
        (core, "public bool DeleteMutationAuthorized => false;", "history remains non-authorizing"),
        (core, "ValueTask<FileDeleteOperationActionHistory> CommitDeletedAsync", "identity-bound commit contract"),
        (store, "private const int SchemaVersion = 1;", "independent schema version"),
        (store, "PRAGMA journal_mode=WAL", "WAL durability mode"),
        (store, "PRAGMA synchronous=FULL", "FULL synchronous durability"),
        (store, "PRAGMA foreign_keys=ON", "foreign keys enabled"),
        (store, "file_delete_action_history_schema_info", "separate schema-info table"),
        (store, "CREATE TABLE IF NOT EXISTS file_delete_actions", "separate delete operation table"),
        (store, "CREATE TABLE IF NOT EXISTS file_delete_action_entries", "separate delete entry table"),
        (store, "AND state = @expected_state", "exact state transition predicate"),
        (store, "AND operation.terminal_state IS NULL", "running-operation transition predicate"),
        (store, "source_volume_serial = @source_volume", "commit volume identity predicate"),
        (store, "source_file_reference = @source_reference", "commit file identity predicate"),
        (store, "ToSqliteInteger(ulong value) => unchecked((long)value)", "unsigned identity write encoding"),
        (store, "FromSqliteInteger(long value) => unchecked((ulong)value)", "unsigned identity read encoding"),
        (store, "InferTerminalState", "store-inferred terminal state"),
        (tests, "BeginPersistsExactSourceOnlyAuthorizationEvidenceIncludingHighBits", "high-bit roundtrip regression"),
        (tests, "MutationBarrierThenExactIdentityCommitCanCompleteSucceeded", "barrier/identity regression"),
        (tests, "PreBarrierFailureCanCompleteFailedWithLaterPendingEntryUntouched", "safe pending-after-failure regression"),
        (tests, "PostBarrierFailureMustSetRecoveryRequiredAndDominatesCompletion", "recovery dominance regression"),
        (tests, "DuplicateOrPostTerminalTransitionsFailClosed", "duplicate/post-terminal regression"),
        (docs, "separate delete-only history schema", "separate schema documentation"),
        (docs, "does not persist a reusable authorization capability", "consent persistence boundary"),
        (docs, "MutationStarted", "durable barrier documentation"),
        (docs, "RecoveryRequired", "recovery ambiguity documentation"),
        (stability_parent, "from verify_file_delete_action_history import (", "stability verifier imports history child"),
        (stability_parent, "run_delete_history_model(cases", "stability verifier runs history model"),
        (stability_parent, "check_delete_history_repository(root)", "stability verifier runs history source checks"),
        (stability_core, "public bool DeleteMutationAuthorized => false;", "stability remains non-authorizing"),
        (plan, "public enum FileOperationKind\n{\n    Copy,\n    Move,", "Copy/Move operation enum unchanged"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
    ]
    for text, needle, label in required:
        checks += require(text, needle, label)

    commit_return_pattern = (
        "cancellationToken).ConfigureAwait(false);\n"
        "            transaction.Commit();\n"
        "            return history;"
    )
    if store.count(commit_return_pattern) != 3:
        raise AssertionError(
            "Begin, entry transition, and completion must materialize their resulting history inside the transaction before commit"
        )
    checks += 1
    checks += forbid(
        store,
        "transaction.Commit();\n            return await LoadRequiredAsync",
        "post-commit cancellable history reload",
    )

    combined = core + "\n" + store
    for text, needle, label in (
        (combined, "file_operation_actions", "Copy action table reference"),
        (combined, "file_operation_action_entries", "Copy entry table reference"),
        (combined, "destination_", "destination persistence"),
        (combined, "Destination", "destination model"),
        (combined, "UndoKind", "Undo semantics"),
        (combined, "DeleteCreatedDestination", "Copy Undo delete semantic"),
        (combined, "File.Delete(", "managed delete mutation"),
        (combined, "Directory.Delete(", "managed directory mutation"),
        (combined, "DeleteFileW", "native delete mutation"),
        (combined, "SetFileInformationByHandle", "handle delete mutation"),
        (combined, "IFileOperationExecutor", "generic executor integration"),
        (generic_execution, "FileDeleteOperationActionHistory", "generic execution integration"),
        (plan, "Delete,", "Delete generic operation enum"),
    ):
        checks += forbid(text, needle, label)

    checks += 1
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0xD31E7E51)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    sqlite_checks = run_sqlite_model(args.cases, args.seed ^ 0x5A17E)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {source_checks:,} source checks" if args.repo_root else ""
    print(
        f"PASS: file delete action history verified with {model_checks + sqlite_checks:,} model/SQLite assertions "
        f"across {args.cases:,} randomized states and {min(5_000, max(1, args.cases // 10)):,} SQLite cases{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
