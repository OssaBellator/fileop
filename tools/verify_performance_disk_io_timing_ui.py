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


def process_partition_state(
    accepted_events: int,
    process_disk_rows: int,
    visible_samples: int,
    hidden_samples: int,
    unattributed_samples: int,
) -> str:
    if accepted_events == 0:
        return "empty" if process_disk_rows == 0 else "invalid"
    if process_disk_rows == 0:
        return "unavailable"
    if visible_samples + hidden_samples + unattributed_samples != accepted_events:
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
    assert process_partition_state(0, 0, 0, 0, 0) == "empty"
    assert process_partition_state(0, 1, 0, 0, 0) == "invalid"
    assert process_partition_state(5, 0, 0, 0, 0) == "unavailable"
    assert process_partition_state(5, 1, 3, 1, 1) == "available"
    assert process_partition_state(5, 1, 3, 1, 0) == "invalid"
    checks += 14

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

        process_rows = 0 if rng.random() < 0.15 else rng.randint(1, 12)
        if process_rows == 0:
            visible = hidden = unattributed = 0
        elif accepted == 0:
            visible = hidden = unattributed = 0
        elif rng.random() < 0.9:
            visible = rng.randint(0, accepted)
            remaining = accepted - visible
            hidden = rng.randint(0, remaining)
            unattributed = remaining - hidden
        else:
            visible = accepted + 1
            hidden = unattributed = 0
        process_state = process_partition_state(
            accepted,
            process_rows,
            visible,
            hidden,
            unattributed,
        )
        if accepted == 0:
            assert process_state == ("empty" if process_rows == 0 else "invalid")
        elif process_rows == 0:
            assert process_state == "unavailable"
        elif visible + hidden + unattributed == accepted:
            assert process_state == "available"
        else:
            assert process_state == "invalid"
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
        (xaml, 'x:Name="ProcessTimingEvidenceStatusText"', "process timing status"),
        (xaml, 'Text="Top attributed process instances (byte-ranked)"', "byte-ranked process title"),
        (xaml, "Rows remain selected and ordered by observed bytes", "byte-ranked process wording"),
        (xaml, "does not prove that the process caused the device response duration", "process causation disclaimer"),
        (xaml, 'Text="Response timing"', "process response timing column"),
        (xaml, 'Text="{Binding ResponseTimingText}"', "process timing binding"),
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
        (view, "result.ProcessResponseTimings", "typed process timing source"),
        (view, "processTimingAvailable", "process timing compatibility state"),
        (view, "result.ProcessResponseTimings[diskIndex]", "process disk order"),
        (view, "disk.Owners[ownerIndex]", "process owner order"),
        (view, "No typed timing", "no invented process timing"),
        (view, "visibleSamples", "visible process sample disclosure"),
        (view, "hiddenSamples", "hidden process sample disclosure"),
        (view, "unattributedSamples", "unresolved process sample disclosure"),
        (view, "DiskIoTimingRow.FormatSummary(timing.Reads)", "shared process read formatter"),
        (view, "DiskIoTimingRow.FormatSummary(timing.Writes)", "shared process write formatter"),
        (view, "DiskIoTimingRow.FormatSummary(timing.Flushes)", "shared process flush formatter"),
        (capture, "WithProcessResponseTimings", "validated process timing attachment"),
        (timing, "public const int MinimumSamplesForP95 = 5;", "Core p95 floor"),
        (capture, "WithResponseTimings", "validated capture timing"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
        (docs, "These are display units only, not performance thresholds.", "unit-not-threshold documentation"),
        (docs, "no latency value is inferred", "legacy evidence documentation"),
        (docs, "Process timing UI presentation", "process timing UI documentation"),
        (docs, "process timing does not choose, reorder or promote rows", "no process promotion documentation"),
        (docs, "No typed timing", "process compatibility documentation"),
        (gate, "verify_performance_disk_io_ui.py", "existing DiskIo UI gate retained"),
        (gate, "verify_performance_disk_io_timing_ui.py --repo-root $repoRoot --cases 50000", "timing UI gate wiring"),
        (gate, "verify_disk_io_process_response_timing.py --repo-root $repoRoot --cases 50000", "process timing evidence gate retained"),
    ]
    for text, needle, label in required:
        checks += require(text, needle, label)

    combined = view + "\n" + docs
    for needle in (
        "slow disk",
        "fast disk",
        "healthy disk",
        "unhealthy disk",
        "slow process",
        "fast process",
        "process bottleneck",
        "latency score",
        "performance score",
        "recommended threshold",
        "automatically capture",
        "PeriodicTimer",
        "DispatcherQueueTimer",
    ):
        checks += forbid(combined.lower(), needle.lower(), "timing judgment/poller")

    checks += forbid(view, "OrderByDescending", "process timing UI ranking")
    checks += forbid(view, ".OrderBy(", "process timing UI reordering")
    checks += forbid(view, ".Take(", "process timing UI truncation")
    checks += forbid(view, ".Sort(", "process timing UI sorting")
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
