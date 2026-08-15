#!/usr/bin/env python3
"""Zero-Actions checks for the beta NTFS-only mutation FileIdentity boundary (#193)."""
from __future__ import annotations

import argparse
import random
import sys
from enum import Enum, auto
from pathlib import Path


class Capability(Enum):
    NTFS = auto()
    UNSUPPORTED = auto()
    UNAVAILABLE = auto()


def mutation_ready(source: Capability, destination: Capability | None = None) -> bool:
    if source is not Capability.NTFS:
        return False
    return destination is None or destination is Capability.NTFS


def run_model(cases: int) -> int:
    checks = 0
    assert mutation_ready(Capability.NTFS, Capability.NTFS)
    assert not mutation_ready(Capability.UNSUPPORTED, Capability.NTFS)
    assert not mutation_ready(Capability.NTFS, Capability.UNSUPPORTED)
    assert not mutation_ready(Capability.UNAVAILABLE, Capability.NTFS)
    assert not mutation_ready(Capability.NTFS, Capability.UNAVAILABLE)
    assert mutation_ready(Capability.NTFS)
    assert not mutation_ready(Capability.UNSUPPORTED)
    assert not mutation_ready(Capability.UNAVAILABLE)
    checks += 8

    rng = random.Random(20260815 ^ 0x193)
    values = list(Capability)
    for _ in range(cases):
        source = rng.choice(values)
        destination = rng.choice(values)
        copy_move = mutation_ready(source, destination)
        delete = mutation_ready(source)
        assert copy_move == (source is Capability.NTFS and destination is Capability.NTFS)
        assert delete == (source is Capability.NTFS)
        if source is not Capability.NTFS:
            assert not copy_move and not delete
            checks += 1
        if destination is not Capability.NTFS:
            assert not copy_move
            checks += 1
        checks += 2
    return checks


def require(text: str, *needles: str) -> int:
    for needle in needles:
        assert needle in text, needle
    return len(needles)


def reject(text: str, *needles: str) -> int:
    for needle in needles:
        assert needle not in text, needle
    return len(needles)


