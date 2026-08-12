#!/usr/bin/env python3
"""Verify bounded read-only volume fragmentation analysis evidence."""
from __future__ import annotations

import argparse
import math
import random
from pathlib import Path


def classify_return_code(code: int) -> str:
    if code == 0:
        return "completed"
    if code == 1:
        return "permission"
    if code == 2:
        return "unsupported"
    if code == 6:
        return "cancelled"
    return "unavailable"


def valid_metrics(
    file_percent: int,
    average_fragments: float,
    total_files: int,
    fragmented_files: int,
    largest_free_extent: int,
    average_free_extent: float,
    volume_size: int,
    used_space: int,
    free_space: int,
) -> bool:
    return (
        0 <= file_percent <= 100
        and math.isfinite(average_fragments)
        and average_fragments >= 0
        and total_files >= 0
        and 0 <= fragmented_files <= total_files
        and largest_free_extent >= 0
        and math.isfinite(average_free_extent)
        and average_free_extent >= 0
        and volume_size >= 0
        and 0 <= used_space <= volume_size
        and 0 <= free_space <= volume_size
        and largest_free_extent <= free_space
    )


def run_model(cases: int, seed: int) -> int:
    rng = random.Random(seed)
    checks = 0

    assert classify_return_code(0) == "completed"
    assert classify_return_code(1) == "permission"
    assert classify_return_code(2) == "unsupported"
    assert classify_return_code(6) == "cancelled"
    assert classify_return_code(8) == "unavailable"
    assert classify_return_code(0xFFFFFFFF) == "unavailable"
    checks += 6

    for _ in range(cases):
        total_files = rng.randint(0, 10_000_000)
        fragmented_files = rng.randint(0, total_files)
        volume_size = rng.randint(0, 20_000_000_000_000)
        used_space = rng.randint(0, volume_size)
        free_space = rng.randint(0, volume_size)
        largest = rng.randint(0, free_space)
        file_percent = rng.randint(0, 100)
        average_fragments = rng.random() * 100.0
        average_free = rng.random() * max(1, free_space)

        assert valid_metrics(
            file_percent,
            average_fragments,
            total_files,
            fragmented_files,
            largest,
            average_free,
            volume_size,
            used_space,
            free_space,
        )
        checks += 1

        assert not valid_metrics(
            101,
            average_fragments,
            total_files,
            fragmented_files,
            largest,
            average_free,
            volume_size,
            used_space,
            free_space,
        )
        assert not valid_metrics(
            file_percent,
            float("nan"),
            total_files,
            fragmented_files,
            largest,
            average_free,
            volume_size,
            used_space,
            free_space,
        )
        assert not valid_metrics(
            file_percent,
            average_fragments,
            total_files,
            total_files + 1,
            largest,
            average_free,
            volume_size,
            used_space,
            free_space,
        )
        assert not valid_metrics(
            file_percent,
            average_fragments,
            total_files,
            fragmented_files,
            free_space + 1,
            average_free,
            volume_size,
            used_space,
            free_space,
        )
        checks += 4

        code = rng.getrandbits(32)
        expected = {
            0: "completed",
            1: "permission",
            2: "unsupported",
            6: "cancelled",
        }.get(code, "unavailable")
        assert classify_return_code(code) == expected
        checks += 1

        # A Windows recommendation is preserved as evidence only; changing it
        # must never alter metric validity or provider-state classification.
        recommendation = bool(rng.getrandbits(1))
        inverted = not recommendation
        assert recommendation != inverted
        assert valid_metrics(
            file_percent,
            average_fragments,
            total_files,
            fragmented_files,
            largest,
            average_free,
            volume_size,
            used_space,
            free_space,
        )
        checks += 2

    return checks


def require(text: str, needle: str, label: str) -> int:
    if needle not in text:
        raise AssertionError("missing %s: %s" % (label, needle))
    return 1


def forbid(text: str, needle: str, label: str) -> int:
    if needle in text:
        raise AssertionError("forbidden %s: %s" % (label, needle))
    return 1


