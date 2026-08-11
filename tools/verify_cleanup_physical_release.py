#!/usr/bin/env python3
"""Verify per-path physical release evidence for cleanup readiness without Actions."""
from __future__ import annotations

import argparse
import random
from pathlib import Path

STATUSES = ("consistent", "changed", "blocked", "unavailable")


def physical_release(base_status: str, allocated: int, hard_links: int) -> tuple[str, int, bool]:
    status = (
        "unavailable"
        if base_status == "consistent" and (allocated < 0 or hard_links == 0)
        else base_status
    )
    upper_bound = (
        allocated
        if status == "consistent" and hard_links == 1 and allocated >= 0
        else 0
    )
    return status, upper_bound, False


def run_model(cases: int, seed: int) -> int:
    checks = 0
    assert physical_release("consistent", 1536, 1) == ("consistent", 1536, False)
    assert physical_release("consistent", 1536, 2) == ("consistent", 0, False)
    assert physical_release("consistent", -1, 1) == ("unavailable", 0, False)
    assert physical_release("consistent", 100, 0) == ("unavailable", 0, False)
    assert physical_release("changed", 100, 1) == ("changed", 0, False)
    checks += 5

    rng = random.Random(seed)
    for _ in range(cases):
        base_status = rng.choice(STATUSES)
        allocated = (
            -1
            if rng.random() < 0.02
            else ((1 << 63) - 1 if rng.random() < 0.001 else rng.randint(0, 10**12))
        )
        hard_links = rng.randint(0, 8)
        status, upper_bound, authorized = physical_release(
            base_status,
            allocated,
            hard_links,
        )

        expected_status = (
            "unavailable"
            if base_status == "consistent" and (allocated < 0 or hard_links == 0)
            else base_status
        )
        assert status == expected_status
        assert upper_bound == (
            allocated
            if status == "consistent" and hard_links == 1 and allocated >= 0
            else 0
        )
        assert 0 <= upper_bound <= max(0, allocated)
        if hard_links != 1 or status != "consistent":
            assert upper_bound == 0
        assert not authorized
        checks += 5
    return checks


def require(text: str, needle: str) -> int:
    if needle not in text:
        raise AssertionError(f"missing required source guard: {needle}")
    return 1


def forbid(text: str, needle: str) -> int:
    if needle in text:
        raise AssertionError(f"forbidden source token: {needle}")
    return 1


def check_repository(root: Path) -> int:
    checks = 0
    core = (root / "src/FileOp.Core/Storage/StorageCleanupReadiness.cs").read_text(encoding="utf-8")
    windows = (root / "src/FileOp.Windows/Storage/WindowsStorageCleanupReadinessService.cs").read_text(encoding="utf-8")
    view = (root / "src/FileOp.App/StorageKnownLocationReviewView.CleanupReadiness.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/StorageCleanupPhysicalReleaseTests.cs").read_text(encoding="utf-8")
    native_tests = (root / "tests/FileOp.Windows.Tests/WindowsCurrentReviewFileEvidenceReaderTests.cs").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")
    plan = (root / "src/FileOp.Core/Operations/FileOperationPlan.cs").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    docs = (root / "docs/cleanup-physical-release-evidence.md").read_text(encoding="utf-8")

    for needle in (
        "long AllocatedBytes",
        "uint HardLinkCount",
        "long? CurrentAllocatedBytes",
        "uint? CurrentHardLinkCount",
        "CurrentPhysicalReleaseUpperBoundBytes",
        "CurrentHardLinkCount == 1",
        "currentFile.HardLinkCount == 0 || currentFile.AllocatedBytes < 0",
        "current file has one hard link",
        "deleting this one path alone is not evidence",
        "CleanupMutationAuthorized => false",
    ):
        checks += require(core, needle)

    for needle in (
        "FileFlagOpenReparsePoint",
        "GetFileInformationByHandleEx",
        "FileInfoByHandleClass.FileStandardInfo",
        "FileStandardInfo",
        "standardInfo.DeletePending",
        "information.NumberOfLinks != standardInfo.NumberOfLinks",
        "standardInfo.EndOfFile",
        "standardInfo.AllocationSize",
        "standardInfo.NumberOfLinks",
    ):
        checks += require(windows, needle)
    for forbidden in (
        "GetCompressedFileSizeW",
        "File.Delete(",
        "Directory.Delete(",
        "DeleteFileW",
        "MoveFileEx",
        "SetFileInformationByHandle",
        "DeviceIoControl",
    ):
        checks += forbid(windows, forbidden)

    for needle in (
        "Current allocation:",
        "hard links",
        "per-path physical-release upper bound",
        "deleting this one name: 0 B",
    ):
        checks += require(view, needle)
    checks += forbid(view, "CleanupMutationAuthorized")

    for needle in (
        "MatchingSingletonEvidenceExposesPhysicalReleaseUpperBoundWithoutAuthorization",
        "MultiLinkEvidenceReportsZeroPerPathPhysicalRelease",
        "InvalidAllocationOrHardLinkEvidenceIsUnavailable",
        "ChangedCandidateNeverExposesPhysicalReleaseUpperBound",
        "CurrentPhysicalReleaseUpperBoundBytes",
    ):
        checks += require(tests, needle)
    for needle in (
        "ReadAsyncCapturesCurrentHandleIdentitySizeAndLastWrite",
        "evidence.AllocatedBytes >= 0",
        "evidence.HardLinkCount > 0",
    ):
        checks += require(native_tests, needle)

    checks += require(plan, "public enum FileOperationKind\n{\n    Copy,\n    Move,")
    checks += forbid(plan, "Delete,")
    checks += require(protocol, "public const int CurrentVersion = 8;")
    checks += forbid(protocol, "PhysicalRelease")
    checks += require(gate, "verify_cleanup_physical_release.py --repo-root $repoRoot --cases 50000")
    for needle in (
        "current per-path physical-release upper bound",
        "multi-link",
        "does not authorize deletion",
        "does not add a delete executor",
    ):
        checks += require(docs, needle)
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=20260811)
    parser.add_argument("--self-test-only", action="store_true")
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model = run_model(args.cases, args.seed)
    source = 0
    if not args.self_test_only:
        if args.repo_root is None:
            parser.error("--repo-root is required unless --self-test-only is used")
        source = check_repository(args.repo_root.resolve())
    print(
        f"PASS: cleanup physical release verifier: {model + source:,} checks "
        f"({model:,} model, {source:,} source) across {args.cases:,} randomized cases."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
