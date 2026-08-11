#!/usr/bin/env python3
"""Verify current physical reclaim evidence without GitHub Actions."""
from __future__ import annotations

import argparse
import random
from pathlib import Path

LONG_MAX = (1 << 63) - 1


def sat_add(left: int, right: int) -> int:
    return LONG_MAX if left > LONG_MAX - right else left + right


def reclaim_upper_bound(files: list[tuple[int, int]]) -> int:
    """files are (hard_link_count, allocated_bytes) for unique physical objects."""
    if len(files) < 2:
        return 0

    singletons = [(links, allocated) for links, allocated in files if links == 1]
    if not singletons:
        return 0

    if any(links != 1 for links, _ in files):
        total = 0
        for _, allocated in singletons:
            total = sat_add(total, allocated)
        return total

    if len(singletons) < 2:
        return 0

    keep_index = min(range(len(singletons)), key=lambda index: singletons[index][1])
    total = 0
    for index, (_, allocated) in enumerate(singletons):
        if index != keep_index:
            total = sat_add(total, allocated)
    return total


def run_model(cases: int) -> int:
    checks = 0
    assert reclaim_upper_bound([(1, 100), (1, 300), (1, 200)]) == 500
    assert reclaim_upper_bound([(2, 128), (1, 512)]) == 512
    assert reclaim_upper_bound([(2, 128)]) == 0
    assert reclaim_upper_bound([(2, 100), (3, 200)]) == 0
    assert reclaim_upper_bound([(1, LONG_MAX), (1, LONG_MAX), (1, LONG_MAX)]) == LONG_MAX
    checks += 5

    rng = random.Random(20260811)
    for _ in range(cases):
        count = rng.randint(1, 8)
        files = []
        for _ in range(count):
            links = rng.randint(1, 4)
            allocated = LONG_MAX if rng.randrange(5000) == 0 else rng.randint(0, 10_000_000_000)
            files.append((links, allocated))

        result = reclaim_upper_bound(files)
        singleton_allocations = [allocated for links, allocated in files if links == 1]
        singleton_total = 0
        for allocated in singleton_allocations:
            singleton_total = sat_add(singleton_total, allocated)

        assert 0 <= result <= LONG_MAX
        assert result <= singleton_total
        if len(files) < 2 or not singleton_allocations:
            assert result == 0
        if len(files) >= 2 and singleton_allocations and any(links != 1 for links, _ in files):
            assert result == singleton_total
        if len(files) >= 2 and len(singleton_allocations) == len(files):
            expected = 0
            keep = min(range(len(files)), key=lambda index: files[index][1])
            for index, (_, allocated) in enumerate(files):
                if index != keep:
                    expected = sat_add(expected, allocated)
            assert result == expected
        assert all(allocated >= 0 for _, allocated in files)
        checks += 6

    return checks