def check_repository(root: Path) -> int:
    paths = {
        "guard": root / "src/FileOp.Windows/Operations/WindowsMutationFilesystemCapability.cs",
        "move": root / "src/FileOp.Windows/Operations/WindowsNtfsMoveOperationExecutionValidator.cs",
        "aliases": root / "src/FileOp.App/MutationExecutionValidatorAliases.cs",
        "tests": root / "tests/FileOp.Windows.Tests/WindowsMutationFilesystemCapabilityBoundaryTests.cs",
        "binding_tests": root / "tests/FileOp.Windows.Tests/WindowsMutationFilesystemCapabilityBindingTests.cs",
        "policy_tests": root / "tests/FileOp.Windows.Tests/WindowsMutationFilesystemCapabilityPolicyTests.cs",
        "resolver": root / "src/FileOp.Windows/Operations/WindowsFileOperationExecutionValidator.cs",
        "gate": root / "tools/test-mutation-filesystem-identity.ps1",
        "product_wiring": root / "tools/verify_mutation_filesystem_product_wiring.py",
        "docs": root / "docs/mutation-filesystem-identity-boundary.md",
    }
    source: dict[str, str] = {}
    for name, path in paths.items():
        if not path.is_file():
            raise FileNotFoundError(path)
        source[name] = path.read_text(encoding="utf-8")

    checks = 0
    guard = source["guard"]
    checks += require(
        guard,
        "WindowsMutationFilesystemCapabilityState",
        "SupportedNtfs",
        "UnsupportedFilesystem",
        "Unavailable",
        "IWindowsMutationFilesystemCapabilityProbe",
        "WindowsMutationFilesystemCapabilityProbe",
        "FileFlagOpenReparsePoint",
        "GetFileInformationByHandle(",
        "observedIdentity != expectedIdentity",
        "TryGetFinalPath(handle",
        "GetVolumeInformationByHandleW(",
        'string.Equals(observedFileSystem, "NTFS"',
        'string.Equals(FileSystemName, "NTFS"',
        "public bool IsBoundTo(",
        "ExpectedIdentity == expectedIdentity",
        "current mutation FileIdentity model is NTFS-only",
        "WindowsNtfsMutationExecutionValidator",
        "validation.SourceDirectory.Identity is not FileIdentity sourceIdentity",
        "validation.DestinationDirectory.Identity is not FileIdentity destinationIdentity",
        "!validation.Items.Any(static item =>",
        "item.Decision == FileOperationExecutionValidationDecision.Ready",
        "CapabilityMatches(",
        "capability.IsBoundTo(canonicalDirectoryPath, expectedIdentity)",
        "not exact NTFS evidence bound to the freshly validated root",
        "The filesystem capability provider returned no evidence.",
        "before durable mutation history",
        "WindowsNtfsFileDeleteOperationExecutionValidator",
        "capability.IsBoundTo(validation.SourceDirectory.CanonicalPath, sourceIdentity)",
        "FileDeleteOperationExecutionValidationDecision.Blocked",
        "before authorization review",
    )
    checks += reject(
        guard,
        "GetVolumeInformationW(",
        "File.Copy(",
        "File.Move(",
        "File.Delete(",
        "Directory.Delete(",
    )

    # The handle identity/path checks must happen before filesystem-name evidence is trusted.
    identity_index = guard.index("observedIdentity != expectedIdentity")
    path_index = guard.index("TryGetFinalPath(handle")
    filesystem_index = guard.index("GetVolumeInformationByHandleW(")
    assert identity_index < filesystem_index
    assert path_index < filesystem_index
    checks += 2

    checks += require(
        source["move"],
        "WindowsNtfsMoveOperationExecutionValidator",
        "new WindowsMoveOperationExecutionValidator()",
        "WindowsNtfsMutationExecutionValidator",
    )

    checks += require(
        source["aliases"],
        "global using WindowsFileOperationExecutionValidator =",
        "FileOp.Windows.Operations.WindowsNtfsMutationExecutionValidator",
        "global using WindowsMoveOperationExecutionValidator =",
        "FileOp.Windows.Operations.WindowsNtfsMoveOperationExecutionValidator",
        "global using WindowsFileDeleteOperationExecutionValidator =",
        "FileOp.Windows.Operations.WindowsNtfsFileDeleteOperationExecutionValidator",
        "Browsing, Search, indexing and read-only preflight are intentionally not narrowed to NTFS",
    )

    checks += require(
        source["tests"],
        "NtfsSourceAndDestinationPreserveReadyCopyValidation",
        "RefsSourceBlocksCopyBeforeDestinationCapabilityIsConsulted",
        "RefsDestinationBlocksCopyAfterSourceNtfsProof",
        "UnavailableFilesystemProofBlocksMutationReadyMove",
        "RefsDeleteRootBlocksEveryReadyItemBeforeAuthorizationReview",
        "NtfsDeleteRootPreservesAuthorizationReviewEvidenceWithoutAuthorizingDelete",
        "RealHandleBoundProbeClassifiesCurrentTempFilesystemAndRejectsStaleIdentity",
        "WindowsMutationFilesystemCapabilityState.UnsupportedFilesystem",
        '"ReFS"',
        "identity changed",
    )
    checks += require(
        source["binding_tests"],
        "SupportedNtfsEvidenceForDifferentIdentityDoesNotAuthorizeCopyValidation",
        "SupportedStateWithNonNtfsFilesystemNameDoesNotAuthorizeCopyValidation",
        "SupportedNtfsEvidenceForDifferentPathDoesNotReachDeleteAuthorizationReview",
        'WindowsMutationFilesystemCapabilityState.SupportedNtfs,\n            "ReFS"',
        "not exact NTFS evidence",
    )
    checks += require(
        source["policy_tests"],
        "SkipOnlyCopyDoesNotRequireFilesystemCapabilityProof",
        "SkipOnlyMoveDoesNotRequireFilesystemCapabilityProof",
        "MissingCapabilityEvidenceBlocksReadyCopyWithoutThrowing",
        "MissingCapabilityEvidenceBlocksDeleteBeforeAuthorizationReviewWithoutThrowing",
        "Assert.AreEqual(0, probe.CallCount)",
        "provider returned no evidence",
        "=> null!;",
    )

    # Pin the reason this guard exists: the current resolver still constructs the mutation
    # identity from the 64-bit BY_HANDLE_FILE_INFORMATION file index. #193 must not pretend
    # that this became a full-width ReFS identity merely because non-NTFS mutation is blocked.
    checks += require(
        source["resolver"],
        "FileIndexHigh",
        "FileIndexLow",
        "new FileIdentity(",
    )

    checks += require(
        source["gate"],
        "verify_mutation_filesystem_identity_boundary.py",
        "verify_mutation_filesystem_product_wiring.py",
        "--cases 50000",
        "WindowsMutationFilesystemCapabilityBoundaryTests",
        "WindowsMutationFilesystemCapabilityBindingTests",
        'FullyQualifiedName~WindowsMutationFilesystemCapability',
        "dotnet test",
        "OfflineOnly",
    )
    checks += require(
        source["product_wiring"],
        'app_root.glob("*.cs")',
        "Unexpected App use of {type_name}",
        "verify_mutation_filesystem_product_wiring.py",
    )
    checks += require(
        source["docs"],
        "NTFS-only",
        "ReFS",
        "GetVolumeInformationByHandleW",
        "BY_HANDLE_FILE_INFORMATION",
        "128-bit",
        "Browsing, Search and indexing",
        "tools/test-local.ps1",
        "tools/test-mutation-filesystem-identity.ps1",
    )
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--cases", type=int, default=50000)
    parser.add_argument("--self-test-only", action="store_true")
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    checks = run_model(args.cases)
    if not args.self_test_only:
        checks += check_repository(args.repo_root.resolve())
    print(f"PASS mutation filesystem identity boundary verification ({checks} checks)")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
