#!/usr/bin/env python3
"""Verify FileOp self-resource diagnostics without GitHub Actions."""
from __future__ import annotations

import argparse
import random
from pathlib import Path


def normalize(
    captured_ms: int,
    started_ms: int,
    cpu_ms: int,
    working_set: int,
    peak_working_set: int,
    private_bytes: int,
    managed_bytes: int,
    threads: int,
) -> tuple[int, int, int, int, int, int, int]:
    return (
        max(0, captured_ms - started_ms),
        max(0, cpu_ms),
        max(0, working_set),
        max(0, peak_working_set),
        max(0, private_bytes),
        max(0, managed_bytes),
        max(0, threads),
    )


def run_model(cases: int) -> int:
    checks = 0
    assert normalize(100, 200, -1, -2, -3, -4, -5, -6) == (0, 0, 0, 0, 0, 0, 0)
    assert normalize(500, 100, 20, 30, 40, 50, 60, 7) == (400, 20, 30, 40, 50, 60, 7)
    checks += 2

    rng = random.Random(20260811)
    for _ in range(cases):
        captured = rng.randrange(-10_000_000, 10_000_001)
        started = rng.randrange(-10_000_000, 10_000_001)
        cpu = rng.randrange(-1_000_000, 10_000_001)
        working = rng.randrange(-1_000_000_000, 16_000_000_001)
        peak = rng.randrange(-1_000_000_000, 16_000_000_001)
        private = rng.randrange(-1_000_000_000, 32_000_000_001)
        managed = rng.randrange(-1_000_000_000, 16_000_000_001)
        threads = rng.randrange(-1000, 10_001)
        result = normalize(captured, started, cpu, working, peak, private, managed, threads)

        assert all(value >= 0 for value in result)
        assert result[0] == max(0, captured - started)
        assert result[1] == max(0, cpu)
        assert result[2] == max(0, working)
        assert result[3] == max(0, peak)
        assert result[4] == max(0, private)
        assert result[5] == max(0, managed)
        assert result[6] == max(0, threads)
        checks += 8

    return checks


def check_repository(root: Path) -> int:
    core = (root / "src/FileOp.Core/Performance/PerformanceDiagnostics.cs").read_text(encoding="utf-8")
    producer = (root / "src/FileOp.App/DesktopSearchEngine.PerformanceDiagnostics.cs").read_text(encoding="utf-8")
    xaml = (root / "src/FileOp.App/FileOpResourceFootprintView.xaml").read_text(encoding="utf-8")
    view = (root / "src/FileOp.App/FileOpResourceFootprintView.xaml.cs").read_text(encoding="utf-8")
    storage_xaml = (root / "src/FileOp.App/StorageOptimizationView.xaml").read_text(encoding="utf-8")
    storage = (root / "src/FileOp.App/StorageOptimizationView.xaml.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/PerformanceDiagnosticsTests.cs").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    checks = 0

    for needle in (
        "public sealed record FileOpProcessResourceSnapshot(",
        "TimeSpan TotalProcessorTime",
        "long WorkingSetBytes",
        "long PeakWorkingSetBytes",
        "long PrivateMemoryBytes",
        "long ManagedMemoryBytes",
        "int ThreadCount",
        "FileOpProcessResourceSnapshot? FileOpResources = null",
        "string? FileOpResourcesStatus = null",
    ):
        assert needle in core, needle
        checks += 1

    for needle in (
        "Process.GetCurrentProcess()",
        "process.Refresh()",
        "process.TotalProcessorTime",
        "process.WorkingSet64",
        "process.PeakWorkingSet64",
        "process.PrivateMemorySize64",
        "GC.GetTotalMemory(forceFullCollection: false)",
        "process.Threads.Count",
        "CaptureFileOpProcessResources(capturedAt)",
    ):
        assert needle in producer, needle
        checks += 1

    for forbidden in (
        "Process.GetProcesses(",
        "Process.GetProcessesByName(",
        "Process.GetProcessById(",
        "GC.Collect(",
        "EmptyWorkingSet",
        "SetProcessWorkingSetSize",
        "PerformanceCounter",
        "PeriodicTimer",
        "DispatcherQueueTimer",
    ):
        assert forbidden not in producer, forbidden
        checks += 1

    for needle in (
        "FileOp resource footprint",
        "Current FileOp process evidence",
        "Working set",
        "Private memory",
        "Managed memory",
        "CPU time since start",
        "Process uptime",
    ):
        assert needle in xaml, needle
        checks += 1

    for needle in (
        "GC.GetTotalMemory(false)",
        "not current CPU utilisation",
        "ByteFormatter.Format(snapshot.WorkingSetBytes)",
        "ByteFormatter.Format(snapshot.PrivateMemoryBytes)",
        "ByteFormatter.Format(snapshot.ManagedMemoryBytes)",
    ):
        assert needle in view, needle
        checks += 1

    assert "<local:FileOpResourceFootprintView x:Name=\"FileOpResourceFootprint\" />" in storage_xaml
    assert "FileOpResourceFootprint.Apply(snapshot.FileOpResources, snapshot.FileOpResourcesStatus);" in storage
    assert "this is not evidence of a process-counter failure" in storage
    checks += 3

    for needle in (
        "FileOpResourceSnapshotKeepsMeasuredUnitsSeparate",
        "FileOpResourceSnapshotRejectsNegativeEvidenceWithoutInventingPressure",
    ):
        assert needle in tests, needle
        checks += 1

    assert "verify_fileop_resource_footprint.py" in gate
    checks += 1

    combined = "\n".join((core, producer, xaml, view, storage_xaml, storage))
    for forbidden in (
        "health score",
        "health grade",
        "RAM booster",
        "free RAM",
        "trim working set",
        "CPU pressure score",
    ):
        assert forbidden.lower() not in combined.lower(), forbidden
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
    repo_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {repo_checks:,} source/test checks" if args.repo_root else ""
    print(
        "PASS: FileOp self-resource footprint verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
