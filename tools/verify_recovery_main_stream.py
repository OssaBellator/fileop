#!/usr/bin/env python3
"""Zero-Actions model/source checks for read-only recovery main-stream verification."""
from __future__ import annotations

import argparse
import hashlib
import random
import sys
from enum import Enum, auto
from pathlib import Path


class Inspection(Enum):
    SAME = auto()
    UNSAFE = auto()


class Read(Enum):
    SUCCESS = auto()
    MISSING = auto()
    DIFFERENT_OBJECT = auto()
    REDIRECTED = auto()
    REPARSE = auto()
    UNEXPECTED_TYPE = auto()
    BUSY = auto()
    INACCESSIBLE = auto()
    ERROR = auto()


class Result(Enum):
    NO_FINGERPRINT = auto()
    NOT_SAME_OBJECT = auto()
    MATCH = auto()
    DIFFERENT_CONTENT = auto()
    MISSING = auto()
    DIFFERENT_OBJECT = auto()
    REDIRECTED = auto()
    REPARSE = auto()
    UNEXPECTED_TYPE = auto()
    BUSY = auto()
    INACCESSIBLE = auto()
    ERROR = auto()


ACCESS_READ = 0x1
ACCESS_WRITE = 0x2
ACCESS_DELETE = 0x4


def classify(
    inspection: Inspection,
    has_fingerprint: bool,
    read: Read,
    recorded: bytes,
    current: bytes,
) -> tuple[Result, bool]:
    if not has_fingerprint:
        return Result.NO_FINGERPRINT, False
    if inspection is not Inspection.SAME:
        return Result.NOT_SAME_OBJECT, False

    mapping = {
        Read.MISSING: Result.MISSING,
        Read.DIFFERENT_OBJECT: Result.DIFFERENT_OBJECT,
        Read.REDIRECTED: Result.REDIRECTED,
        Read.REPARSE: Result.REPARSE,
        Read.UNEXPECTED_TYPE: Result.UNEXPECTED_TYPE,
        Read.BUSY: Result.BUSY,
        Read.INACCESSIBLE: Result.INACCESSIBLE,
        Read.ERROR: Result.ERROR,
    }
    if read is not Read.SUCCESS:
        return mapping[read], True
    return (Result.MATCH if recorded == current else Result.DIFFERENT_CONTENT), True


def share_compatible(
    existing_access: int,
    existing_share: int,
    new_access: int = ACCESS_READ,
    new_share: int = ACCESS_READ,
) -> bool:
    """Model the symmetric Windows desired-access/share-mode compatibility rule."""
    existing_access_allowed_by_new = (existing_access & ~new_share) == 0
    new_access_allowed_by_existing = (new_access & ~existing_share) == 0
    return existing_access_allowed_by_new and new_access_allowed_by_existing


def run_model(cases: int) -> int:
    rng = random.Random(20260809)
    checks = 0
    reads = tuple(Read)
    for _ in range(cases):
        inspection = Inspection.SAME if rng.random() < 0.55 else Inspection.UNSAFE
        has_fingerprint = rng.random() < 0.75
        read = rng.choice(reads)
        payload = rng.randbytes(rng.randrange(0, 4096))
        recorded = hashlib.sha256(payload).digest()
        current_payload = payload if rng.random() < 0.5 else payload + b"changed"
        current = hashlib.sha256(current_payload).digest()

        result, reader_called = classify(
            inspection,
            has_fingerprint,
            read,
            recorded,
            current,
        )
        assert reader_called == (has_fingerprint and inspection is Inspection.SAME)
        checks += 1

        if not has_fingerprint:
            assert result is Result.NO_FINGERPRINT
            checks += 1
            continue
        if inspection is not Inspection.SAME:
            assert result is Result.NOT_SAME_OBJECT
            checks += 1
            continue

        if read is Read.SUCCESS:
            assert result is (Result.MATCH if recorded == current else Result.DIFFERENT_CONTENT)
            checks += 1
        else:
            assert result not in (Result.MATCH, Result.DIFFERENT_CONTENT)
            checks += 1

        if result is Result.MATCH:
            assert read is Read.SUCCESS and recorded == current
            checks += 1
        elif result is Result.DIFFERENT_CONTENT:
            assert read is Read.SUCCESS and recorded != current
            checks += 1
        else:
            assert read is not Read.SUCCESS
            checks += 1
    return checks


def run_share_model(cases: int) -> int:
    rng = random.Random(20260812)
    checks = 0
    for _ in range(cases):
        existing_access = rng.randrange(0, 8)
        existing_share = rng.randrange(0, 8)
        compatible = share_compatible(existing_access, existing_share)

        if existing_access & (ACCESS_WRITE | ACCESS_DELETE):
            assert not compatible
            checks += 1
        elif existing_access & ACCESS_READ:
            assert compatible == bool(existing_share & ACCESS_READ)
            checks += 1
        else:
            assert compatible == bool(existing_share & ACCESS_READ)
            checks += 1

        if compatible:
            assert (existing_access & ~(ACCESS_READ)) == 0
            assert (existing_share & ACCESS_READ) != 0
            checks += 2
        else:
            assert (
                (existing_access & (ACCESS_WRITE | ACCESS_DELETE)) != 0
                or (existing_share & ACCESS_READ) == 0
            )
            checks += 1
    return checks


