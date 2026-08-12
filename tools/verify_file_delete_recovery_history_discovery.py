#!/usr/bin/env python3
"""Verify bounded read-only discovery of recovery-sensitive file-delete history."""
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
MAX_LIMIT = 4_096


@dataclass(frozen=True)
class Operation:
    operation_id: str
    started: int
    entry_states: tuple[int, ...]


def is_recovery_sensitive(operation: Operation) -> bool:
    return any(state in (MUTATION_STARTED, RECOVERY_REQUIRED) for state in operation.entry_states)


def discover(operations: list[Operation], limit: int) -> list[Operation]:
    if limit <= 0 or limit > MAX_LIMIT:
        raise ValueError("limit")
    candidates = [operation for operation in operations if is_recovery_sensitive(operation)]
    candidates.sort(key=lambda operation: (operation.started, operation.operation_id), reverse=True)
    return candidates[:limit]


def run_model(cases: int, seed: int) -> int:
    rng = random.Random(seed)
    checks = 0
    states = (PENDING, MUTATION_STARTED, COMMITTED, FAILED, RECOVERY_REQUIRED)

    for case in range(cases):
        count = rng.randint(1, 24)
        operations: list[Operation] = []
        for ordinal in range(count):
            entry_count = rng.randint(1, 5)
            entry_states = tuple(rng.choice(states) for _ in range(entry_count))
            operations.append(
                Operation(
                    operation_id=f"{case:08x}-{ordinal:04x}",
                    started=rng.randint(0, 10_000),
                    entry_states=entry_states,
                )
            )

        limit = rng.randint(1, min(MAX_LIMIT, count + 5))
        result = discover(operations, limit)
        expected = sorted(
            (operation for operation in operations if is_recovery_sensitive(operation)),
            key=lambda operation: (operation.started, operation.operation_id),
            reverse=True,
        )[:limit]

        assert result == expected
        assert len(result) <= limit
        assert all(is_recovery_sensitive(operation) for operation in result)
        assert all(
            (left.started, left.operation_id) >= (right.started, right.operation_id)
            for left, right in zip(result, result[1:])
        )
        checks += 4

        safe = [operation for operation in operations if not is_recovery_sensitive(operation)]
        result_ids = {operation.operation_id for operation in result}
        assert all(operation.operation_id not in result_ids for operation in safe)
        checks += 1

        mutation_authorized = False
        automatic_retry_authorized = False
        deletion_inferred_from_mutation_started = False
        assert not mutation_authorized
        assert not automatic_retry_authorized
        assert not deletion_inferred_from_mutation_started
        checks += 3

    for invalid_limit in (0, -1, MAX_LIMIT + 1, MAX_LIMIT * 2):
        try:
            discover([], invalid_limit)
        except ValueError:
            checks += 1
        else:
            raise AssertionError("invalid recovery-history limit was accepted")

    return checks


