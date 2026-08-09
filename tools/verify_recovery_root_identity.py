#!/usr/bin/env python3
"""Zero-Actions model/source checks for durable Copy recovery root identity evidence."""
from __future__ import annotations

import argparse
import random
import sqlite3
import sys
from enum import Enum, auto
from pathlib import Path


class RootStatus(Enum):
    NO_IDENTITY = auto()
    MISSING = auto()
    SAME = auto()
    DIFFERENT = auto()
    REDIRECTED = auto()
    REPARSE = auto()
    UNEXPECTED_TYPE = auto()
    INACCESSIBLE = auto()
    ERROR = auto()


class ContentGate(Enum):
    NO_FINGERPRINT = auto()
    ROOT_NOT_VERIFIED = auto()
    FILE_NOT_SAME = auto()
    READ = auto()


def gate(has_fingerprint: bool, root_status: RootStatus, file_same: bool) -> ContentGate:
    if not has_fingerprint:
        return ContentGate.NO_FINGERPRINT
    if root_status is not RootStatus.SAME:
        return ContentGate.ROOT_NOT_VERIFIED
    if not file_same:
        return ContentGate.FILE_NOT_SAME
    return ContentGate.READ


def run_model(cases: int) -> int:
    rng = random.Random(20260813)
    checks = 0
    verified_statuses = tuple(status for status in RootStatus if status is not RootStatus.NO_IDENTITY)
    for _ in range(cases):
        has_recorded_roots = rng.random() < 0.8
        root_status = rng.choice(verified_statuses) if has_recorded_roots else RootStatus.NO_IDENTITY
        has_fingerprint = rng.random() < 0.75
        file_same = rng.random() < 0.6
        result = gate(has_fingerprint, root_status, file_same)

        assert (root_status is RootStatus.NO_IDENTITY) == (not has_recorded_roots)
        checks += 1
        assert (result is ContentGate.READ) == (
            has_fingerprint and root_status is RootStatus.SAME and file_same
        )
        checks += 1
        if not has_fingerprint:
            assert result is ContentGate.NO_FINGERPRINT
            checks += 1
        elif root_status is not RootStatus.SAME:
            assert result is ContentGate.ROOT_NOT_VERIFIED
            checks += 1
        elif not file_same:
            assert result is ContentGate.FILE_NOT_SAME
            checks += 1
        else:
            assert result is ContentGate.READ
            checks += 1
    return checks


def to_sqlite(value: int) -> int:
    return value if value < (1 << 63) else value - (1 << 64)


def from_sqlite(value: int) -> int:
    return value if value >= 0 else value + (1 << 64)


def run_sqlite_model(cases: int = 5000) -> int:
    rng = random.Random(20260814)
    connection = sqlite3.connect(":memory:")
    connection.execute("PRAGMA foreign_keys = ON")
    connection.executescript(
        """
        CREATE TABLE actions(operation_id TEXT PRIMARY KEY) WITHOUT ROWID;
        CREATE TABLE roots(
            operation_id TEXT PRIMARY KEY,
            source_volume INTEGER NOT NULL,
            source_reference INTEGER NOT NULL,
            destination_volume INTEGER NOT NULL,
            destination_reference INTEGER NOT NULL,
            FOREIGN KEY(operation_id) REFERENCES actions(operation_id) ON DELETE CASCADE
        ) WITHOUT ROWID;
        """
    )
    checks = 0
    for index in range(cases):
        operation_id = f"op-{index}"
        connection.execute("INSERT INTO actions VALUES (?)", (operation_id,))
        source = (rng.getrandbits(64), rng.getrandbits(64))
        destination = (rng.getrandbits(64), rng.getrandbits(64))
        connection.execute(
            "INSERT INTO roots VALUES (?, ?, ?, ?, ?)",
            (
                operation_id,
                to_sqlite(source[0]),
                to_sqlite(source[1]),
                to_sqlite(destination[0]),
                to_sqlite(destination[1]),
            ),
        )
        row = connection.execute(
            "SELECT source_volume, source_reference, destination_volume, destination_reference "
            "FROM roots WHERE operation_id = ?",
            (operation_id,),
        ).fetchone()
        restored_source = (from_sqlite(row[0]), from_sqlite(row[1]))
        restored_destination = (from_sqlite(row[2]), from_sqlite(row[3]))
        assert restored_source == source
        assert restored_destination == destination
        checks += 2

        if index % 5 == 0:
            connection.execute("DELETE FROM roots WHERE operation_id = ?", (operation_id,))
            row = connection.execute(
                "SELECT roots.source_volume FROM actions "
                "LEFT JOIN roots ON roots.operation_id = actions.operation_id "
                "WHERE actions.operation_id = ?",
                (operation_id,),
            ).fetchone()
            assert row == (None,)
            checks += 1
    connection.close()
    return checks


