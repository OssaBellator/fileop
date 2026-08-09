#!/usr/bin/env python3
"""Zero-Actions model/source checks for Copy owner/group/DACL recovery evidence."""
from __future__ import annotations

import argparse
import random
import sqlite3
import sys
from enum import Enum, auto
from pathlib import Path
from typing import Optional, Tuple

MASK = 0x00000007


class Status(Enum):
    NO_RECORDED = auto()
    SAME = auto()
    DIFFERENT = auto()
    UNAVAILABLE = auto()


def compare(recorded: Optional[Tuple[int, str]], current: Optional[Tuple[int, str]]) -> Status:
    if recorded is None:
        return Status.NO_RECORDED
    if current is None:
        return Status.UNAVAILABLE
    return Status.SAME if recorded == current else Status.DIFFERENT


def random_digest(rng: random.Random) -> str:
    return "".join(rng.choice("0123456789abcdef") for _ in range(64))


def run_model(cases: int) -> int:
    rng = random.Random(20260821)
    checks = 0
    for _ in range(cases):
        recorded = (MASK, random_digest(rng))
        current = recorded if rng.random() < 0.5 else (MASK, random_digest(rng))
        expected = Status.SAME if recorded == current else Status.DIFFERENT
        assert compare(recorded, current) is expected
        assert compare(None, current) is Status.NO_RECORDED
        assert compare(recorded, None) is Status.UNAVAILABLE
        assert compare(recorded, (MASK ^ 0x1, recorded[1])) is Status.DIFFERENT
        checks += 4
        if expected is Status.SAME:
            assert recorded[0] == MASK and current[0] == MASK
            assert len(recorded[1]) == 64
            checks += 2
    return checks


