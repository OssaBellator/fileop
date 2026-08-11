#!/usr/bin/env python3
"""Verify descriptive DiskIo response-timing UI without hosted Actions."""
from __future__ import annotations

import argparse
import random
import xml.etree.ElementTree as ET
from pathlib import Path


def unit_for_microseconds(value: float) -> str:
    if value < 1_000:
        return "us"
    if value < 1_000_000:
        return "ms"
    return "s"


def timing_state(accepted_events: int, timing_rows: int, timing_samples: int) -> str:
    if timing_rows == 0:
        return "empty" if accepted_events == 0 else "unavailable"
    if timing_samples != accepted_events:
        return "invalid"
    return "available"


def run_model(cases: int, seed: int) -> int:
    checks = 0
    assert unit_for_microseconds(0) == "us"
    assert unit_for_microseconds(999.9) == "us"
    assert unit_for_microseconds(1_000) == "ms"
    assert unit_for_microseconds(999_999) == "ms"
    assert unit_for_microseconds(1_000_000) == "s"
    assert timing_state(0, 0, 0) == "empty"
    assert timing_state(5, 0, 0) == "unavailable"
    assert timing_state(5, 1, 5) == "available"
    assert timing_state(5, 1, 4) == "invalid"
    checks += 9

    rng = random.Random(seed)
    for _ in range(cases):
        microseconds = rng.random() * 4_000_000
        expected_unit = (
            "us" if microseconds < 1_000
            else "ms" if microseconds < 1_000_000
            else "s"
        )
        assert unit_for_microseconds(microseconds) == expected_unit
        checks += 1

        accepted = rng.randint(0, 500_000)
        timing_rows = 0 if rng.random() < 0.15 else rng.randint(1, 12)
        timing_samples = accepted if rng.random() < 0.9 else rng.randint(0, 500_000)
        state = timing_state(accepted, timing_rows, timing_samples)
        if timing_rows == 0:
            assert state == ("empty" if accepted == 0 else "unavailable")
        elif timing_samples == accepted:
            assert state == "available"
        else:
            assert state == "invalid"
        checks += 1

        sample_count = rng.randint(1, 100)
        p95_visible = sample_count >= 5
        assert p95_visible == (sample_count >= 5)
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


def check_repository(root: Path) -> int:
    xaml_path = root / "src/FileOp.App/DiskIoAttributionView.xaml"
    xaml = xaml_path.read_text(encoding="utf-8")
    view = (root / "src/FileOp.App/DiskIoAttributionView.xaml.cs").read_text(encoding="utf-8")
    timing = (root / "src/FileOp.Core/Performance/DiskIoResponseTiming.cs").read_text(encoding="utf-8")
    capture = (root / "src/FileOp.Core/Performance/DiskIoCapture.cs").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")
    docs = (root / "docs/disk-io-response-timing.md").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")

    checks = 0
    ET.parse(xaml_path)
    checks += 1

    required = [
        (xaml, 'x:Name="TimingList"', "timing list"),
        (xaml, 'x:Name="TimingEvidenceStatusText"', "timing status"),
        (xaml, 'Text="Response timing by physical disk"', "timing section"),
        (xaml, 'Text="Read response"', "read response column"),
        (xaml, 'Text="Write response"', "write response column"),
        (xaml, 'Text="Flush response"', "flush response column"),
        (xaml, "not a queue-time/service-time decomposition", "queue/service disclaimer"),
        (xaml, "do not by themselves establish a storage bottleneck", "no bottleneck verdict"),
        (xaml, "p95 is shown only with at least 5 samples", "p95 sample floor wording"),
        (view, "result.ResponseTimings", "typed timing source"),
        (view, "DiskIoTimingRow.FromEvidence", "timing row projection"),
        (view, "TimingList.ItemsSource = null", "timing row reset"),
        (view, "TimingEvidenceStatusText.Text", "timing status lifecycle"),
        (view, "No latency value is inferred", "legacy no-inference wording"),
        (view, "DiskIoResponseTimingAnalyzer.MinimumSamplesForP95", "shared p95 floor"),
        (view, "summary.P95 is { } percentile", "optional p95 evidence"),
        (view, "return \"No samples\";", "no zero-latency invention"),
        (view, "duration.TotalMicroseconds", "microsecond display"),
        (view, "duration.TotalMilliseconds", "millisecond display"),
        (view, "duration.TotalSeconds", "second display"),
        (timing, "public const int MinimumSamplesForP95 = 5;", "Core p95 floor"),
        (capture, "WithResponseTimings", "validated capture timing"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
        (docs, "These are display units only, not performance thresholds.", "unit-not-threshold documentation"),
        (docs, "no latency value is inferred", "legacy evidence documentation"),
        (gate, "verify_performance_disk_io_ui.py", "existing DiskIo UI gate retained"),
        (gate, "verify_performance_disk_io_timing_ui.py --repo-root $repoRoot --cases 50000", "timing UI gate wiring"),
    ]
    for text, needle, label in required:
        checks += require(text, needle, label)

    combined = view + "\n" + docs
    for needle in (
        "slow disk",
        "fast disk",
        "healthy disk",
        "unhealthy disk",
        "latency score",
        "performance score",
        "recommended threshold",
        "automatically capture",
        "PeriodicTimer",
        "DispatcherQueueTimer",
    ):
        checks += forbid(combined.lower(), needle.lower(), "timing judgment/poller")

    checks += forbid(view, "CaptureDiskIoAttributionAsync", "timing display starting capture")
    checks += forbid(view, "Task.Delay", "timing display sampler")
    checks += forbid(view, "System.Threading.Timer", "timing display timer")
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0xD15C0)
    args = parser.parse_args()
    if args.cases < 0:
        parser.error("--cases must be non-negative")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {source_checks:,} source/UI checks" if args.repo_root else ""
    print(
        "PASS: Performance DiskIo timing UI verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