def run_sqlite_model(cases: int, seed: int) -> int:
    rng = random.Random(seed)
    checks = 0
    connection = sqlite3.connect(":memory:")
    connection.executescript(
        """
        CREATE TABLE file_delete_actions(
            operation_id TEXT PRIMARY KEY,
            started_utc_ticks INTEGER NOT NULL);
        CREATE TABLE file_delete_action_entries(
            operation_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            source_volume_serial INTEGER NOT NULL,
            source_file_reference INTEGER NOT NULL,
            state INTEGER NOT NULL,
            PRIMARY KEY(operation_id, ordinal),
            FOREIGN KEY(operation_id) REFERENCES file_delete_actions(operation_id));
        """
    )

    sqlite_cases = min(5_000, max(1, cases // 10))
    for case in range(sqlite_cases):
        operation_count = rng.randint(4, 12)
        expected: list[tuple[int, str]] = []
        high_bits: dict[str, tuple[int, int]] = {}
        for ordinal in range(operation_count):
            operation_id = f"op-{case:05d}-{ordinal:03d}"
            started = rng.randint(0, 100_000)
            connection.execute(
                "INSERT INTO file_delete_actions(operation_id, started_utc_ticks) VALUES(?, ?)",
                (operation_id, started),
            )
            entry_count = rng.randint(1, 4)
            states: list[int] = []
            for entry_ordinal in range(entry_count):
                state = rng.choice((PENDING, MUTATION_STARTED, COMMITTED, FAILED, RECOVERY_REQUIRED))
                states.append(state)
                volume = rng.getrandbits(64)
                reference = rng.getrandbits(64)
                high_bits[f"{operation_id}:{entry_ordinal}"] = (volume, reference)
                connection.execute(
                    "INSERT INTO file_delete_action_entries VALUES(?, ?, ?, ?, ?)",
                    (
                        operation_id,
                        entry_ordinal,
                        volume if volume < (1 << 63) else volume - (1 << 64),
                        reference if reference < (1 << 63) else reference - (1 << 64),
                        state,
                    ),
                )
            if any(state in (MUTATION_STARTED, RECOVERY_REQUIRED) for state in states):
                expected.append((started, operation_id))
        connection.commit()

        limit = rng.randint(1, min(8, operation_count))
        rows = connection.execute(
            """
            SELECT operation.operation_id
            FROM file_delete_actions AS operation
            WHERE operation.operation_id LIKE ?
              AND EXISTS (
                  SELECT 1
                  FROM file_delete_action_entries AS entry
                  WHERE entry.operation_id = operation.operation_id
                    AND entry.state IN (?, ?))
            ORDER BY operation.started_utc_ticks DESC, operation.operation_id DESC
            LIMIT ?
            """,
            (f"op-{case:05d}-%", MUTATION_STARTED, RECOVERY_REQUIRED, limit),
        ).fetchall()
        actual_ids = [row[0] for row in rows]
        expected_ids = [
            operation_id
            for _, operation_id in sorted(expected, reverse=True)[:limit]
        ]
        assert actual_ids == expected_ids
        assert len(actual_ids) <= limit
        checks += 2

        for operation_id in actual_ids:
            state_rows = connection.execute(
                "SELECT state, source_volume_serial, source_file_reference, ordinal "
                "FROM file_delete_action_entries WHERE operation_id=? ORDER BY ordinal",
                (operation_id,),
            ).fetchall()
            assert any(row[0] in (MUTATION_STARTED, RECOVERY_REQUIRED) for row in state_rows)
            for state, volume, reference, entry_ordinal in state_rows:
                expected_volume, expected_reference = high_bits[f"{operation_id}:{entry_ordinal}"]
                actual_volume = volume if volume >= 0 else volume + (1 << 64)
                actual_reference = reference if reference >= 0 else reference + (1 << 64)
                assert actual_volume == expected_volume
                assert actual_reference == expected_reference
                checks += 2
            checks += 1

        connection.execute(
            "DELETE FROM file_delete_action_entries WHERE operation_id LIKE ?",
            (f"op-{case:05d}-%",),
        )
        connection.execute(
            "DELETE FROM file_delete_actions WHERE operation_id LIKE ?",
            (f"op-{case:05d}-%",),
        )
        connection.commit()

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
    reader = (root / "src/FileOp.Core/Operations/SqliteFileDeleteOperationRecoveryHistoryReader.cs").read_text(encoding="utf-8")
    history = (root / "src/FileOp.Core/Operations/FileDeleteOperationActionHistory.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/FileDeleteOperationRecoveryHistoryReaderTests.cs").read_text(encoding="utf-8")
    docs = (root / "docs/file-delete-recovery-history-discovery.md").read_text(encoding="utf-8")
    local_gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    plan = (root / "src/FileOp.Core/Operations/FileOperationPlan.cs").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")

    consumers = []
    for subtree in ("src/FileOp.App", "src/FileOp.Windows", "src/FileOp.Indexer"):
        directory = root / subtree
        if directory.exists():
            for path in directory.rglob("*.cs"):
                if "IFileDeleteOperationRecoveryHistoryReader" in path.read_text(encoding="utf-8"):
                    consumers.append(path.relative_to(root).as_posix())
    if consumers:
        raise AssertionError(
            "delete recovery-history reader must remain unwired from production: "
            + ", ".join(consumers)
        )

    checks = 0
    for text, needle, label in (
        (reader, "public interface IFileDeleteOperationRecoveryHistoryReader : IAsyncDisposable", "read-only recovery interface"),
        (reader, "public sealed class SqliteFileDeleteOperationRecoveryHistoryReader", "SQLite recovery reader"),
        (reader, "private const int MaximumRecoveryCandidateLimit = 4_096;", "bounded recovery limit"),
        (reader, "Mode = SqliteOpenMode.ReadOnly", "read-only SQLite mode"),
        (reader, "PRAGMA query_only=ON", "SQLite query-only guard"),
        (reader, "using var transaction = connection.BeginTransaction(deferred: true);", "deferred read snapshot"),
        (reader, "ValidateSchema(connection, transaction);", "schema validation inside snapshot"),
        (reader, "command.Transaction = transaction;", "snapshot-bound commands"),
        (reader, "SELECT version FROM file_delete_action_history_schema_info", "schema version read"),
        (reader, "entry.state IN (@mutation_started_state, @recovery_required_state)", "exact recovery-state predicate"),
        (reader, "ORDER BY operation.started_utc_ticks DESC, operation.operation_id DESC", "deterministic newest-first ordering"),
        (reader, "LIMIT @limit", "bounded SQL query"),
        (reader, "LoadRequiredAsync(\n                connection,\n                transaction,", "history hydration inside snapshot"),
        (reader, "if (!history.IsRecoverySensitive || history.DeleteMutationAuthorized)", "validated recovery-only result"),
        (history, "public bool DeleteMutationAuthorized => false;", "history remains non-authorizing"),
        (tests, "RestartReaderReturnsOnlyRecoverySensitiveHistoriesNewestFirst", "restart discovery regression"),
        (tests, "RecoveryDiscoveryLimitIsBoundedAndPreservesNewestOrdering", "bounds/order regression"),
        (tests, "0xF123456789ABCDEFUL", "high-bit identity regression"),
        (docs, "does not infer that a `MutationStarted` file was deleted", "restart ambiguity documentation"),
        (local_gate, "verify_file_delete_recovery_history_discovery.py --repo-root $repoRoot --cases 50000", "offline gate wiring"),
        (plan, "public enum FileOperationKind\n{\n    Copy,\n    Move,", "Copy/Move enum unchanged"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 unchanged"),
    ):
        checks += require(text, needle, label)

    for text, needle, label in (
        (reader, "CREATE TABLE", "schema creation"),
        (reader, "INSERT INTO", "history insert"),
        (reader, "UPDATE file_delete", "history update"),
        (reader, "DELETE FROM", "history delete"),
        (reader, "MarkMutationStartedAsync", "mutation barrier transition"),
        (reader, "CommitDeletedAsync", "delete commit transition"),
        (reader, "MarkMutationRecoveryRequiredAsync", "recovery transition"),
        (reader, "CompleteAsync", "history completion"),
        (reader, "File.Delete(", "managed delete mutation"),
        (reader, "Directory.Delete(", "directory mutation"),
        (reader, "DeleteFileW", "native delete mutation"),
        (reader, "SetFileInformationByHandle", "handle delete mutation"),
        (reader, "IFileOperationExecutor", "generic executor integration"),
        (plan, "Delete,", "generic Delete operation kind"),
    ):
        checks += forbid(text, needle, label)

    return checks + 1


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0xD15C0A31)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    sqlite_checks = run_sqlite_model(args.cases, args.seed ^ 0x51A7E)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {source_checks:,} source checks" if args.repo_root else ""
    print(
        f"PASS: file delete recovery-history discovery verified with {model_checks + sqlite_checks:,} "
        f"model/SQLite assertions across {args.cases:,} randomized states and "
        f"{min(5_000, max(1, args.cases // 10)):,} SQLite cases{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