def run_sqlite_model(cases: int) -> int:
    rng = random.Random(20260822)
    db = sqlite3.connect(":memory:")
    db.execute("PRAGMA foreign_keys=ON")
    db.executescript(
        """
        CREATE TABLE entries(operation_id TEXT, ordinal INTEGER, state INTEGER,
            destination_identity TEXT, PRIMARY KEY(operation_id, ordinal)) WITHOUT ROWID;
        CREATE TABLE fingerprints(operation_id TEXT, ordinal INTEGER, digest TEXT,
            PRIMARY KEY(operation_id, ordinal),
            FOREIGN KEY(operation_id, ordinal) REFERENCES entries(operation_id, ordinal)) WITHOUT ROWID;
        CREATE TABLE hard_links(operation_id TEXT, ordinal INTEGER,
            hard_link_count INTEGER CHECK(hard_link_count BETWEEN 1 AND 4294967295),
            PRIMARY KEY(operation_id, ordinal),
            FOREIGN KEY(operation_id, ordinal) REFERENCES entries(operation_id, ordinal)) WITHOUT ROWID;
        CREATE TABLE metadata(operation_id TEXT, ordinal INTEGER,
            creation INTEGER, last_access INTEGER, last_write INTEGER,
            attributes INTEGER CHECK(attributes BETWEEN 0 AND 4294967295),
            PRIMARY KEY(operation_id, ordinal),
            FOREIGN KEY(operation_id, ordinal) REFERENCES entries(operation_id, ordinal)) WITHOUT ROWID;
        CREATE TABLE security(operation_id TEXT, ordinal INTEGER,
            security_information INTEGER CHECK(security_information=7),
            digest TEXT CHECK(length(digest)=64),
            PRIMARY KEY(operation_id, ordinal),
            FOREIGN KEY(operation_id, ordinal) REFERENCES entries(operation_id, ordinal)) WITHOUT ROWID;
        """
    )
    checks = 0
    for index in range(cases):
        op = "op-{}".format(index)
        security_digest = random_digest(rng)
        content_digest = random_digest(rng)
        count = rng.randint(1, 2**32 - 1)
        attrs = rng.getrandbits(32)
        times = tuple(rng.randint(-(2**63), 2**63 - 1) for _ in range(3))
        db.execute("INSERT INTO entries VALUES (?,0,1,NULL)", (op,))
        db.commit()
        db.execute("BEGIN IMMEDIATE")
        db.execute("UPDATE entries SET state=2,destination_identity='id' WHERE operation_id=? AND ordinal=0 AND state=1", (op,))
        db.execute("INSERT INTO fingerprints VALUES (?,0,?)", (op, content_digest))
        db.execute("INSERT INTO hard_links VALUES (?,0,?)", (op, count))
        db.execute("INSERT INTO metadata VALUES (?,0,?,?,?,?)", (op, times[0], times[1], times[2], attrs))
        db.execute("INSERT INTO security VALUES (?,0,7,?)", (op, security_digest))
        db.commit()
        row = db.execute(
            "SELECT e.state,f.digest,h.hard_link_count,m.attributes,s.security_information,s.digest "
            "FROM entries e JOIN fingerprints f USING(operation_id,ordinal) "
            "JOIN hard_links h USING(operation_id,ordinal) "
            "JOIN metadata m USING(operation_id,ordinal) "
            "JOIN security s USING(operation_id,ordinal) WHERE e.operation_id=?",
            (op,),
        ).fetchone()
        assert row == (2, content_digest, count, attrs, MASK, security_digest)
        checks += 6

    db.execute("INSERT INTO entries VALUES ('rollback',0,1,NULL)")
    db.commit()
    db.executescript("""
        CREATE TRIGGER fail_security BEFORE INSERT ON security
        WHEN NEW.operation_id='rollback'
        BEGIN SELECT RAISE(ABORT, 'fixture'); END;
    """)
    try:
        db.execute("BEGIN IMMEDIATE")
        db.execute("UPDATE entries SET state=2,destination_identity='id' WHERE operation_id='rollback'")
        db.execute("INSERT INTO fingerprints VALUES ('rollback',0,?)", ("a" * 64,))
        db.execute("INSERT INTO hard_links VALUES ('rollback',0,1)")
        db.execute("INSERT INTO metadata VALUES ('rollback',0,1,2,3,32)")
        db.execute("INSERT INTO security VALUES ('rollback',0,7,?)", ("b" * 64,))
        raise AssertionError("security trigger should abort")
    except sqlite3.IntegrityError:
        db.rollback()
    assert db.execute("SELECT state,destination_identity FROM entries WHERE operation_id='rollback'").fetchone() == (1, None)
    for table in ("fingerprints", "hard_links", "metadata", "security"):
        assert db.execute("SELECT 1 FROM {} WHERE operation_id='rollback'".format(table)).fetchone() is None
    checks += 5
    db.close()
    return checks


def require(source: str, needles: Tuple[str, ...], folded: bool = False) -> int:
    haystack = source.casefold() if folded else source
    for needle in needles:
        candidate = needle.casefold() if folded else needle
        assert candidate in haystack, needle
    return len(needles)


