#!/usr/bin/env python3
"""Verify FileOp's measurable performance-diagnostics boundary without GitHub Actions."""
from __future__ import annotations

import argparse
import random
import xml.etree.ElementTree as ET
from pathlib import Path


def volume_metrics(total: int | None, free: int | None) -> tuple[int | None, float | None]:
    if total is None or free is None:
        return None, None
    used = max(0, total - free)
    percent = None if total <= 0 else min(100.0, max(0.0, free * 100.0 / total))
    return used, percent


def run_model(cases: int) -> int:
    rng = random.Random(20260810)
    checks = 0

    assert volume_metrics(1_000, 250) == (750, 25.0)
    assert volume_metrics(1_000, 1_500) == (0, 100.0)
    assert volume_metrics(0, 0) == (0, None)
    assert volume_metrics(None, 10) == (None, None)
    checks += 4

    for _ in range(cases):
        total = rng.randint(1, 2**50)
        free = rng.randint(0, total)
        used, percent = volume_metrics(total, free)
        assert used == total - free
        assert percent is not None and 0.0 <= percent <= 100.0
        assert abs(percent - free * 100.0 / total) < 1e-10
        checks += 3

    return checks


def check_repository(root: Path) -> int:
    paths = {
        "model": root / "src/FileOp.Core/Performance/PerformanceDiagnostics.cs",
        "engine": root / "src/FileOp.App/DesktopSearchEngine.PerformanceDiagnostics.cs",
        "view": root / "src/FileOp.App/PerformanceDiagnosticsView.xaml",
        "view_code": root / "src/FileOp.App/PerformanceDiagnosticsView.xaml.cs",
        "optimize_view": root / "src/FileOp.App/StorageOptimizationView.xaml",
        "optimize_code": root / "src/FileOp.App/StorageOptimizationView.xaml.cs",
        "main": root / "src/FileOp.App/MainWindow.StorageOptimization.cs",
        "test": root / "tests/FileOp.Windows.Tests/PerformanceDiagnosticsTests.cs",
        "doc": root / "docs/performance-diagnostics.md",
        "protocol": root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs",
        "gate": root / "tools/test-local.ps1",
    }
    text: dict[str, str] = {}
    for name, path in paths.items():
        if not path.is_file():
            raise FileNotFoundError(path)
        text[name] = path.read_text(encoding="utf-8")

    ET.fromstring(text["view"])
    ET.fromstring(text["optimize_view"])
    checks = 2

    for needle in (
        "PerformanceProbeMeasurement",
        "ElapsedMicroseconds",
        "VolumeFreePercent",
        "VolumeUsedBytes",
        "Math.Clamp",
    ):
        assert needle in text["model"], needle
        checks += 1

    for needle in (
        "CapturePerformanceDiagnosticsAsync",
        "SearchAsync(string.Empty, limit: 1)",
        "AnalyzeStorageAsync(root, maxEntries: 1)",
        "MeasureTimerOverheadMicroseconds",
        "for (var sample = 0; sample < 128; sample++)",
        "DriveInfo(volumeRoot)",
        "AvailableFreeSpace",
        "!state.IsBusy",
        "state.Mode is DesktopSearchMode.Native or DesktopSearchMode.Fallback",
    ):
        assert needle in text["engine"], needle
        checks += 1

    for needle in (
        "Measured diagnostics only",
        "does not continuously poll",
        "clean the registry",
        "free RAM",
        "disable services",
        "Exact measurement",
        "Refresh diagnostics",
    ):
        assert needle in text["view"], needle
        checks += 1

    assert '<local:PerformanceDiagnosticsView x:Name="PerformanceDiagnostics" />' in text["optimize_view"]
    assert "PerformanceDiagnostics.RefreshRequested += PerformanceDiagnostics_RefreshRequested;" in text["optimize_code"]
    assert "PerformanceDiagnostics.SetReadyForRefresh(ready);" in text["optimize_code"]
    assert "SetPerformanceUnavailable" in text["optimize_code"]
    checks += 4

    for needle in (
        "await CapturePerformanceDiagnosticsAsync(generation);",
        "private async Task CapturePerformanceDiagnosticsAsync(int generation)",
        "_searchEngine.CapturePerformanceDiagnosticsAsync()",
        "SetPerformanceUnavailable(",
        "generation != Volatile.Read(ref _storageOptimizationGeneration)",
    ):
        assert needle in text["main"], needle
        checks += 1

    for needle in (
        "SnapshotReportsCapacityWithoutHealthScoring",
        "SnapshotKeepsUnknownAndOutOfRangeCapacityConservative",
        "Assert.AreEqual(100d, raced.VolumeFreePercent);",
    ):
        assert needle in text["test"], needle
        checks += 1

    for needle in (
        "There is no continuous performance poller",
        "SearchAsync(string.Empty, limit: 1)",
        "AnalyzeStorageAsync(root, maxEntries: 1)",
        "does not turn those values into a green/yellow/red health grade",
    ):
        assert needle in text["doc"], needle
        checks += 1

    assert "public const int CurrentVersion = 7;" in text["protocol"]
    assert "GetPerformanceDiagnostics" not in text["protocol"]
    checks += 2

    performance_source = "\n".join(
        text[name]
        for name in ("model", "engine", "view", "view_code", "optimize_view", "optimize_code", "main")
    )
    forbidden = (
        "PeriodicTimer",
        "DispatcherQueueTimer",
        "FileSystemWatcher",
        "Registry.",
        "RegistryKey",
        "ServiceController",
        "SetProcessWorkingSetSize",
        "EmptyWorkingSet",
        "powercfg",
        "defrag.exe",
        "Delete(",
    )
    for needle in forbidden:
        assert needle not in performance_source, needle
        checks += 1

    assert "verify_performance_diagnostics.py" in text["gate"]
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
        "PASS: measurable performance diagnostics verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized capacity cases{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