def check_repository(root: Path) -> int:
    paths = {
        "core": root / "src/FileOp.Core/Operations/FileOperationRecoveryContentVerification.cs",
        "windows": root / "src/FileOp.Windows/Operations/WindowsFileContentFingerprintReader.cs",
        "core_tests": root / "tests/FileOp.Windows.Tests/FileOperationRecoveryContentVerificationTests.cs",
        "windows_tests": root / "tests/FileOp.Windows.Tests/WindowsFileContentFingerprintReaderTests.cs",
        "integration_tests": root / "tests/FileOp.Windows.Tests/WindowsFileOperationRecoveryContentVerificationTests.cs",
        "docs": root / "docs/file-operation-recovery-content-verification.md",
        "py_wrapper": root / "tools/test-copy-executor-local.py",
        "ps_wrapper": root / "tools/test-copy-executor-local.ps1",
        "windows_gate": root / "tools/test-windows-copy-local.ps1",
    }
    missing = [str(path) for path in paths.values() if not path.is_file()]
    if missing:
        raise FileNotFoundError(", ".join(missing))
    source = {name: path.read_text(encoding="utf-8") for name, path in paths.items()}

    core_needles = (
        "public interface IFileContentFingerprintReader",
        "FileOperationRecoveryDestinationStatus.SameObject",
        "DestinationContentFingerprint is not FileContentFingerprint recorded",
        "MatchesRecordedMainStream",
        "DifferentMainStream",
        "FileContentFingerprintReadStatus.Busy",
        "currentFingerprint: null",
        "evidence only",
    )
    for needle in core_needles:
        assert needle.casefold() in source["core"].casefold(), needle

    windows_needles = (
        "class WindowsFileContentFingerprintReader",
        "FileReadData | FileReadAttributes | Synchronize",
        "FileShare.Read",
        "FileFlagOpenReparsePoint",
        "IncrementalHash.CreateHash(HashAlgorithmName.SHA256)",
        "GetFileInformationByHandle(",
        "GetFinalPathNameByHandleW(",
        "ReadFile(",
        "identity != expectedIdentity",
        "FileContentFingerprintReadStatus.Busy",
        "ErrorSharingViolation = 32",
        "FileSize(before) != FileSize(after)",
        "ToUInt64(before.LastWriteTime) != ToUInt64(after.LastWriteTime)",
    )
    for needle in windows_needles:
        assert needle in source["windows"], needle

    forbidden_windows = (
        "File.WriteAll",
        "File.OpenWrite(",
        "File.Delete(",
        "File.Move(",
        "Directory.Delete(",
        "Directory.Move(",
        "WriteFile(",
    )
    for needle in forbidden_windows:
        assert needle not in source["windows"], needle

    for test_name in (
        "MatchingMainStreamRequiresSameObjectAndRemainsEvidenceOnly",
        "DifferentDigestIsDifferentMainStream",
        "MissingFingerprintSkipsStableReader",
        "NonSameObjectInspectionSkipsStableReader",
        "StableReaderUnsafeStatusesPropagateConservatively",
        "ReaderExceptionFailsClosedAsError",
    ):
        assert test_name in source["core_tests"], test_name

    for test_name in (
        "ReaderHashesPrimaryStreamAndPreservesIdentityEvidence",
        "EmptyFileProducesStandardSha256Digest",
        "DifferentExpectedIdentityIsRejectedBeforeHashEvidence",
        "MissingDestinationFailsClosedWithoutFingerprint",
        "DirectoryDestinationFailsClosedWithoutFingerprint",
        "ExistingWriterCausesBusyInsteadOfWeakReadProof",
    ):
        assert test_name in source["windows_tests"], test_name

    for test_name in (
        "StableSameObjectAndDigestProducesMainStreamMatchEvidence",
        "ContentEditAfterSameObjectInspectionIsDetectedBySecondStage",
        "ReplacementAfterSameObjectInspectionIsDetectedBeforeHashEvidence",
    ):
        assert test_name in source["integration_tests"], test_name

    for needle in (
        "main data stream",
        "not deletion authorization",
        "FileShare.Read",
        "sharing",
        "metadata",
        "alternate data streams",
    ):
        assert needle.casefold() in source["docs"].casefold(), needle

    verifier = "verify_recovery_main_stream.py"
    assert verifier in source["py_wrapper"]
    assert verifier in source["ps_wrapper"]
    assert "FullyQualifiedName~FileOperationRecoveryContentVerificationTests" in source["windows_gate"]
    assert "FullyQualifiedName~WindowsFileContentFingerprintReaderTests" in source["windows_gate"]
    assert "FullyQualifiedName~WindowsFileOperationRecoveryContentVerificationTests" in source["windows_gate"]

    forbidden_core = ("CanDelete", "CanUndo", "DeleteCreatedDestination")
    for needle in forbidden_core:
        assert needle not in source["core"], needle

    return (
        len(core_needles)
        + len(windows_needles)
        + len(forbidden_windows)
        + 6
        + 6
        + 3
        + 6
        + 5
        + len(forbidden_core)
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--self-test-only", action="store_true")
    parser.add_argument("--cases", type=int, default=50000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error("--cases must be greater than zero")

    protocol_checks = run_model(args.cases)
    share_checks = run_share_model(args.cases)
    checks = protocol_checks + share_checks
    print(
        f"PASS recovery main-stream verification model: {checks} checks across "
        f"{args.cases} randomized protocol cases + {args.cases} share-compatibility cases"
    )
    if not args.self_test_only:
        print(
            "PASS recovery main-stream source wiring: "
            f"{check_repository(args.repo_root.resolve())} checks"
        )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
