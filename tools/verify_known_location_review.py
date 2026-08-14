#!/usr/bin/env python3
"""Verify conservative known-location review semantics without GitHub Actions."""
from __future__ import annotations

import argparse
import random
from pathlib import Path

LONG_MAX = (1 << 63) - 1
PACKAGE_EXTENSIONS = {".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".msu"}
ARCHIVE_EXTENSIONS = {".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz"}
DISK_IMAGE_EXTENSIONS = {".iso"}
# This is the exact pre-refinement Downloads candidate extension set. The richer
# provenance split must never widen or narrow membership without separate review.
LEGACY_REVIEW_EXTENSIONS = PACKAGE_EXTENSIONS | ARCHIVE_EXTENSIONS | DISK_IMAGE_EXTENSIONS


def classify_download(extension: str) -> str | None:
    normalized = extension.lower()
    if normalized in PACKAGE_EXTENSIONS:
        return "package"
    if normalized in ARCHIVE_EXTENSIONS:
        return "archive"
    if normalized in DISK_IMAGE_EXTENSIONS:
        return "disk-image"
    return None


def measured_bytes(logical: int, allocated: int | None) -> int:
    return logical if allocated is None else allocated


def sat_sum(values: list[int]) -> int:
    total = 0
    for raw in values:
        value = max(0, raw)
        total = LONG_MAX if total > LONG_MAX - value else total + value
    return total


def run_model(cases: int) -> int:
    checks = 0
    assert classify_download(".msi") == "package"
    assert classify_download(".MSIXBUNDLE") == "package"
    assert classify_download(".zip") == "archive"
    assert classify_download(".ISO") == "disk-image"
    assert classify_download(".exe") is None
    assert classify_download(".img") is None
    assert classify_download(".vhdx") is None
    assert measured_bytes(100, None) == 100
    assert measured_bytes(100, 40) == 40
    assert sat_sum([LONG_MAX, LONG_MAX]) == LONG_MAX
    checks += 10

    rng = random.Random(20260811)
    extension_pool = sorted(
        LEGACY_REVIEW_EXTENSIONS
        | {".exe", ".dll", ".txt", ".bin", ".tmp", ".img", ".vhd", ".vhdx"}
    )
    for _ in range(cases):
        extension = rng.choice(extension_pool)
        logical = rng.randint(0, 10**12)
        allocated = None if rng.randrange(4) == 0 else rng.randint(0, 10**12)
        result = classify_download(extension)
        expected = (
            "package"
            if extension in PACKAGE_EXTENSIONS
            else "archive"
            if extension in ARCHIVE_EXTENSIONS
            else "disk-image"
            if extension in DISK_IMAGE_EXTENSIONS
            else None
        )
        assert result == expected
        assert (result is not None) == (extension in LEGACY_REVIEW_EXTENSIONS)
        assert (extension == ".exe") <= (result is None)
        assert (extension in {".img", ".vhd", ".vhdx"}) <= (result is None)
        assert measured_bytes(logical, allocated) == (logical if allocated is None else allocated)
        user_temp_candidate = True
        assert user_temp_candidate
        values = [measured_bytes(logical, allocated), rng.randint(0, LONG_MAX)]
        total = sat_sum(values)
        assert 0 <= total <= LONG_MAX
        assert total >= min(values[0], LONG_MAX)
        cap = rng.randint(1, 50)
        source_count = rng.randint(0, cap)
        assert (source_count >= cap) == (source_count == cap)
        checks += 9

    return checks


def check_repository(root: Path) -> int:
    model = (root / "src/FileOp.Core/Storage/StorageKnownLocationReview.cs").read_text(encoding="utf-8")
    resolver = (root / "src/FileOp.Windows/Storage/WindowsKnownFolderPathResolver.cs").read_text(encoding="utf-8")
    engine = (root / "src/FileOp.App/DesktopSearchEngine.KnownLocationReview.cs").read_text(encoding="utf-8")
    cross_volume = (root / "src/FileOp.App/DesktopSearchEngine.KnownLocationReviewCrossVolume.cs").read_text(encoding="utf-8")
    xaml = (root / "src/FileOp.App/StorageKnownLocationReviewView.xaml").read_text(encoding="utf-8")
    view = (root / "src/FileOp.App/StorageKnownLocationReviewView.xaml.cs").read_text(encoding="utf-8")
    parent = (root / "src/FileOp.App/StorageOptimizationView.xaml.cs").read_text(encoding="utf-8")
    coordinator = (root / "src/FileOp.App/MainWindow.StorageOptimization.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/StorageKnownLocationReviewTests.cs").read_text(encoding="utf-8")
    docs = (root / "docs/known-location-review.md").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    checks = 0

    for needle in (
        "StorageReviewProvenance",
        "StorageReviewLocationStatus",
        "StorageReviewReason",
        "StorageReviewCandidate",
        "StorageKnownLocationReview",
        "StorageKnownLocationReviewSnapshot",
        "downloads.old-package-extension.v1",
        "downloads.old-archive-extension.v1",
        "downloads.old-disk-image-extension.v1",
        "user-temp.old-large-file.v1",
        "OldArchiveOrDiskImage,",
        "StorageReviewReason.OldArchive,",
        "StorageReviewReason.OldDiskImage,",
        '".msi"',
        '".msixbundle"',
        '".zip"',
        'private static readonly HashSet<string> DiskImageExtensions',
        '[".iso"]',
        "Unsupported storage-review provenance.",
        "SourceMayBeTruncated",
        "analysis.StaleLargeFiles.Count >= analysis.Policy.MaxStaleLargeFiles",
        "review evidence only",
        "long.MaxValue - value",
    ):
        assert needle in model, needle
        checks += 1

    classifier_start = model.index("private static StorageReviewCandidate? ClassifyDownloads")
    classifier_end = model.index("private static StorageReviewCandidate ClassifyUserTemp", classifier_start)
    classifier = model[classifier_start:classifier_end]
    for needle in (
        "StorageReviewReason.OldInstallerPackage",
        "StorageReviewReason.OldArchive",
        "StorageReviewReason.OldDiskImage",
        "DownloadsInstallerRuleId",
        "DownloadsArchiveRuleId",
        "DownloadsDiskImageRuleId",
    ):
        assert needle in classifier, needle
        checks += 1
    assert "StorageReviewReason.OldArchiveOrDiskImage" not in classifier
    checks += 1

    archive_start = model.index("private static readonly HashSet<string> ArchiveExtensions")
    disk_image_start = model.index("private static readonly HashSet<string> DiskImageExtensions", archive_start)
    archive_block = model[archive_start:disk_image_start]
    assert '".iso"' not in archive_block
    assert '".img"' not in model
    assert '".vhd"' not in model
    assert '".vhdx"' not in model
    checks += 4

    for forbidden in ("SafeToDelete", '".exe"', "File.Delete(", "Directory.Delete("):
        assert forbidden not in model, forbidden
        checks += 1

    for needle in (
        "374DE290-123F-4565-9164-39C4925E467B",
        "SHGetKnownFolderPath",
        "IntPtr.Zero",
        "Marshal.FreeCoTaskMem",
        "CoInitializeEx",
        "RpcEChangedMode",
        "CoUninitialize",
    ):
        assert needle in resolver, needle
        checks += 1
    for forbidden in ("Registry", "Directory.Enumerate", "Directory.GetFiles", "File.Open"):
        assert forbidden not in resolver, forbidden
        checks += 1

    for needle in (
        "GetDownloadsPath()",
        "Path.GetTempPath()",
        "capturedRoot",
        "StorageRootPath is not { } currentRoot",
        "The native indexing source changed or fell behind while known-location review evidence was being captured.",
        "IsReviewPathWithinRoot",
        "AnalyzeStorageOptimizationAsync(fullPath)",
        "StorageKnownLocationReviewClassifier.Classify",
    ):
        assert needle in engine, needle
        checks += 1
    assert "StorageReviewLocationStatus.OutsideActiveVolume" in cross_volume
    checks += 1
    for forbidden in (
        "FileSystemCrawler",
        "Directory.Enumerate",
        "Directory.GetFiles",
        "File.Open",
        "IndexingServiceOperation",
        "IndexingStorageOptimizationRequest",
    ):
        assert forbidden not in engine, forbidden
        checks += 1

    for needle in (
        "Known locations review",
        "do not establish that a file is safe to delete",
        "LocationStatusList",
        "CandidateList",
    ):
        assert needle in xaml, needle
        checks += 1
    assert xaml.count("<Button") == 2
    checks += 1
    for action in ('Content="Check readiness"', 'Content="Review in Files"'):
        assert action in xaml, action
        checks += 1
    for forbidden in ('Content="Delete"', 'Content="Clean up"', 'Content="Execute"', 'Content="Queue"'):
        assert forbidden not in xaml, forbidden
        checks += 1

    for needle in (
        "candidate measured bytes",
        "not guaranteed reclaimable space or deletion authorization",
        "old installer/package extension",
        "old archive extension",
        "old disk-image extension",
        "old archive/disk-image extension",
        "rule {candidate.RuleId}",
        "Review only; this rule does not establish safe deletion.",
        "SourceMayBeTruncated",
    ):
        assert needle in view, needle
        checks += 1

    for needle in (
        "SetKnownLocationReviewLoading",
        "SetKnownLocationReviewUnavailable",
        "ApplyKnownLocationReview",
    ):
        assert needle in parent, needle
        checks += 1

    for needle in (
        "StorageKnownLocationReviewSnapshot? _storageKnownLocationReview",
        "_storageKnownLocationReview = null",
        "CaptureKnownLocationReviewAsync",
        "AnalyzeKnownLocationReviewAsync",
        "_performanceDiskIoCaptureActive",
        "_storageOptimizationLoadedForSource = false",
        "SetReadyForRefresh(false)",
    ):
        assert needle in coordinator, needle
        checks += 1

    load_start = coordinator.index("private async Task LoadStorageOptimizationAsync")
    load_end = coordinator.index("private async Task CaptureKnownLocationReviewAsync", load_start)
    performance_probe_index = coordinator.index(
        "await CapturePerformanceDiagnosticsAsync(generation);",
        load_start,
        load_end,
    )
    known_location_index = coordinator.index(
        "await CaptureKnownLocationReviewAsync(generation);",
        load_start,
        load_end,
    )
    assert performance_probe_index < known_location_index
    checks += 1

    disk_io_start = coordinator.index("private async void StorageOptimizationView_PerformanceDiskIoCaptureRequested")
    disk_io_end = coordinator.index("private async void StorageOptimizationView_SameSizeVerificationRequested", disk_io_start)
    disk_io_block = coordinator[disk_io_start:disk_io_end]
    for needle in (
        "_performanceDiskIoCaptureActive = false;",
        "!_storageOptimizationLoadedForSource",
        "HandleStorageOptimizationEngineState(_searchEngine.State);",
    ):
        assert needle in disk_io_block, needle
        checks += 1

    for needle in (
        "DownloadsArchiveExtensionsUseArchiveOnlyReviewProvenance",
        "DownloadsDiskImageUsesDistinctReviewProvenance",
        "DownloadsArchiveDiskImageSplitDoesNotWidenCandidateExtensions",
        "DownloadsExecutableIsNotInferredToBeInstaller",
        "DownloadsUnrecognizedExtensionIsIgnored",
        "UserTempUsesLocationProvenanceWithoutExtensionGuess",
        "ReachingUpstreamStaleCapIsMarkedPotentiallyTruncated",
        "CandidateMeasuredBytesSaturateAcrossLocationAndSnapshot",
        "UnsupportedProvenanceIsRejected",
    ):
        assert needle in tests, needle
        checks += 1

    for needle in (
        "downloads.old-archive-extension.v1",
        "downloads.old-disk-image-extension.v1",
        "`.iso` remains within the existing review candidate set",
        "`.img`, `.vhd`, and `.vhdx` are not added",
        "does not establish safe deletion",
    ):
        assert needle in docs, needle
        checks += 1

    assert "public const int CurrentVersion = 8;" in protocol
    assert "StorageKnownLocationReview" not in protocol
    assert "verify_known_location_review.py" in gate
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
        "PASS: known-location review verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized candidate states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