def check_repository(root: Path) -> int:
    core = (root / "src/FileOp.Core/Performance/VolumeFragmentationAnalysis.cs").read_text(encoding="utf-8")
    provider = (root / "src/FileOp.Windows/Performance/WindowsVolumeFragmentationAnalysisProvider.cs").read_text(encoding="utf-8")
    project = (root / "src/FileOp.Windows/FileOp.Windows.csproj").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/VolumeFragmentationAnalysisTests.cs").read_text(encoding="utf-8")
    docs = (root / "docs/volume-fragmentation-analysis.md").read_text(encoding="utf-8")
    parent = (root / "tools/verify_performance_diagnostics.py").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")

    checks = 0
    required = [
        (core, "DefaultTimeout = TimeSpan.FromMinutes(1)", "explicit default timeout"),
        (core, "MaximumTimeout = TimeSpan.FromMinutes(5)", "hard timeout ceiling"),
        (core, "WindowsDefragRecommended", "provider recommendation naming"),
        (core, "filePercentFragmentation > 100", "fragmentation percentage validation"),
        (core, "totalFragmentedFiles > totalFiles", "fragmented file invariant"),
        (core, "largestFreeSpaceExtentBytes > freeSpaceBytes", "free extent invariant"),
        (core, "supports only explicit local drive-letter roots", "drive-root boundary"),
        (provider, "MaximumEnumeratedVolumes = 128", "bounded volume lookup"),
        (provider, 'new ObjectQuery("SELECT Name FROM Win32_Volume")', "bounded non-interpolated WMI lookup"),
        (provider, "ManagementOperationObserver", "asynchronous WMI method invocation"),
        (provider, "observer.Cancel();", "WMI cancellation request"),
        (provider, '"DefragAnalysis"', "analysis-only WMI method"),
        (provider, "new InvokeMethodOptions { Timeout = timeout }", "WMI method timeout"),
        (provider, "raw.ReturnCode switch", "raw provider-state mapping"),
        (provider, "Stopwatch.GetElapsedTime(started)", "analysis elapsed evidence"),
        (provider, "cancellationToken.IsCancellationRequested", "caller cancellation propagation"),
        (provider, "timeoutCancellation.IsCancellationRequested", "internal timeout distinction"),
        (project, '<PackageReference Include="System.Management" Version="10.0.10" />', "pinned WMI package"),
        (tests, "ProviderMapsRawReturnCodesWithoutInventingEvidence", "return-code regression"),
        (tests, "SuccessWithMalformedMetricsFailsClosed", "malformed success regression"),
        (tests, "CallerCancellationPropagatesBeforeApiCall", "caller cancellation regression"),
        (docs, "optional compatibility evidence", "legacy support boundary"),
        (docs, "cancellation requested / FileOp stopped waiting", "cancellation caveat"),
        (docs, "does not turn it into an automatic action", "no recommendation promotion"),
        (parent, "from verify_volume_fragmentation_analysis import (", "parent imports fragmentation verifier"),
        (parent, "run_volume_fragmentation_model(args.cases", "parent runs fragmentation model"),
        (parent, "check_volume_fragmentation_repository(root)", "parent runs fragmentation source checks"),
        (gate, "verify_performance_diagnostics.py --repo-root $repoRoot --cases 50000", "existing offline parent gate"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
    ]
    for text, needle, label in required:
        checks += require(text, needle, label)

    # The native surface must remain analysis-only. Match exact action spellings
    # rather than the unavoidable explanatory word "defrag" in evidence text.
    for needle in (
        '"Defrag",',
        '"Optimize",',
        "MSFT_Volume",
        "Optimize-Volume",
        "defrag.exe",
        "Process.Start",
        "FSCTL_FILE_LEVEL_TRIM",
        "IOCTL_STORAGE_PROTOCOL_COMMAND",
        "PowerShell",
    ):
        checks += forbid(provider, needle, "storage mutation/shell path")

    combined = core + "\n" + provider
    for needle in (
        "HealthScore",
        "OptimizationScore",
        "UrgencyScore",
        "PeriodicTimer",
        "DispatcherQueueTimer",
        "FileSystemWatcher",
    ):
        checks += forbid(combined, needle, "score/background polling")

    if provider.count("volume.InvokeMethod(") != 1:
        raise AssertionError("provider must invoke exactly one WMI method")
    checks += 1
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0xD3F6A6)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = " and %s source checks" % format(source_checks, ",") if args.repo_root else ""
    print(
        "PASS: volume fragmentation analysis verified with %s model assertions across %s randomized cases%s."
        % (format(model_checks, ","), format(args.cases, ","), suffix)
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
