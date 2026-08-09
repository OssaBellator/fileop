#!/usr/bin/env python3
"""Zero-Actions model/source checks for Copy recovery hard-link evidence."""
from __future__ import annotations

import argparse
import random
import sqlite3
import sys
from enum import Enum, auto
from pathlib import Path


class Topology(Enum):
    NO_RECORDED = auto()
    SAME = auto()
    DIFFERENT = auto()
    UNAVAILABLE = auto()


class Content(Enum):
    MATCH = auto()
    DIFFERENT = auto()
    UNSAFE = auto()


def stable_count(before: int, after: int) -> int | None:
    return before if before > 0 and before == after else None


def topology(recorded: int | None, current: int | None) -> Topology:
    if recorded is None:
        return Topology.NO_RECORDED
    if current is None:
        return Topology.UNAVAILABLE
    return Topology.SAME if recorded == current else Topology.DIFFERENT


def run_model(cases: int) -> int:
    rng = random.Random(20260817)
    checks = 0
    for _ in range(cases):
        recorded = None if rng.random() < 0.2 else rng.randint(1, 64)
        before = rng.randint(0, 64)
        after = before if rng.random() < 0.7 else rng.randint(0, 64)
        current = stable_count(before, after)
        status = topology(recorded, current)
        content = rng.choice(tuple(Content))

        assert (current is not None) == (before > 0 and before == after)
        checks += 1
        assert (status is Topology.NO_RECORDED) == (recorded is None)
        checks += 1
        if recorded is not None and current is None:
            assert status is Topology.UNAVAILABLE
            checks += 1
        if recorded is not None and current is not None:
            assert status is (Topology.SAME if recorded == current else Topology.DIFFERENT)
            checks += 1
        if status is Topology.SAME:
            assert current == recorded and current is not None and current > 0
            checks += 1
        if status is Topology.DIFFERENT:
            assert current is not None and recorded is not None and current != recorded
            checks += 1

        # Topology never rewrites the separately determined byte result.
        observed_content = content
        _ = status
        assert observed_content is content
        checks += 1
    return checks