def check_repository(root: Path) -> int:
    paths = {
        "history": root / "src/FileOp.Core/Operations/FileOperationActionHistory.cs",
        "store": root / "src/FileOp.Core/Operations/SqliteFileOperationActionHistoryStore.cs",
        "inspection": root / "src/FileOp.Core/Operations/FileOperationRecoveryInspection.cs",
        "content": root / "src/FileOp.Core/Operations/FileOperationRecoveryContentVerification.cs",
        "store_tests": root / "tests/FileOp.Windows.Tests/FileOperationActionHistoryTestsRootIdentity.cs",
        "inspection_tests": root / "tests/FileOp.Windows.Tests/FileOperationRecoveryInspectionTestsRootIdentity.cs",
        "integration_tests": root / "tests/FileOp.Windows.Tests/WindowsFileOperationRecoveryContentVerificationTests.cs",
        "docs": root / "docs/file-operation-recovery-root-identity.md",
        "py_wrapper": root / "tools/test-copy-executor-local.py",
        "ps_wrapper": root / "tools/test-copy-executor-local.ps1",
    }
    missing = [str(path) for path in paths.values() if not path.is_file()]
    if missing:
        raise FileNotFoundError(", ".join(missing))
    source = {name: path.read_text(encoding="utf-8") for name, path in paths.items()}

    history_needles = (
        "FileIdentity? SourceDirectoryIdentity = null",
        "FileIdentity? DestinationDirectoryIdentity = null",
        "SourceDirectoryIdentity.HasValue != DestinationDirectoryIdentity.HasValue",
        "public bool HasVerifiedRootIdentities",
    )
    for needle in history_needles:
        assert needle in source["history"], needle

    store_needles = (
        "private const int SchemaVersion = 1;",
        "file_operation_action_root_identities",
        "PersistRootIdentitiesAsync(",
        "SourceDirectoryIdentity: sourceDirectoryIdentity",
        "DestinationDirectoryIdentity: destinationDirectoryIdentity",
        "LEFT JOIN file_operation_action_root_identities AS roots",
        "sourceDirectoryIdentity = ReadIdentity(reader, 11, 12)",
        "destinationDirectoryIdentity = ReadIdentity(reader, 13, 14)",
        "!validation.SourceDirectory.Identity.HasValue",
        "!validation.DestinationDirectory.Identity.HasValue",
    )
    for needle in store_needles:
        assert needle in source["store"], needle

    begin_start = source["store"].index("public async ValueTask<FileOperationActionHistory> BeginAsync(")
    begin_end = source["store"].index("public ValueTask<FileOperationActionHistory> MarkMutationStartedAsync(")
    begin_body = source["store"][begin_start:begin_end]
    assert "using var transaction = connection.BeginTransaction();" in begin_body
    assert "PersistRootIdentitiesAsync(" in begin_body
    assert begin_body.index("PersistRootIdentitiesAsync(") < begin_body.index("transaction.Commit();")

    inspection_needles = (
        "public enum FileOperationRecoveryRootStatus",
        "NoVerifiedIdentity",
        "public sealed record FileOperationRecoveryRootInspection",
        "public FileOperationRecoveryRootInspection DestinationDirectory",
        "history.DestinationDirectoryIdentity is not FileIdentity expected",
        "FileOperationRecoveryRootStatus.SameObject",
        "FileOperationRecoveryRootStatus.DifferentObject",
        "FileOperationRecoveryRootStatus.Redirected",
        "namespace evidence only",
    )
    for needle in inspection_needles:
        assert needle.casefold() in source["inspection"].casefold(), needle

    content_needles = (
        "DestinationRootNotVerified",
        "inspection.DestinationDirectory.IsSameRecordedRoot",
        "Main-stream verification is skipped because the destination root is not verified",
    )
    for needle in content_needles:
        assert needle in source["content"], needle

    for test_name in (
        "BeginPersistsHighBitRootIdentitiesAcrossReopen",
        "LegacyHistoryWithoutRootSideRowLoadsAsUnverified",
        "BeginRejectsMissingRootIdentityBeforeWritingHistory",
        "HistoryRejectsHalfRootIdentityEvidence",
    ):
        assert test_name in source["store_tests"], test_name

    for test_name in (
        "LegacyHistoryDoesNotResolveRootWithoutDurableIdentity",
        "SameAndDifferentRootIdentityAreDistinguished",
        "RootUnsafeStatesAreClassifiedConservatively",
    ):
        assert test_name in source["inspection_tests"], test_name

    for test_name in (
        "StableSameObjectAndDigestProducesMainStreamMatchEvidence",
        "ReplacedRootWithSameFileMovedBackIsEvidenceInsufficient",
    ):
        assert test_name in source["integration_tests"], test_name

    for needle in (
        "point-in-time",
        "legacy",
        "no migration",
        "same file object",
        "final handle-bound authorization protocol",
        "hard-link",
        "not a held namespace lock",
    ):
        assert needle.casefold() in source["docs"].casefold(), needle

    verifier_name = "verify_recovery_root_identity.py"
    assert verifier_name in source["py_wrapper"]
    assert verifier_name in source["ps_wrapper"]

    forbidden = (
        "CanDelete",
        "CanUndo",
        "File.Delete(",
        "File.Move(",
        "Directory.Delete(",
        "Directory.Move(",
    )
    for needle in forbidden:
        assert needle not in source["inspection"], needle
        assert needle not in source["content"], needle

    return (
        len(history_needles)
        + len(store_needles)
        + 3
        + len(inspection_needles)
        + len(content_needles)
        + 4
        + 3
        + 2
        + 7
        + 2
        + (len(forbidden) * 2)
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--self-test-only", action="store_true")
    parser.add_argument("--cases", type=int, default=50000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error("--cases must be greater than zero")

    model_checks = run_model(args.cases)
    sqlite_checks = run_sqlite_model()
    total = model_checks + sqlite_checks
    print(
        f"PASS recovery root identity model: {total} checks across "
        f"{args.cases} randomized evidence cases + 5000 SQLite identity cases"
    )
    if not args.self_test_only:
        print(
            "PASS recovery root identity source wiring: "
            f"{check_repository(args.repo_root.resolve())} checks"
        )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError, sqlite3.DatabaseError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
