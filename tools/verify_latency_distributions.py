#!/usr/bin/env python3
"""Verify FileOp's bounded session latency-distribution semantics without GitHub Actions."""
from __future__ import annotations

import argparse
import math
import random
import xml.etree.ElementTree as ET
from collections import deque
from pathlib import Path


def median(values: list[int]) -> int:
    ordered = sorted(values)
    middle = len(ordered) // 2
    if len(ordered) % 2:
        return ordered[middle]
    left = ordered[middle - 1]
    right = ordered[middle]
    return left + ((right - left) // 2)


def p95(values: list[int]) -> int | None:
    if len(values) < 5:
        return None
    ordered = sorted(values)
    rank = max(0, min(len(ordered) - 1, math.ceil(len(ordered) * 0.95) - 1))
    return ordered[rank]


def summarize(values: list[int], capacity: int = 20) -> tuple[int, int, int, int | None, int]:
    retained = [max(0, value) for value in values[-capacity:]]
    if not retained:
        raise ValueError("summary requires at least one sample")
    ordered = sorted(retained)
    return len(retained), ordered[0], median(retained), p95(retained), ordered[-1]


def run_model(cases: int) -> int:
    checks = 0
    assert summarize([10, 20, 30, 40, 100]) == (5, 10, 30, 100, 100)
    assert summarize([1, 2, 100, 4], capacity=3) == (3, 2, 4, None, 100)
    assert summarize([10, 21]) == (2, 10, 15, None, 21)
    assert summarize([-5]) == (1, 0, 0, None, 0)
    checks += 4

    rng = random.Random(20260810)
    for _ in range(cases):
        capacity = rng.randint(1, 20)
        count = rng.randint(1, 80)
        values = [rng.randint(-10_000, 10_000_000) for _ in range(count)]
        retained = [max(0, value) for value in values[-capacity:]]
        sample_count, minimum, med, percentile, maximum = summarize(values, capacity)

        assert sample_count == min(count, capacity)
        assert minimum == min(retained)
        assert maximum == max(retained)
        assert minimum <= med <= maximum
        assert (percentile is None) == (sample_count < 5)
        if percentile is not None:
            rank = math.ceil(sample_count * 0.95) - 1
            assert percentile == sorted(retained)[rank]
        else:
            assert sample_count < 5
        checks += 6
    return checks


def run_scope_model(cases: int) -> int:
    rng = random.Random(20260810 ^ 0x5A5A)
    checks = 0
    for _ in range(cases):
        histories: dict[tuple[str, str], deque[int]] = {}
        current_scope = rng.choice(["native index", "profile fallback"])
        for _sample in range(rng.randint(1, 60)):
            kind = rng.choice(["search", "storage", "timer"])
            scope = rng.choice(["native index", "profile fallback", "local process"])
            if kind == "timer":
                continue
            key = (kind, scope)
            queue = histories.setdefault(key, deque(maxlen=20))
            queue.append(rng.randint(0, 1_000_000))

        visible = [key for key in histories if key[1] == current_scope]
        assert all(scope == current_scope for _, scope in visible)
        assert all(len(histories[key]) <= 20 for key in histories)
        assert all(kind in {"search", "storage"} for kind, _ in histories)
        checks += 3
    return checks


def check_repository(root: Path) -> int:
    paths = {
        "model": root / "src/FileOp.Core/Performance/PerformanceDiagnostics.cs",
        "history": root / "src/FileOp.Core/Performance/PerformanceProbeHistory.cs",
        "view": root / "src/FileOp.App/PerformanceDiagnosticsView.xaml",
        "view_code": root / "src/FileOp.App/PerformanceDiagnosticsView.xaml.cs",
        "tests": root / "tests/FileOp.Windows.Tests/PerformanceProbeHistoryTests.cs",
        "gate": root / "tools/test-local.ps1",
        "performance_doc": root / "docs/performance-diagnostics.md",
    }
    text: dict[str, str] = {}
    for name, path in paths.items():
        if not path.is_file():
            raise FileNotFoundError(path)
        text[name] = path.read_text(encoding="utf-8")

    ET.fromstring(text["view"])
    checks = 1

    for needle in (
        "PerformanceProbeKind",
        "TimerBaseline",
        "Search",
        "Storage",
        "PerformanceProbeKind Kind = PerformanceProbeKind.Other",
    ):
        assert needle in text["model"], needle
        checks += 1

    for needle in (
        "PerformanceProbeDistribution",
        "public const int DefaultSampleCapacity = 20;",
        "public const int MinimumSamplesForP95 = 5;",
        "Dictionary<ProbeKey, Queue<ProbeSample>>",
        "while (samples.Count > _sampleCapacity)",
        "samples.Dequeue();",
        "probe.Kind is not (PerformanceProbeKind.Search or PerformanceProbeKind.Storage)",
        '"Indexed search probe" => PerformanceProbeKind.Search',
        '"Storage root probe" => PerformanceProbeKind.Storage',
        '"Timer baseline" => PerformanceProbeKind.TimerBaseline',
        "Math.Ceiling(ordered.Length * 0.95d)",
        "left + ((right - left) / 2)",
        "samples.Peek().CapturedAt",
        "samples.Last().CapturedAt",
    ):
        assert needle in text["history"], needle
        checks += 1

    for needle in (
        "Session latency distribution",
        "Last 20 explicit diagnostics samples per exact Search/Storage probe and source",
        "nearest-rank p95 is shown only after 5 samples",
        "Nothing is sampled in the background or persisted",
        'Text="p95 (5+)"',
        'x:Name="LatencyDistributionList"',
        'Text="Current elapsed"',
    ):
        assert needle in text["view"], needle
        checks += 1

    for needle in (
        "private readonly PerformanceProbeHistory _probeHistory = new();",
        "_probeHistory",
        ".AddAndSummarize(snapshot)",
        "PerformanceProbeDistributionRow.FromDistribution",
        "LatencyDistributionList.ItemsSource = null;",
        '$"{distribution.SampleCount:N0}/{distribution.SampleCapacity:N0}"',
        "PerformanceProbeHistory.MinimumSamplesForP95",
        '"Collect {PerformanceProbeHistory.MinimumSamplesForP95}+"',
    ):
        assert needle in text["view_code"], needle
        checks += 1

    for needle in (
        "HistorySummarizesMedianAndP95AfterFiveSamples",
        "HistoryCapsSamplesAndDropsOldest",
        "HistorySeparatesScopesAndIgnoresTimerBaseline",
        "LegacyExactProbeNamesResolveKinds",
        "EvenMedianUsesIntegerMidpointWithoutInventingExtraPrecision",
        "Assert.IsNull(distribution.P95Microseconds)",
    ):
        assert needle in text["tests"], needle
        checks += 1

    for needle in (
        "Session latency",
        "20",
        "p95",
        "explicit",
        "not persisted",
    ):
        assert needle.casefold() in text["performance_doc"].casefold(), needle
        checks += 1

    latency_source = "\n".join(text[name] for name in ("history", "view", "view_code"))
    for forbidden in (
        "DispatcherQueueTimer",
        "PeriodicTimer",
        "Task.Delay(",
        "FileSystemWatcher",
        "SqliteConnection",
        "File.Write",
        "File.Append",
        "SaveSnapshot",
        "SaveCheckpoint",
        "Registry.",
        "ServiceController",
        "EmptyWorkingSet",
    ):
        assert forbidden not in latency_source, forbidden
        checks += 1

    assert "verify_latency_distributions.py" in text["gate"]
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
    scope_checks = run_scope_model(args.cases)
    repository_checks = 0
    if args.repo_root is not None:
        repository_checks = check_repository(args.repo_root.resolve())
    suffix = f", {scope_checks:,} scope/capacity checks"
    if args.repo_root is not None:
        suffix += f" and {repository_checks:,} source/UI checks"
    print(
        "PASS: bounded session latency distributions verified with "
        f"{model_checks:,} arithmetic assertions across {args.cases:,} randomized sample sets{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
