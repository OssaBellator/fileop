#!/usr/bin/env python3
"""Verify bounded DiskIo response-timing evidence without hosted Actions."""
from __future__ import annotations

import argparse
import math
import random
import sys
from pathlib import Path
from typing import Optional, Tuple


def summarize(values: list[int]) -> Optional[Tuple[int, int, int, Optional[int], int]]:
    if not values:
        return None
    ordered = sorted(values)
    count = len(ordered)
    middle = count // 2
    median = (
        ordered[middle]
        if count % 2
        else ordered[middle - 1] + (ordered[middle] - ordered[middle - 1]) // 2
    )
    p95 = None
    if count >= 5:
        rank = (count * 95 + 99) // 100
        p95 = ordered[rank - 1]
    return count, ordered[0], median, p95, ordered[-1]


def run_model(cases: int, seed: int) -> int:
    rng = random.Random(seed)
    checks = 0

    fixed = [
        ([1, 2, 3, 4, 5], (5, 1, 3, 5, 5)),
        ([2, 8], (2, 2, 5, None, 8)),
        ([0], (1, 0, 0, None, 0)),
    ]
    for values, expected in fixed:
        assert summarize(values) == expected
        checks += 1

    for _ in range(cases):
        event_count = rng.randint(0, 60)
        events: list[tuple[int, int, int]] = []
        for _ in range(event_count):
            events.append(
                (
                    rng.randint(0, 4),
                    rng.randrange(3),
                    rng.randint(0, 10_000_000),
                )
            )

        grouped: dict[tuple[int, int], list[int]] = {}
        for disk, operation, ticks in events:
            grouped.setdefault((disk, operation), []).append(ticks)

        for values in grouped.values():
            summary = summarize(values)
            assert summary is not None
            count, minimum, median, p95, maximum = summary
            checks += 1
            assert count == len(values)
            checks += 1
            assert minimum == min(values)
            assert maximum == max(values)
            checks += 2
            assert minimum <= median <= maximum
            checks += 1
            if len(values) < 5:
                assert p95 is None
                checks += 1
            else:
                rank = math.ceil(len(values) * 0.95)
                assert p95 == sorted(values)[rank - 1]
                checks += 1
                assert median <= p95 <= maximum
                checks += 1

        assert sum(len(values) for values in grouped.values()) == event_count
        checks += 1

    return checks


def require(text: str, needle: str, label: str) -> int:
    if needle not in text:
        raise AssertionError(f"missing {label}: {needle}")
    return 1


def forbid(text: str, needle: str, label: str) -> int:
    if needle in text:
        raise AssertionError(f"forbidden {label}: {needle}")
    return 1


