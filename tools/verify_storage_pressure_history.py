#!/usr/bin/env python3
"""Verify FileOp's descriptive free-space/native-history correlation without GitHub Actions."""
from __future__ import annotations

import argparse
import random
import xml.etree.ElementTree as ET
from pathlib import Path


def analyze(
    older_logical: int,
    older_allocated: int | None,
    newer_logical: int,
    newer_allocated: int | None,
    total: int | None,
    free: int | None,
) -> tuple[int, bool, int | None, float | None, float | None]:
    known_total = total if total is not None and total > 0 else None
    known_free = None if free is None else max(0, free)
    if known_total is not None and known_free is not None:
        known_free = min(known_free, known_total)

    physical = older_allocated is not None and newer_allocated is not None
    delta = (
        newer_allocated - older_allocated
        if physical
        else newer_logical - older_logical
    )
    percent = (
        None
        if known_total is None or known_free is None
        else min(100.0, max(0.0, known_free * 100.0 / known_total))
    )
    multiple = (
        known_free / delta
        if physical and delta > 0 and known_free is not None
        else None
    )
    return delta, physical, known_free, percent, multiple


def run_model(cases: int) -> int:
    checks = 0
    assert analyze(1200, 1000, 1500, 1200, 10000, 2000) == (200, True, 2000, 20.0, 10.0)
    assert analyze(1000, None, 1300, None, 10000, 2000) == (300, False, 2000, 20.0, None)
    assert analyze(1000, 800, 1500, None, 10000, 3000) == (500, False, 3000, 30.0, None)
    assert analyze(1200, 1000, 1000, 800, 10000, 2000) == (-200, True, 2000, 20.0, None)
    assert analyze(1000, 800, 1300, 1000, 1000, 1500) == (200, True, 1000, 100.0, 5.0)
    checks += 5

    rng = random.Random(20260810)
    for _ in range(cases):
        older_logical = rng.randint(0, 2**50)
        logical_delta = rng.randint(-(2**30), 2**30)
        newer_logical = max(0, older_logical + logical_delta)
        physical_complete = rng.choice([True, False])
        older_allocated = rng.randint(0, 2**50) if physical_complete else rng.choice([None, rng.randint(0, 2**50)])
        if physical_complete:
            allocated_delta = rng.randint(-(2**30), 2**30)
            newer_allocated = max(0, older_allocated + allocated_delta)
        else:
            newer_allocated = None
        total = rng.randint(1, 2**50)
        free = rng.randint(0, total + 2**20)

        delta, physical, normalized_free, percent, multiple = analyze(
            older_logical,
            older_allocated,
            newer_logical,
            newer_allocated,
            total,
            free,
        )

        assert 0 <= normalized_free <= total
        assert percent is not None and 0.0 <= percent <= 100.0
        assert physical == (older_allocated is not None and newer_allocated is not None)
        if physical:
            assert delta == newer_allocated - older_allocated
        else:
            assert delta == newer_logical - older_logical
        assert (multiple is not None) == (physical and delta > 0)
        if multiple is not None:
            assert multiple == normalized_free / delta
        checks += 6
    return checks


def check_repository(root: Path) -> int:
    paths = {
        "model": root / "src/FileOp.Core/Storage/StorageHistoryPressureEvidence.cs",
        "capacity": root / "src/FileOp.App/DesktopSearchEngine.VolumeCapacity.cs",
        "performance": root / "src/FileOp.App/DesktopSearchEngine.PerformanceDiagnostics.cs",
        "view": root / "src/FileOp.App/StorageHistoryView.xaml",
        "view_code": root / "src/FileOp.App/StorageHistoryView.xaml.cs",
        "tests": root / "tests/FileOp.Windows.Tests/StorageHistoryPressureEvidenceTests.cs",
        "doc": root / "docs/storage-pressure-history.md",
        "gate": root / "tools/test-local.ps1",
    }
    text: dict[str, str] = {}
    for name, path in paths.items():
        if not path.is_file():
            raise FileNotFoundError(path)
        text[name] = path.read_text(encoding="utf-8")

    ET.fromstring(text["view"])
    checks = 1

    for needle in (
        "StorageHistoryPressureEvidence",
        "StorageHistoryDelta.Between",
        "DeltaUsesPhysicalAllocation",
        "FreeSpaceToLastPositivePhysicalGrowthMultiple",
        "growth <= 0",
        "Math.Min(knownFree, knownTotal)",
    ):
        assert needle in text["model"], needle
        checks += 1

    for needle in (
        "ReadVolumeCapacity",
        "DriveInfo(volumeRoot)",
        "AvailableFreeSpace",
    ):
        assert needle in text["capacity"], needle
        checks += 1

    assert "ReadVolumeCapacity(root)" in text["performance"]
    assert "DriveInfo(" not in text["performance"]
    checks += 2

    for needle in (
        "Current free space vs latest history",
        "Physical free space is never compared with logical-size growth",
        "does not project a disk-full date",
        "Free space / last growth",
        'x:Name="PressureFreeText"',
        'x:Name="PressureMultipleText"',
    ):
        assert needle in text["view"], needle
        checks += 1

    for needle in (
        "StorageHistoryPressureEvidence.Analyze",
        "DesktopSearchEngine.ReadVolumeCapacity(capacityRootSnapshot.RootPath)",
        'PressureMultipleText.Text = "Not comparable"',
        'PressureMultipleText.Text = "No positive growth"',
        "Logical-size growth is not compared with physical free-space bytes",
        "not a forecast, trend guarantee, cause attribution, or disk-full date",
    ):
        assert needle in text["view_code"], needle
        checks += 1

    for needle in (
        "PhysicalGrowthCanBeComparedWithCurrentFreeSpace",
        "LogicalFallbackNeverClaimsPhysicalHeadroomComparison",
        "MixedAllocationEvidenceFallsBackToLogicalDelta",
        "NonPositivePhysicalChangeDoesNotProduceGrowthMultiple",
        "CapacityRaceIsClampedBeforeHeadroomComparison",
        "OneObservationShowsLatestEvidenceWithoutInventingChange",
    ):
        assert needle in text["tests"], needle
        checks += 1

    for needle in (
        "descriptive ratio, not a time estimate",
        "Logical-size growth is never substituted for physical allocation",
        "does not calculate",
        "time to full",
        "disk-full date",
        "Only the newest two observations feed the pressure comparison",
    ):
        assert needle.casefold() in text["doc"].casefold(), needle
        checks += 1

    source = "\n".join(text[name] for name in ("model", "view", "view_code"))
    for forbidden in (
        "DispatcherQueueTimer",
        "PeriodicTimer",
        "Task.Delay(",
        "FileSystemWatcher",
        "daysRemaining",
        "hoursRemaining",
        "timeToFull",
        "growthPerDay",
        "growthPerHour",
        "Registry.",
        "ServiceController",
    ):
        assert forbidden not in source, forbidden
        checks += 1

    assert "verify_storage_pressure_history.py" in text["gate"]
    checks += 1
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    args = parser.parse_args()
    if args.cases < 0:
        raise ValueError("--cases must be non-negative")

    model_checks = run_model(args.cases)
    repository_checks = 0
    if args.repo_root is not None:
        repository_checks = check_repository(args.repo_root.resolve())
    suffix = f" and {repository_checks:,} source/UI checks" if args.repo_root else ""
    print(
        "PASS: descriptive Storage pressure/history evidence verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized cases{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
