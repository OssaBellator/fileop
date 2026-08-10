#!/usr/bin/env python3
"""Verify stricter client-side Optimize thresholds without GitHub Actions."""
from __future__ import annotations

import argparse
import random
from pathlib import Path

LONG_MAX = (1 << 63) - 1


def apply_model(
    large_baseline: int,
    same_size_baseline: int,
    stale_baseline: int,
    large_threshold: int,
    same_size_threshold: int,
    stale_threshold: int,
    as_of_day: int,
    large_files: list[tuple[int, int]],
    stale_files: list[tuple[int, int]],
    same_size_groups: list[tuple[int, int]],
):
    if large_threshold < large_baseline:
        raise ValueError("large threshold below helper baseline")
    if same_size_threshold < same_size_baseline:
        raise ValueError("same-size threshold below helper baseline")
    if stale_threshold < stale_baseline:
        raise ValueError("stale age below helper baseline")

    largest = [item for item in large_files if item[0] >= large_threshold]
    stale_cutoff = as_of_day - stale_threshold
    stale = [
        item
        for item in stale_files
        if item[0] >= large_threshold and item[1] <= stale_cutoff
    ]
    groups = [item for item in same_size_groups if item[0] >= same_size_threshold]
    total = 0
    for _, potential in groups:
        value = max(0, potential)
        total = LONG_MAX if total > LONG_MAX - value else total + value
    return largest, stale, groups, total


def run_model(cases: int) -> int:
    checks = 0
    baseline = apply_model(
        100,
        50,
        180,
        100,
        50,
        180,
        1000,
        [(100, 0), (200, 0)],
        [(100, 820), (200, 819)],
        [(50, 10), (100, 20)],
    )
    assert baseline[0] == [(100, 0), (200, 0)]
    assert baseline[2] == [(50, 10), (100, 20)]
    assert baseline[3] == 30
    checks += 3

    for args in (
        (99, 50, 180),
        (100, 49, 180),
        (100, 50, 179),
    ):
        try:
            apply_model(
                100,
                50,
                180,
                args[0],
                args[1],
                args[2],
                1000,
                [],
                [],
                [],
            )
        except ValueError:
            checks += 1
        else:
            raise AssertionError("looser threshold unexpectedly accepted")

    rng = random.Random(20260811)
    for _ in range(cases):
        large_baseline = rng.randint(1, 10_000_000)
        same_baseline = rng.randint(1, 10_000_000)
        stale_baseline = rng.randint(1, 1000)
        large_threshold = large_baseline * rng.choice((1, 2, 4, 8))
        same_threshold = same_baseline * rng.choice((1, 2, 4, 8))
        stale_threshold = stale_baseline * rng.choice((1, 2, 4, 6))
        as_of = 10_000
        large_files = [
            (rng.randint(large_baseline, large_baseline * 12), rng.randint(0, as_of))
            for _ in range(rng.randint(0, 30))
        ]
        stale_files = [
            (rng.randint(large_baseline, large_baseline * 12), rng.randint(0, as_of))
            for _ in range(rng.randint(0, 30))
        ]
        groups = [
            (rng.randint(same_baseline, same_baseline * 12), rng.randint(0, 10**12))
            for _ in range(rng.randint(0, 25))
        ]

        largest, stale, filtered_groups, total = apply_model(
            large_baseline,
            same_baseline,
            stale_baseline,
            large_threshold,
            same_threshold,
            stale_threshold,
            as_of,
            large_files,
            stale_files,
            groups,
        )

        assert largest == [item for item in large_files if item[0] >= large_threshold]
        assert stale == [
            item
            for item in stale_files
            if item[0] >= large_threshold and item[1] <= as_of - stale_threshold
        ]
        assert filtered_groups == [item for item in groups if item[0] >= same_threshold]
        assert all(item[0] >= large_threshold for item in largest)
        assert all(item[0] >= same_threshold for item in filtered_groups)
        assert 0 <= total <= LONG_MAX
        checks += 6

    return checks


def check_repository(root: Path) -> int:
    core = (root / "src/FileOp.Core/Storage/StorageOptimizationThresholds.cs").read_text(encoding="utf-8")
    view = (root / "src/FileOp.App/StorageOptimizationView.Thresholds.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/StorageOptimizationThresholdFilterTests.cs").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")
    checks = 0

    for needle in (
        "StorageOptimizationDisplayThresholds",
        "StorageOptimizationFilteredView",
        "StorageOptimizationThresholdFilter",
        "thresholds.LargeFileMinimumBytes < analysis.Policy.LargeFileMinimumBytes",
        "thresholds.SameSizeMinimumBytes < analysis.Policy.SameSizeMinimumBytes",
        "thresholds.StaleAgeDays < analysis.Policy.StaleAgeDays",
        "file.MeasuredBytes >= thresholds.LargeFileMinimumBytes",
        "file.LastWriteTime.ToUniversalTime() <= staleCutoff",
        "group.LogicalBytesPerFile >= thresholds.SameSizeMinimumBytes",
        "long.MaxValue - value",
    ):
        assert needle in core, needle
        checks += 1

    for needle in (
        'Text = "View thresholds"',
        "DisplayMemberPath = \"Label\"",
        "Session-only stricter filters",
        "does not rerun the helper",
        "StorageOptimizationThresholdFilter.ValidateAgainstAnalysis",
        "StorageOptimizationThresholdFilter.Apply",
        "SameSizeGroupsList.RegisterPropertyChangedCallback",
        "item.Group.LogicalBytesPerFile >= thresholds.SameSizeMinimumBytes",
        "item.Index",
        "_sameSizeVerificationResults.TryGetValue(item.Index",
        "_sameSizeVerificationMessages.TryGetValue(item.Index",
        "SaturatingMultiply(baseline, 8)",
        "SaturatingMultiply(baseline, 6)",
    ):
        assert needle in view, needle
        checks += 1

    for forbidden in (
        "AnalyzeStorageOptimizationAsync(",
        "IndexingStorageOptimizationRequest",
        "File.Open",
        "SHA256",
        "PeriodicTimer",
        "DispatcherQueueTimer",
    ):
        assert forbidden not in view, forbidden
        checks += 1

    for needle in (
        "StricterThresholdsFilterWithoutReorderingEvidence",
        "BaselineThresholdsPreserveCurrentBoundedAnalysis",
        "LooserLargeThresholdIsRejected",
        "LooserSameSizeThresholdIsRejected",
        "YoungerStaleAgeIsRejected",
        "SameSizePotentialSavingsSaturate",
    ):
        assert needle in tests, needle
        checks += 1

    assert "public const int CurrentVersion = 8;" in protocol
    assert "StorageOptimizationDisplayThresholds" not in protocol
    checks += 2
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
        "PASS: Optimize threshold overlay verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized bounded analyses{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
