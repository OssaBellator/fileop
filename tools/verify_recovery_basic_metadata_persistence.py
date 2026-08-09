#!/usr/bin/env python3
"""Zero-Actions persistence/source checks for Copy recovery basic metadata evidence."""
from __future__ import annotations

import argparse
import random
import sqlite3
import sys
from pathlib import Path


def to_sqlite(value: int) -> int:
    return value if value < (1 << 63) else value - (1 << 64)


def from_sqlite(value: int) -> int:
    return value if value >= 0 else value + (1 << 64)


def run_sqlite_model(cases: int = 5000) -> int:
    rng = random.Random(20260820)
    connection = sqlite3.connect(":memory:")
    connection.execute("PRAGMA foreign_keys = ON")
    connection.executescript(
        """
        CREATE TABLE entries(
            operation_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            state INTEGER NOT NULL,
            destination_identity TEXT NULL,
            PRIMARY KEY(operation_id, ordinal)
        ) WITHOUT ROWID;
        CREATE TABLE fingerprints(
            operation_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            digest TEXT NOT NULL,
            PRIMARY KEY(operation_id, ordinal),
            FOREIGN KEY(operation_id, ordinal) REFERENCES entries(operation_id, ordinal)
        ) WITHOUT ROWID;
        CREATE TABLE hard_links(
            operation_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            hard_link_count INTEGER NOT NULL CHECK(hard_link_count BETWEEN 1 AND 4294967295),
            PRIMARY KEY(operation_id, ordinal),
            FOREIGN KEY(operation_id, ordinal) REFERENCES entries(operation_id, ordinal)
        ) WITHOUT ROWID;
        CREATE TABLE metadata(
            operation_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            creation INTEGER NOT NULL,
            last_access INTEGER NOT NULL,
            last_write INTEGER NOT NULL,
            attributes INTEGER NOT NULL CHECK(attributes BETWEEN 0 AND 4294967295),
            PRIMARY KEY(operation_id, ordinal),
            FOREIGN KEY(operation_id, ordinal) REFERENCES entries(operation_id, ordinal)
        ) WITHOUT ROWID;
        """
    )
    checks = 0
    for index in range(cases):
        op = f"op-{index}"
        times = tuple(rng.getrandbits(64) for _ in range(3))
        attrs = rng.getrandbits(32)
        count = rng.randint(1, 2**32 - 1)
        connection.execute("INSERT INTO entries VALUES (?, 0, 1, NULL)", (op,))
        connection.execute("BEGIN")
        connection.execute("UPDATE entries SET state=2, destination_identity=? WHERE operation_id=?", (f"id-{index}", op))
        connection.execute("INSERT INTO fingerprints VALUES (?, 0, ?)", (op, f"digest-{index}"))
        connection.execute("INSERT INTO hard_links VALUES (?, 0, ?)", (op, count))
        connection.execute(
            "INSERT INTO metadata VALUES (?, 0, ?, ?, ?, ?)",
            (op, to_sqlite(times[0]), to_sqlite(times[1]), to_sqlite(times[2]), attrs),
        )
        connection.commit()
        row = connection.execute(
            "SELECT e.state, h.hard_link_count, m.creation, m.last_access, m.last_write, m.attributes "
            "FROM entries e JOIN hard_links h USING(operation_id, ordinal) "
            "JOIN metadata m USING(operation_id, ordinal) WHERE e.operation_id=?",
            (op,),
        ).fetchone()
        assert row[0] == 2 and row[1] == count and row[5] == attrs
        assert tuple(from_sqlite(value) for value in row[2:5]) == times
        checks += 5

    connection.execute("INSERT INTO entries VALUES ('rollback', 0, 1, NULL)")
    connection.commit()
    connection.executescript(
        """
        CREATE TRIGGER fail_metadata BEFORE INSERT ON metadata
        WHEN NEW.operation_id='rollback'
        BEGIN SELECT RAISE(ABORT, 'fixture'); END;
        """
    )
    try:
        connection.execute("BEGIN")
        connection.execute("UPDATE entries SET state=2, destination_identity='id' WHERE operation_id='rollback'")
        connection.execute("INSERT INTO fingerprints VALUES ('rollback', 0, 'digest')")
        connection.execute("INSERT INTO hard_links VALUES ('rollback', 0, 1)")
        connection.execute("INSERT INTO metadata VALUES ('rollback', 0, 1, 2, 3, 32)")
        raise AssertionError("metadata trigger should abort")
    except sqlite3.IntegrityError:
        connection.rollback()
    assert connection.execute("SELECT state, destination_identity FROM entries WHERE operation_id='rollback'").fetchone() == (1, None)
    assert connection.execute("SELECT 1 FROM fingerprints WHERE operation_id='rollback'").fetchone() is None
    assert connection.execute("SELECT 1 FROM hard_links WHERE operation_id='rollback'").fetchone() is None
    assert connection.execute("SELECT 1 FROM metadata WHERE operation_id='rollback'").fetchone() is None
    checks += 4
    connection.close()
    return checks