def check_repository(root: Path) -> int:
    paths = {
        "semantics": root / "src/FileOp.Core/Operations/FileOperationSecurityDescriptorEvidence.cs",
        "contract": root / "src/FileOp.Core/Operations/FileCopyDestinationSecurityDescriptorEvidence.cs",
        "verifier": root / "src/FileOp.Core/Operations/FileOperationRecoverySecurityDescriptorVerification.cs",
        "store": root / "src/FileOp.Core/Operations/SqliteFileOperationActionHistorySecurityDescriptorEvidenceStore.cs",
        "digest": root / "src/FileOp.Windows/Operations/WindowsFileSecurityDescriptorDigest.cs",
        "commit": root / "src/FileOp.Windows/Operations/WindowsRootBoundFileCommitSecurityDescriptorEvidenceSource.cs",
        "current": root / "src/FileOp.Windows/Operations/WindowsRootBoundFileSecurityDescriptorEvidenceReader.cs",
        "bridge": root / "src/FileOp.Windows/Operations/WindowsFileOperationActionHistorySecurityDescriptorEvidenceStore.cs",
        "store_tests": root / "tests/FileOp.Windows.Tests/FileOperationActionHistorySecurityDescriptorEvidenceTests.cs",
        "verifier_tests": root / "tests/FileOp.Windows.Tests/FileOperationRecoverySecurityDescriptorVerificationTests.cs",
        "commit_tests": root / "tests/FileOp.Windows.Tests/WindowsRootBoundFileCommitSecurityDescriptorEvidenceSourceTests.cs",
        "reader_tests": root / "tests/FileOp.Windows.Tests/WindowsRootBoundFileSecurityDescriptorEvidenceReaderTests.cs",
        "bridge_tests": root / "tests/FileOp.Windows.Tests/WindowsFileOperationActionHistorySecurityDescriptorEvidenceStoreTests.cs",
        "docs": root / "docs/file-operation-recovery-security-descriptor-evidence.md",
        "py": root / "tools/test-copy-executor-local.py",
        "ps": root / "tools/test-copy-executor-local.ps1",
        "win": root / "tools/test-windows-copy-local.ps1",
    }
    missing = [str(path) for path in paths.values() if not path.is_file()]
    if missing:
        raise FileNotFoundError(", ".join(missing))
    src = {name: path.read_text(encoding="utf-8") for name, path in paths.items()}
    checks = 0

    checks += require(src["semantics"], (
        "OwnerSecurityInformation = 0x00000001u",
        "GroupSecurityInformation = 0x00000002u",
        "DaclSecurityInformation = 0x00000004u",
        "securityInformation != QueriedSecurityInformationMask",
        "SameQueriedDescriptorBytes",
    ))
    checks += require(src["contract"], (
        "IFileCopyDestinationCommitSecurityDescriptorEvidenceSource",
        "IFileOperationActionHistorySecurityDescriptorEvidenceStore",
        "CommitCopyWithSecurityDescriptorEvidenceAsync",
        "GetDestinationSecurityDescriptorEvidenceAsync",
    ))
    checks += require(src["verifier"], (
        "GetAsync(inspection.OperationId",
        "MatchesDurableHistory(",
        "GetDestinationSecurityDescriptorEvidenceAsync(",
        "IsConsistentSuccess(",
    ))

    checks += require(src["store"], (
        "file_operation_action_entry_security_descriptor_evidence",
        "CHECK(security_information = 7)",
        "sha256_hex_digest TEXT NOT NULL CHECK(length(sha256_hex_digest) = 64)",
        "file_operation_action_entry_content_fingerprints",
        "file_operation_action_entry_hard_link_evidence",
        "file_operation_action_entry_basic_metadata_evidence",
        "transaction.Commit();",
        "FileOperationUndoKind.None",
    ), folded=True)
    for forbidden_column in (
        "owner_sid",
        "group_sid",
        "dacl_blob",
        "descriptor_blob",
        "security_descriptor_bytes",
        "ace_blob",
    ):
        assert forbidden_column not in src["store"].casefold(), forbidden_column
        checks += 1

    checks += require(src["digest"], (
        "GetKernelObjectSecurity(",
        "QueriedSecurityInformationMask",
        "SHA256.HashData",
        "ErrorInsufficientBuffer = 122",
        "SetLastError = true",
        "CallingConvention = CallingConvention.Winapi",
        "MarshalAs(UnmanagedType.Bool)",
    ))
    for name in ("digest", "commit", "current"):
        for forbidden in (
            "AccessSystemSecurity",
            "SaclSecurityInformation",
            "AdjustTokenPrivileges",
            "LookupPrivilegeValue",
            "SetKernelObjectSecurity",
            "File.Delete(",
            "File.Move(",
            "CanDelete",
            "CanUndo",
        ):
            assert forbidden not in src[name], (name, forbidden)
            checks += 1

    for name in ("commit", "current"):
        checks += require(src[name], (
            "ReadControl = 0x00020000",
            "RootDirectory = rootDirectory.DangerousGetHandle()",
            "NtCreateFile(",
            "WindowsFileSecurityDescriptorDigest.Read(leafHandle)",
            "GetFileInformationByHandle(",
            "GetFinalPathNameByHandleW(",
        ))
        for forbidden in ("FileReadData", "ReadFile("):
            assert forbidden not in src[name], (name, forbidden)
            checks += 1
    assert "FileShare.ReadWrite" in src["commit"]
    assert "FileShare.Read" in src["current"]
    checks += 2

    checks += require(src["bridge"], (
        "ReadVerifiedEvidenceAsync(",
        "CommitCopyWithSecurityDescriptorEvidenceAsync(",
        "MarkMutationRecoveryRequiredWithSecurityDescriptorEvidenceAsync(",
        "pending.DestinationIdentity != identity",
        "pending.DestinationContentFingerprint != fingerprint",
    ))

    for name, tests in {
        "store_tests": (
            "SecurityAwareCommitPersistsAllEvidenceAcrossReopen",
            "SecurityInsertFailureRollsBackStateAndAllEarlierEvidenceRows",
        ),
        "verifier_tests": (
            "MatchingDigestIsReportedAsExactQueriedByteMatch",
            "MismatchedDurableHistorySkipsSecurityRowAndReader",
        ),
        "commit_tests": (
            "SourceCapturesStableOwnerGroupDaclDigestWithExistingEvidence",
            "ReplacedRootWithSameFileMovedBackFailsClosed",
        ),
        "reader_tests": (
            "ReaderObservesStableOwnerGroupDaclDigest",
            "MissingLeafFailsClosed",
        ),
        "bridge_tests": (
            "FailedStrongCommitReusesExactSecurityObservationForRecovery",
            "MismatchedRecoveryEvidenceDowngradesToNoPartialProof",
        ),
    }.items():
        for test in tests:
            assert test in src[name], test
            checks += 1

    checks += require(src["docs"], (
        "0x00000007",
        "READ_CONTROL",
        "SACL/audit evidence is deliberately excluded",
        "Raw security-descriptor bytes, SIDs, ACEs and ACL contents are never written",
        "not semantic ACL equivalence",
        "point-in-time evidence",
        "explicit user authorization",
    ), folded=True)

    assert "verify_recovery_security_descriptor_evidence.py" in src["py"]
    assert "verify_recovery_security_descriptor_evidence.py" in src["ps"]
    checks += 2
    for filter_name in (
        "FileSecurityDescriptorEvidenceTests",
        "FileOperationActionHistorySecurityDescriptorEvidenceTests",
        "FileOperationRecoverySecurityDescriptorVerificationTests",
        "WindowsRootBoundFileCommitSecurityDescriptorEvidenceSourceTests",
        "WindowsRootBoundFileSecurityDescriptorEvidenceReaderTests",
        "WindowsFileOperationActionHistorySecurityDescriptorEvidenceStoreTests",
    ):
        assert filter_name in src["win"], filter_name
        checks += 1

    for name in ("semantics", "verifier", "store", "bridge"):
        for forbidden in ("CanDelete", "CanUndo"):
            assert forbidden not in src[name], (name, forbidden)
            checks += 1
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--self-test-only", action="store_true")
    parser.add_argument("--cases", type=int, default=50000)
    parser.add_argument("--sqlite-cases", type=int, default=5000)
    args = parser.parse_args()
    if args.cases <= 0 or args.sqlite_cases <= 0:
        parser.error("case counts must be greater than zero")
    model_checks = run_model(args.cases)
    sqlite_checks = run_sqlite_model(args.sqlite_cases)
    total = model_checks + sqlite_checks
    print("PASS recovery security-descriptor evidence model: {} checks across {} randomized cases + {} SQLite cases + rollback".format(total, args.cases, args.sqlite_cases))
    if not args.self_test_only:
        print("PASS recovery security-descriptor source wiring: {} checks".format(check_repository(args.repo_root.resolve())))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError, sqlite3.DatabaseError) as exc:
        print("FAIL: {}".format(exc), file=sys.stderr)
        raise SystemExit(1)