def check_repository(root: Path) -> int:
    core = (root / "src/FileOp.Core/Storage/StoragePhysicalReclaim.cs").read_text(encoding="utf-8")
    contract = (root / "src/FileOp.Core/Storage/StorageOptimization.cs").read_text(encoding="utf-8")
    reader = (root / "src/FileOp.Windows/Storage/WindowsCurrentFilePhysicalEvidenceReader.cs").read_text(encoding="utf-8")
    verifier = (root / "src/FileOp.Windows/Storage/WindowsSameSizeContentVerifier.cs").read_text(encoding="utf-8")
    xaml = (root / "src/FileOp.App/StorageOptimizationView.xaml").read_text(encoding="utf-8")
    view = (root / "src/FileOp.App/StorageOptimizationView.xaml.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/StoragePhysicalReclaimTests.cs").read_text(encoding="utf-8")
    native_tests = (root / "tests/FileOp.Windows.Tests/WindowsCurrentFilePhysicalEvidenceReaderTests.cs").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    checks = 0

    for needle in (
        "StoragePhysicalFileIdentity",
        "StoragePhysicalFileEvidence",
        "StorageVerifiedPhysicalFile",
        "StorageVerifiedPhysicalMatchSet",
        "StoragePhysicalReclaimEvidenceStatus",
        "StoragePhysicalReclaimAnalyzer",
        "first.HardLinkCount < groupedEvidence.Length",
        "consumedIdentities.Add(first.Identity)",
        "file.IsSingletonLink",
        "if (physicalFiles.Any(static file => !file.IsSingletonLink))",
        "keepIndex",
        "SaturatingAdd",
    ):
        assert needle in core, needle
        checks += 1

    for needle in (
        "StoragePhysicalReclaimVerification? PhysicalReclaim = null",
        "VerifiedPhysicalReclaimableBytesUpperBound",
        "StoragePhysicalReclaimEvidenceStatus.Verified",
    ):
        assert needle in contract, needle
        checks += 1

    for needle in (
        "GetFileInformationByHandle",
        "GetFileInformationByHandleEx",
        "FileInfoByHandleClass.FileStandardInfo",
        "FileStandardInfo",
        "AllocationSize",
        "EndOfFile",
        "VolumeSerialNumber",
        "NumberOfLinks",
        "FileIndexHigh",
        "FileIndexLow",
        "standardInfo.DeletePending",
        "standardInfo.Directory",
        "standardInfo.EndOfFile != stream.Length",
        "handleInfo.NumberOfLinks != standardInfo.NumberOfLinks",
    ):
        assert needle in reader, needle
        checks += 1

    for forbidden in (
        "GetCompressedFileSizeW",
        "ToExtendedLengthPath",
        "FileAccess.Write",
        "File.Delete(",
        "DeleteFile",
        "SetFileInformationByHandle",
        "DeviceIoControl",
        "CreateHardLink",
    ):
        assert forbidden not in reader, forbidden
        checks += 1

    matching_index = verifier.index("var matchingSets =")
    physical_index = verifier.index("var physicalReclaim = CapturePhysicalReclaimEvidence", matching_index)
    return_index = verifier.index("return new StorageSameSizeContentVerification", physical_index)
    dispose_index = verifier.index("foreach (var stream in streams)", return_index)
    assert matching_index < physical_index < return_index < dispose_index
    checks += 3
    for needle in (
        "_physicalEvidenceReader.Read(stream, fullPath)",
        "StoragePhysicalReclaimAnalyzer.Analyze(",
        "SHA-256 content evidence is valid, but current physical identity/allocation evidence is unavailable",
    ):
        assert needle in verifier, needle
        checks += 1

    for needle in (
        "current physical identity, hard-link count, and allocated disk bytes",
        "current upper bound for sampled matches, not deletion authorization",
    ):
        assert needle in xaml, needle
        checks += 1

    for needle in (
        "VerifiedPhysicalReclaimableBytesUpperBound",
        "unique physical file(s)",
        "singleton-link physical file(s)",
        "not deletion authorization",
        "becomes stale if the files change",
    ):
        assert needle in view, needle
        checks += 1

    for needle in (
        "AllSingletonCopiesKeepSmallestAllocationAndReclaimOthers",
        "ExistingHardLinkedCopyCanRemainWhileSingletonCopyIsReclaimable",
        "HardLinkAliasesOfSamePhysicalFileDoNotBecomeReclaimBytes",
        "DistinctMultiLinkFilesRemainNonReclaimableFromSampledPaths",
        "MissingPhysicalEvidenceFailsClosed",
        "AliasCountCannotExceedCurrentHardLinkCount",
        "PhysicalIdentityCannotAppearInTwoContentMatchSets",
        "ExtremeSingletonAllocationsSaturateAfterChoosingKeeper",
    ):
        assert needle in tests, needle
        checks += 1

    for needle in (
        "TwoOpenHandlesReportSameCurrentIdentityAndAllocation",
        "DisposedHandleIsRejectedBeforeNativeRead",
    ):
        assert needle in native_tests, needle
        checks += 1

    assert "public const int CurrentVersion = 8;" in protocol
    assert "PhysicalReclaim" not in protocol
    assert "verify_physical_reclaim_evidence.py" in gate
    checks += 3
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    args = parser.parse_args()
    if args.cases < 0:
        raise ValueError("--cases must be non-negative")

    model_checks = run_model(args.cases)
    repo_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {repo_checks:,} source/test checks" if args.repo_root else ""
    print(
        "PASS: physical reclaim evidence verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized physical-file sets{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