def run_source_guards(root: Path) -> int:
    checks = 0
    timing = (root / "src/FileOp.Core/Performance/DiskIoResponseTiming.cs").read_text(encoding="utf-8")
    capture = (root / "src/FileOp.Core/Performance/DiskIoCapture.cs").read_text(encoding="utf-8")
    attribution = (root / "src/FileOp.Core/Performance/DiskIoAttribution.cs").read_text(encoding="utf-8")
    bridge = (root / "src/FileOp.Windows/Performance/WindowsDiskIoEventRecordBridge.cs").read_text(encoding="utf-8")
    collector = (root / "src/FileOp.Windows/Performance/WindowsDiskIoCaptureCollector.cs").read_text(encoding="utf-8")
    provider = (root / "src/FileOp.Windows/Performance/WindowsDiskIoAttributionProvider.cs").read_text(encoding="utf-8")
    ui = (root / "src/FileOp.App/DiskIoAttributionView.xaml.cs").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/DiskIoResponseTimingTests.cs").read_text(encoding="utf-8")
    provider_tests = (root / "tests/FileOp.Windows.Tests/WindowsDiskIoAttributionProviderTests.cs").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    docs = (root / "docs/disk-io-response-timing.md").read_text(encoding="utf-8")

    required = [
        (timing, "public const int MinimumSamplesForP95 = 5;", "p95 sample floor"),
        (timing, ".GroupBy(static observation => observation.PhysicalDiskNumber)", "per-disk grouping"),
        (timing, "Build(group, DiskIoOperationKind.Read)", "read timing"),
        (timing, "Build(group, DiskIoOperationKind.Write)", "write timing"),
        (timing, "Build(group, DiskIoOperationKind.Flush)", "flush timing"),
        (timing, "((long)ticks.Length * 95 + 99) / 100", "nearest-rank p95"),
        (timing, "observation.ResponseTime < TimeSpan.Zero", "negative timing rejection"),
        (timing, "observation.Timestamp < startedAt || observation.Timestamp > endedAt", "window validation"),
        (bridge, "ResponseTime: metadata.ConvertHighResolutionResponseTime", "QPC-derived response conversion"),
        (collector, "decoded.ResponseTime", "decoded response preservation"),
        (collector, "_responseTimings.Add(responseTiming);", "one timing per accepted completion"),
        (collector, "public IReadOnlyList<DiskIoResponseTimingObservation> ResponseTimings { get; init; }", "snapshot compatibility body property"),
        (capture, "public IReadOnlyList<DiskIoDiskResponseTiming> ResponseTimings { get; private init; }", "read-only typed timing evidence"),
        (capture, "public DiskIoCaptureResult WithResponseTimings(", "validated timing attachment"),
        (capture, "snapshot.Length != Report.Disks.Count", "one timing row per report disk"),
        (capture, "readSamples != disk.ReadOperations", "read count alignment"),
        (capture, "writeSamples != disk.WriteOperations", "write count alignment"),
        (capture, "flushSamples != disk.FlushOperations", "flush count alignment"),
        (capture, "sampleCount != Report.AcceptedEventCount", "total count alignment"),
        (provider, "DiskIoResponseTimingAnalyzer.Analyze(", "provider timing aggregation"),
        (provider, "collection.ResponseTimings.Count != collection.Observations.Count", "raw timing count consistency"),
        (provider, "responseTimingSampleCount != collection.Observations.Count", "summary count consistency"),
        (provider, ".WithResponseTimings(responseTimings)", "validated result timing attachment"),
        (provider, "one decoded response-duration sample per completion", "provider evidence wording"),
        (tests, "SummarizesEachOperationPerPhysicalDisk", "Core timing regression"),
        (tests, "EmptyTimingSetRemainsEmptyInsteadOfInventingLatency", "no invented timing regression"),
        (provider_tests, "MismatchedResponseTimingCountFailsClosed", "provider count regression"),
        (gate, "verify_disk_io_response_timing.py --repo-root $repoRoot --cases 50000", "offline gate wiring"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
        (docs, "not a queue-time/service-time decomposition", "timing semantics"),
        (docs, "does not label a disk as a bottleneck", "no premature bottleneck claim"),
    ]
    for text, needle, label in required:
        checks += require(text, needle, label)

    checks += require(
        attribution,
        "public sealed record DiskIoEventObservation(\n    DateTimeOffset Timestamp,\n    uint PhysicalDiskNumber,\n    DiskIoOperationKind Operation,\n    long TransferBytes,\n    DiskIoProcessIdentity? Owner);",
        "unchanged attribution observation constructor/deconstruction",
    )
    checks += forbid(
        collector,
        "IReadOnlyList<DiskIoResponseTimingObservation> ResponseTimings,",
        "positional collector snapshot timing field",
    )
    checks += forbid(capture, "ResponseTimings { get; init; }", "publicly mutable timing attachment")
    checks += forbid(ui, "ResponseTimings", "premature timing UI claim")
    checks += forbid(ui.lower(), "bottleneck", "premature bottleneck UI claim")
    checks += forbid(timing.lower(), "health score", "health scoring")
    checks += forbid(timing, "Task.Delay", "timing background sampler")
    checks += forbid(timing, "Timer", "timing poller")
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0xD150)
    parser.add_argument("--self-test-only", action="store_true")
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = 0
    if not args.self_test_only:
        if args.repo_root is None:
            parser.error("--repo-root is required unless --self-test-only is used")
        source_checks = run_source_guards(args.repo_root.resolve())

    print(
        f"PASS: DiskIo response timing verifier: {model_checks + source_checks:,} checks "
        f"({model_checks:,} model, {source_checks:,} source) across {args.cases:,} randomized cases"
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except AssertionError as error:
        print(f"FAIL: {error}", file=sys.stderr)
        raise SystemExit(1)