def method_body(source: str, signature: str) -> str:
    start = source.index(signature)
    next_public = source.find("\n    public ", start + len(signature))
    return source[start:] if next_public < 0 else source[start:next_public]


def require(source: str, needles: tuple[str, ...], *, folded: bool = False) -> int:
    haystack = source.casefold() if folded else source
    for needle in needles:
        candidate = needle.casefold() if folded else needle
        assert candidate in haystack, needle
    return len(needles)


def check_repository(root: Path) -> int:
    paths = {
        "semantics": root / "src/FileOp.Core/Operations/FileOperationBasicMetadataEvidence.cs",
        "contract": root / "src/FileOp.Core/Operations/FileCopyDestinationBasicMetadataEvidence.cs",
        "store": root / "src/FileOp.Core/Operations/SqliteFileOperationActionHistoryBasicMetadataEvidenceStore.cs",
        "verifier": root / "src/FileOp.Core/Operations/FileOperationRecoveryBasicMetadataVerification.cs",
        "commit_reader": root / "src/FileOp.Windows/Operations/WindowsRootBoundFileCommitBasicMetadataEvidenceSource.cs",
        "current_reader": root / "src/FileOp.Windows/Operations/WindowsRootBoundFileBasicMetadataEvidenceReader.cs",
        "bridge": root / "src/FileOp.Windows/Operations/WindowsFileOperationActionHistoryBasicMetadataEvidenceStore.cs",
        "store_tests": root / "tests/FileOp.Windows.Tests/FileOperationActionHistoryBasicMetadataEvidenceTests.cs",
        "bridge_tests": root / "tests/FileOp.Windows.Tests/WindowsFileOperationActionHistoryBasicMetadataEvidenceStoreTests.cs",
        "verifier_tests": root / "tests/FileOp.Windows.Tests/FileOperationRecoveryBasicMetadataVerificationTests.cs",
        "commit_reader_tests": root / "tests/FileOp.Windows.Tests/WindowsRootBoundFileCommitBasicMetadataEvidenceSourceTests.cs",
        "current_reader_tests": root / "tests/FileOp.Windows.Tests/WindowsRootBoundFileBasicMetadataEvidenceReaderTests.cs",
        "docs": root / "docs/file-operation-recovery-basic-metadata-evidence.md",
    }
    missing = [str(path) for path in paths.values() if not path.is_file()]
    if missing:
        raise FileNotFoundError(", ".join(missing))
    source = {name: path.read_text(encoding="utf-8") for name, path in paths.items()}
    checks = 0

    checks += require(source["semantics"], (
        "StableCopiedAttributesMask",
        "0x00002027u",
        "LastAccessTimeMatchesDiagnostic",
        "creationMatches && lastWriteMatches && attributesMatch",
    ))
    checks += require(source["contract"], (
        "FileCopyDestinationCommitBasicMetadataEvidence",
        "HardLinkCount = hardLinkCount",
        "IFileCopyDestinationCommitBasicMetadataEvidenceSource",
        "IFileOperationActionHistoryBasicMetadataEvidenceStore",
        "GetDestinationBasicMetadataEvidenceAsync",
    ))

    checks += require(source["store"], (
        "file_operation_action_entry_basic_metadata_evidence",
        "creation_time_filetime",
        "last_access_time_filetime",
        "last_write_time_filetime",
        "file_attributes INTEGER NOT NULL CHECK(file_attributes BETWEEN 0 AND 4294967295)",
        "PersistFingerprintAsync(",
        "PersistHardLinkCountAsync(",
        "PersistBasicMetadataAsync(",
        "FromSqliteInteger",
    ))
    for signature in (
        "public async ValueTask<FileOperationActionHistory> CommitCopyWithBasicMetadataEvidenceAsync(",
        "public async ValueTask<FileOperationActionHistory> MarkMutationRecoveryRequiredWithBasicMetadataEvidenceAsync(",
    ):
        body = method_body(source["store"], signature)
        assert body.index("PersistFingerprintAsync(") < body.index("PersistHardLinkCountAsync(")
        assert body.index("PersistHardLinkCountAsync(") < body.index("PersistBasicMetadataAsync(")
        assert body.index("PersistBasicMetadataAsync(") < body.index("transaction.Commit();")
        after_commit = body[body.index("transaction.Commit();"):]
        assert "OpenConnection(" not in after_commit
        assert "GetAsync(" not in after_commit
        checks += 5

    checks += require(source["verifier"], (
        "GetDestinationBasicMetadataEvidenceAsync(",
        "TryCreateRequest(",
        "IsConsistentSuccess(",
        "FileOperationRecoveryBasicMetadataComparer.Compare(recorded, read.BasicMetadata)",
        "Last-access equality is diagnostic only",
    ), folded=True)

    for name in ("commit_reader", "current_reader"):
        checks += require(source[name], (
            "RootDirectory = rootDirectory.DangerousGetHandle()",
            "NtCreateFile(",
            "GetFileInformationByHandle(",
            "GetFinalPathNameByHandleW(",
            "StableCopiedAttributesMask",
            "CallingConvention = CallingConvention.Winapi",
            "ExactSpelling = true",
            "MarshalAs(UnmanagedType.Bool)",
        ))
        for forbidden in ("ReadFile(", "FileReadData", "File.Delete(", "File.Move(", "CanDelete", "CanUndo"):
            assert forbidden not in source[name], forbidden
            checks += 1

    assert "FileShare.ReadWrite" in source["commit_reader"]
    assert "FileShare.Read" in source["current_reader"]
    assert "ToUInt64(after.LastAccessTime)" in source["commit_reader"]
    assert "ToUInt64(after.LastAccessTime)" in source["current_reader"]
    checks += 4

    checks += require(source["bridge"], (
        "ReadVerifiedEvidenceAsync(",
        "CommitCopyWithBasicMetadataEvidenceAsync(",
        "MarkMutationRecoveryRequiredWithBasicMetadataEvidenceAsync(",
        "pending.DestinationIdentity != identity",
        "pending.DestinationContentFingerprint != fingerprint",
        "ClearPending(operationId)",
    ))

    for test_name in (
        "CombinedCommitPersistsMetadataAndHardLinkEvidenceAcrossReopen",
        "CombinedRecoveryPersistsMetadataWithoutUndoAuthority",
        "HardLinkOnlyCommitLoadsWithNoBasicMetadataEvidence",
        "MetadataInsertFailureRollsBackStateFingerprintAndHardLinkEvidence",
        "CombinedEvidenceRejectsZeroHardLinkCount",
    ):
        assert test_name in source["store_tests"], test_name
        checks += 1
    for test_name in (
        "LegacyCommitCallPersistsCombinedEvidence",
        "FailedCombinedCommitReusesExactMetadataEvidenceForRecovery",
        "EvidenceCollectionFailureDowngradesRecoveryToNoPartialProof",
    ):
        assert test_name in source["bridge_tests"], test_name
        checks += 1
    for test_name in (
        "LastAccessOnlyDifferenceRemainsSameStableMetadata",
        "LastWriteDifferenceIsReportedWithoutMutationAuthority",
        "MissingRecordedMetadataSkipsReader",
        "UnverifiedRootSkipsReaderAndReturnsUnavailable",
        "UnsafeReaderStatusReturnsUnavailable",
        "InconsistentSuccessEvidenceFailsClosedAsUnavailable",
    ):
        assert test_name in source["verifier_tests"], test_name
        checks += 1
    for test_name in (
        "SourceCapturesPositiveCountAndStableBasicMetadata",
        "ExistingTrustedWriterCanCoexistWithCommitMetadataRead",
        "WrongDestinationIdentityFailsClosed",
        "ReplacedRootWithSameFileMovedBackFailsClosed",
    ):
        assert test_name in source["commit_reader_tests"], test_name
        checks += 1
    for test_name in (
        "ReaderObservesCreationLastWriteAndSafeAttributes",
        "ExistingWriterMakesCurrentMetadataUnavailableAsBusy",
        "WrongLeafIdentityFailsClosed",
        "ReplacedRootWithSameFileMovedBackFailsClosed",
    ):
        assert test_name in source["current_reader_tests"], test_name
        checks += 1

    checks += require(source["docs"], (
        "Last-access is diagnostic only",
        "0x00002027",
        "point-in-time evidence, not a metadata/topology lock",
        "second root-bound handle observation",
        "No one status is renamed \"unchanged file\"",
        "explicit user authorization",
    ), folded=True)

    for name in ("semantics", "store", "verifier", "bridge"):
        for forbidden in ("CanDelete", "CanUndo"):
            assert forbidden not in source[name], forbidden
            checks += 1

    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--self-test-only", action="store_true")
    parser.add_argument("--sqlite-cases", type=int, default=5000)
    args = parser.parse_args()
    if args.sqlite_cases <= 0:
        parser.error("--sqlite-cases must be greater than zero")

    sqlite_checks = run_sqlite_model(args.sqlite_cases)
    print(f"PASS recovery basic-metadata persistence model: {sqlite_checks} checks across {args.sqlite_cases} SQLite cases + rollback")
    if not args.self_test_only:
        print(f"PASS recovery basic-metadata persistence/source wiring: {check_repository(args.repo_root.resolve())} checks")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError, sqlite3.DatabaseError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