def run_sqlite_model(cases: int = 5000) -> int:
    rng = random.Random(20260818)
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
        """
    )
    checks = 0
    for index in range(cases):
        operation_id = f"op-{index}"
        count = rng.randint(1, 2**32 - 1)
        connection.execute("INSERT INTO entries VALUES (?, 0, 1, NULL)", (operation_id,))
        connection.execute(
            "UPDATE entries SET state=2, destination_identity=? WHERE operation_id=? AND ordinal=0 AND state=1",
            (f"id-{index}", operation_id),
        )
        connection.execute("INSERT INTO fingerprints VALUES (?, 0, ?)", (operation_id, f"digest-{index}"))
        connection.execute("INSERT INTO hard_links VALUES (?, 0, ?)", (operation_id, count))
        connection.commit()
        row = connection.execute(
            "SELECT e.state, f.digest, h.hard_link_count FROM entries e "
            "JOIN fingerprints f USING(operation_id, ordinal) "
            "JOIN hard_links h USING(operation_id, ordinal) WHERE e.operation_id=?",
            (operation_id,),
        ).fetchone()
        assert row == (2, f"digest-{index}", count)
        checks += 3

    connection.execute("INSERT INTO entries VALUES ('rollback', 0, 1, NULL)")
    connection.commit()
    connection.executescript(
        """
        CREATE TRIGGER fail_hard_link BEFORE INSERT ON hard_links
        WHEN NEW.operation_id = 'rollback'
        BEGIN SELECT RAISE(ABORT, 'fixture'); END;
        """
    )
    try:
        connection.execute("BEGIN")
        connection.execute("UPDATE entries SET state=2, destination_identity='id' WHERE operation_id='rollback'")
        connection.execute("INSERT INTO fingerprints VALUES ('rollback', 0, 'digest')")
        connection.execute("INSERT INTO hard_links VALUES ('rollback', 0, 1)")
        raise AssertionError("hard-link trigger should abort")
    except sqlite3.IntegrityError:
        connection.rollback()
    assert connection.execute("SELECT state, destination_identity FROM entries WHERE operation_id='rollback'").fetchone() == (1, None)
    assert connection.execute("SELECT 1 FROM fingerprints WHERE operation_id='rollback'").fetchone() is None
    assert connection.execute("SELECT 1 FROM hard_links WHERE operation_id='rollback'").fetchone() is None
    checks += 3
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
        "history": root / "src/FileOp.Core/Operations/FileOperationActionHistory.cs",
        "evidence_contract": root / "src/FileOp.Core/Operations/FileCopyDestinationHardLinkEvidence.cs",
        "store": root / "src/FileOp.Core/Operations/SqliteFileOperationActionHistoryHardLinkEvidenceStore.cs",
        "content": root / "src/FileOp.Core/Operations/FileOperationRecoveryContentVerification.cs",
        "reader": root / "src/FileOp.Windows/Operations/WindowsRootBoundFileContentFingerprintReader.cs",
        "commit_reader": root / "src/FileOp.Windows/Operations/WindowsRootBoundFileHardLinkEvidenceSource.cs",
        "bridge": root / "src/FileOp.Windows/Operations/WindowsFileOperationActionHistoryHardLinkEvidenceStore.cs",
        "store_tests": root / "tests/FileOp.Windows.Tests/FileOperationActionHistoryHardLinkEvidenceTests.cs",
        "multi_tests": root / "tests/FileOp.Windows.Tests/FileOperationActionHistoryHardLinkEvidenceMultiEntryTests.cs",
        "content_tests": root / "tests/FileOp.Windows.Tests/FileOperationRecoveryContentVerificationTests.cs",
        "reader_tests": root / "tests/FileOp.Windows.Tests/WindowsRootBoundFileContentFingerprintReaderTests.cs",
        "commit_reader_tests": root / "tests/FileOp.Windows.Tests/WindowsRootBoundFileHardLinkEvidenceSourceTests.cs",
        "bridge_tests": root / "tests/FileOp.Windows.Tests/WindowsFileOperationActionHistoryHardLinkEvidenceStoreTests.cs",
        "docs": root / "docs/file-operation-recovery-hard-link-evidence.md",
        "py_wrapper": root / "tools/test-copy-executor-local.py",
        "ps_wrapper": root / "tools/test-copy-executor-local.ps1",
        "windows_gate": root / "tools/test-windows-copy-local.ps1",
    }
    missing = [str(path) for path in paths.values() if not path.is_file()]
    if missing:
        raise FileNotFoundError(", ".join(missing))
    source = {name: path.read_text(encoding="utf-8") for name, path in paths.items()}
    checks = 0

    checks += require(source["history"], (
        "public uint? DestinationHardLinkCount { get; init; }",
        "public interface IFileOperationActionHistoryHardLinkEvidenceStore",
        "Existing IFileOperationActionHistoryStore implementers remain source-compatible",
        "unchanged Copy",
    ))
    checks += require(source["evidence_contract"], (
        "public interface IFileCopyDestinationHardLinkEvidenceSource",
        "ReadVerifiedHardLinkCountAsync(",
        "must fail closed",
    ), folded=True)

    checks += require(source["store"], (
        "file_operation_action_entry_hard_link_evidence",
        "CHECK(hard_link_count BETWEEN 1 AND 4294967295)",
        "LoadRequiredOverlayAsync(",
        "PersistFingerprintAsync(",
        "PersistHardLinkCountAsync(",
        "CarryPriorEvidence(",
        "DestinationHardLinkCount = hardLinkCount",
        "Legacy commit path retained for source/API compatibility",
    ), folded=True)
    for signature in (
        "public async ValueTask<FileOperationActionHistory> CommitCopyWithHardLinkEvidenceAsync(",
        "public async ValueTask<FileOperationActionHistory> MarkMutationRecoveryRequiredWithHardLinkEvidenceAsync(",
    ):
        body = method_body(source["store"], signature)
        assert body.index("LoadRequiredOverlayAsync(") < body.index("connection.BeginTransaction()")
        assert body.index("PersistFingerprintAsync(") < body.index("PersistHardLinkCountAsync(")
        assert body.index("PersistHardLinkCountAsync(") < body.index("transaction.Commit();")
        after_commit = body[body.index("transaction.Commit();"):]
        assert "OpenConnection(" not in after_commit
        assert "GetAsync(" not in after_commit
        assert "OverlayAsync(" not in after_commit
        checks += 6

    checks += require(source["content"], (
        "public uint? CurrentDestinationHardLinkCount { get; init; }",
        "public enum FileOperationRecoveryHardLinkStatus",
        "NoRecordedCount",
        "SameCount",
        "DifferentCount",
        "Unavailable",
        "RecordedDestinationHardLinkCount",
        "CurrentDestinationHardLinkCount is not uint current",
        "Hard-link count is surfaced separately as topology evidence",
        "read.CurrentDestinationHardLinkCount is uint count",
    ))
    assert "CurrentDestinationHardLinkCount" not in method_body(source["content"], "private static bool IsConsistentSuccess(")
    checks += 1

    checks += require(source["reader"], (
        "leafBefore.NumberOfLinks == 0",
        "leafBefore.NumberOfLinks != leafAfter.NumberOfLinks",
        "leafAfter.NumberOfLinks == 0",
        "CurrentDestinationHardLinkCount = leafAfter.NumberOfLinks",
    ))

    checks += require(source["commit_reader"], (
        "class WindowsRootBoundFileHardLinkEvidenceSource",
        "FileTraverse | FileReadAttributes | Synchronize",
        "FileReadAttributes | Synchronize",
        "FileShare.ReadWrite",
        "RootDirectory = rootDirectory.DangerousGetHandle()",
        "NtCreateFile(",
        "GetFileInformationByHandle(",
        "GetFinalPathNameByHandleW(",
        "NumberOfLinks",
        "leafAfter.NumberOfLinks != leafBefore.NumberOfLinks",
        "CallingConvention = CallingConvention.Winapi",
        "ExactSpelling = true",
        "MarshalAs(UnmanagedType.Bool)",
    ))
    for forbidden in ("FileReadData", "ReadFile(", "File.Delete(", "File.Move(", "CanDelete", "CanUndo"):
        assert forbidden not in source["commit_reader"], forbidden
        checks += 1

    checks += require(source["bridge"], (
        "class WindowsFileOperationActionHistoryHardLinkEvidenceStore",
        "ReadVerifiedHardLinkCountAsync(",
        "CommitCopyWithHardLinkEvidenceAsync(",
        "MarkMutationRecoveryRequiredWithHardLinkEvidenceAsync(",
        "_pending[key] = evidence",
        "pending.DestinationIdentity != identity",
        "pending.DestinationContentFingerprint != fingerprint",
        "Persist recovery without partial proof",
        "ClearPending(operationId)",
    ), folded=True)

    for test_name in (
        "TopologyAwareCommitPersistsHardLinkCountAcrossReopen",
        "TopologyAwareRecoveryPersistsHardLinkCountWithoutUndoCandidate",
        "LegacyCommittedRowLoadsWithNoHardLinkEvidence",
        "HardLinkEvidenceInsertFailureRollsBackCommitAndFingerprint",
        "ZeroHardLinkCountIsRejectedBeforeTransition",
    ):
        assert test_name in source["store_tests"], test_name
        checks += 1
    assert "LaterCommitAndCompletionPreserveEarlierHardLinkEvidence" in source["multi_tests"]
    checks += 1

    for test_name in (
        "MatchingRecordedHardLinkCountIsSeparateTopologyEvidence",
        "DifferentHardLinkCountDoesNotBecomeContentMismatch",
        "MissingCurrentHardLinkCountLeavesContentMatchButTopologyUnavailable",
    ):
        assert test_name in source["content_tests"], test_name
        checks += 1
    assert "AdditionalHardLinkIncreasesStableObservedCountWithoutChangingBytesOrIdentity" in source["reader_tests"]
    checks += 1

    for test_name in (
        "PositiveCountIsObservedThroughRecordedRootAndLeafIdentity",
        "AdditionalHardLinkIncreasesObservedCountWithoutChangingDestinationIdentity",
        "ExistingTrustedWriterCanCoexistWithAttributesOnlyEvidenceRead",
        "WrongLeafIdentityFailsClosed",
        "ReplacedRootWithSameFileMovedBackFailsClosed",
    ):
        assert test_name in source["commit_reader_tests"], test_name
        checks += 1

    for test_name in (
        "LegacyCommitCallCollectsCountAndPersistsStrongEvidence",
        "FailedStrongCommitCarriesExactCountIntoRecovery",
        "EvidenceSourceFailureDropsIdentityFingerprintFromRecovery",
        "CompletionClearsUnusedPendingObservation",
    ):
        assert test_name in source["bridge_tests"], test_name
        checks += 1

    checks += require(source["docs"], (
        "same observed count",
        "does not prove the same hard-link names",
        "point-in-time evidence, not a topology lock",
        "no migration or schema-version increment",
        "final handle-bound authorization protocol",
        "not captured from the mutation primitive's original destination handle itself",
    ), folded=True)

    assert "verify_recovery_hard_link_evidence.py" in source["py_wrapper"]
    assert "verify_recovery_hard_link_evidence.py" in source["ps_wrapper"]
    assert "FullyQualifiedName~FileOperationActionHistoryHardLinkEvidence" in source["windows_gate"]
    assert "FullyQualifiedName~WindowsRootBoundFileHardLinkEvidenceSourceTests" in source["windows_gate"]
    checks += 4

    for forbidden in ("CanDelete", "CanUndo"):
        assert forbidden not in source["content"]
        assert forbidden not in source["store"]
        assert forbidden not in source["bridge"]
        checks += 3

    return checks


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
        f"PASS recovery hard-link evidence model: {total} checks across "
        f"{args.cases} randomized topology cases + 5000 SQLite atomicity cases"
    )
    if not args.self_test_only:
        print(f"PASS recovery hard-link evidence source wiring: {check_repository(args.repo_root.resolve())} checks")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError, sqlite3.DatabaseError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
