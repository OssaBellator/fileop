#!/usr/bin/env python3
"""Verify process-bound DiskIo response timing without hosted Actions."""
from __future__ import annotations

import argparse
import random
from pathlib import Path
from typing import Dict, List, Optional, Tuple

Owner = Optional[Tuple[int, Optional[int]]]
Event = Tuple[int, int, int, Owner, int]


def visible_owner_keys(events: List[Event], max_owners: int) -> List[Tuple[int, Optional[int]]]:
    totals: Dict[Tuple[int, Optional[int]], Tuple[int, int]] = {}
    for _, _, transfer_bytes, owner, _ in events:
        if owner is None:
            continue
        current_bytes, current_ops = totals.get(owner, (0, 0))
        totals[owner] = (current_bytes + transfer_bytes, current_ops + 1)
    return [
        owner
        for owner, _ in sorted(
            totals.items(),
            key=lambda item: (
                -item[1][0],
                -item[1][1],
                item[0][0],
                -1 if item[0][1] is None else item[0][1],
            ),
        )[:max_owners]
    ]


def operation_counts(events: List[Event]) -> Tuple[int, int, int]:
    return tuple(sum(1 for event in events if event[1] == operation) for operation in range(3))  # type: ignore[return-value]


def analyze_reference(events: List[Event], max_owners: int):
    visible = visible_owner_keys(events, max_owners)
    visible_set = set(visible)
    owner_counts = {
        owner: operation_counts([event for event in events if event[3] == owner])
        for owner in visible
    }
    unattributed = operation_counts([event for event in events if event[3] is None])
    hidden = operation_counts(
        [event for event in events if event[3] is not None and event[3] not in visible_set]
    )
    return visible, owner_counts, unattributed, hidden


def run_model(cases: int, seed: int) -> int:
    rng = random.Random(seed)
    checks = 0

    hidden_slow: List[Event] = [
        (0, 0, 4_000, (10, 100), 1),
        (0, 0, 2_000, (20, 100), 500),
        (0, 1, 100, (30, 100), 5_000),
    ]
    visible, _, _, hidden = analyze_reference(hidden_slow, 2)
    assert visible == [(10, 100), (20, 100)]
    assert hidden == (0, 1, 0)
    checks += 2

    for case in range(cases):
        event_count = rng.randint(0, 80)
        max_owners = rng.randint(1, 6)
        events: List[Event] = []
        for index in range(event_count):
            disk = 0
            operation = rng.randrange(3)
            transfer_bytes = 0 if operation == 2 else rng.randint(0, 1_000_000)
            if rng.random() < 0.2:
                owner: Owner = None
            else:
                pid = rng.randint(1, 16)
                started = None if rng.random() < 0.15 else case * 1000 - rng.randint(0, 500)
                owner = (pid, started)
            response_ticks = rng.randint(0, 50_000_000)
            events.append((disk, operation, transfer_bytes, owner, response_ticks))

        visible, owner_counts, unattributed, hidden = analyze_reference(events, max_owners)
        checks += 1
        assert len(visible) <= max_owners
        assert len(visible) == len(set(visible))
        checks += 2

        visible_samples = sum(sum(owner_counts[owner]) for owner in visible)
        total_partition = visible_samples + sum(unattributed) + sum(hidden)
        assert total_partition == len(events)
        checks += 1

        all_counts = operation_counts(events)
        reconstructed = tuple(
            sum(owner_counts[owner][operation] for owner in visible)
            + unattributed[operation]
            + hidden[operation]
            for operation in range(3)
        )
        assert reconstructed == all_counts
        checks += 1

        # Changing only response durations cannot change the byte/operation-ranked visible set.
        retimed = [
            (disk, operation, transfer_bytes, owner, rng.randint(0, 500_000_000))
            for disk, operation, transfer_bytes, owner, _ in events
        ]
        assert visible_owner_keys(retimed, max_owners) == visible
        checks += 1

        # A PID reused with a different start value remains a distinct process instance.
        pid = rng.randint(1, 16)
        reuse_events: List[Event] = [
            (0, 0, 1000, (pid, case * 1000 - 100), 1),
            (0, 0, 900, (pid, case * 1000 - 10), 2),
        ]
        reuse_visible = visible_owner_keys(reuse_events, 2)
        assert len(reuse_visible) == 2
        assert reuse_visible[0] != reuse_visible[1]
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
    process_timing = (root / "src/FileOp.Core/Performance/DiskIoProcessResponseTiming.cs").read_text(encoding="utf-8")
    timing = (root / "src/FileOp.Core/Performance/DiskIoResponseTiming.cs").read_text(encoding="utf-8")
    capture = (root / "src/FileOp.Core/Performance/DiskIoCapture.cs").read_text(encoding="utf-8")
    provider = (root / "src/FileOp.Windows/Performance/WindowsDiskIoAttributionProvider.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/DiskIoProcessResponseTimingTests.cs").read_text(encoding="utf-8")
    ui = (root / "src/FileOp.App/DiskIoAttributionView.xaml.cs").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")
    docs = (root / "docs/disk-io-response-timing.md").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")

    checks = 0
    required = [
        (timing, "internal static DiskIoResponseTimingSummary? BuildSummary(", "shared summary helper"),
        (timing, "internal static void ValidateObservation(", "shared timing validation"),
        (process_timing, "foreach (var attributionOwner in disk.Owners)", "attribution owner order"),
        (process_timing, "var visibleKeys = disk.Owners", "visible owners sourced from attribution"),
        (process_timing, "DiskIoResponseTimingAnalyzer.BuildSummary(samples, DiskIoOperationKind.Read)", "read summary"),
        (process_timing, "DiskIoResponseTimingAnalyzer.BuildSummary(samples, DiskIoOperationKind.Write)", "write summary"),
        (process_timing, "DiskIoResponseTimingAnalyzer.BuildSummary(samples, DiskIoOperationKind.Flush)", "flush summary"),
        (process_timing, "unattributedRead != disk.UnattributedReadOperations", "unattributed count validation"),
        (process_timing, "hiddenRead != disk.OtherIdentifiedReadOperations", "hidden count validation"),
        (process_timing, "row.TotalSamples != disk.TotalOperations", "disk total validation"),
        (process_timing, "ProcessKey(int ProcessId, DateTimeOffset? StartedAt)", "stable process key"),
        (capture, "public IReadOnlyList<DiskIoDiskProcessResponseTiming> ProcessResponseTimings { get; private init; }", "read-only process timing evidence"),
        (capture, "private bool ResponseTimingsAttached { get; init; }", "explicit disk timing attachment state"),
        (capture, "public DiskIoCaptureResult WithProcessResponseTimings(", "validated process timing attachment"),
        (capture, "ResponseTimingsAttached = true", "disk timing attachment marker"),
        (capture, "if (!ResponseTimingsAttached)", "empty capture disk timing prerequisite"),
        (capture, "Disk-I/O disk response timing must be attached before process response timing.", "disk timing prerequisite"),
        (capture, "timingOwner.Owner != attributionOwner.Owner", "visible owner identity validation"),
        (capture, "timing.Owners.Count != disk.Owners.Count", "visible owner row count validation"),
        (provider, "DiskIoProcessResponseTimingAnalyzer.Analyze(", "provider process timing analysis"),
        (provider, ".WithProcessResponseTimings(processResponseTimings)", "provider process timing attachment"),
        (provider, "provenance-bound disk and visible-owner response timing", "provider semantics wording"),
        (tests, "PreservesByteRankedVisibleOwnersAndDisclosesHiddenAndUnattributedSamples", "selection/bucket regression"),
        (tests, "hidden owner has the largest response duration", "no latency-promotion regression"),
        (tests, "ProcessTimingFailsClosedWhenVisibleOwnerCountsDiverge", "visible count regression"),
        (tests, "ProcessTimingFailsClosedWhenUnattributedAndHiddenBucketsDiverge", "bucket count regression"),
        (tests, "CaptureAttachmentRequiresDiskTimingAndPreservesVisibleOwnerOrder", "attachment regression"),
        (tests, "EmptyCaptureStillRequiresExplicitDiskTimingAttachment", "empty attachment-state regression"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
        (docs, "byte-ranked visible attribution owners", "byte-ranked process timing documentation"),
        (docs, "does not yet display process-specific timing", "no process timing UI"),
        (gate, "verify_disk_io_response_timing.py --repo-root $repoRoot --cases 50000", "disk timing verifier retained"),
        (gate, "verify_disk_io_timing_provenance.py --repo-root $repoRoot --cases 50000", "provenance verifier retained"),
        (gate, "verify_disk_io_process_response_timing.py --repo-root $repoRoot --cases 50000", "process timing verifier wiring"),
    ]
    for text, needle, label in required:
        checks += require(text, needle, label)

    provider_process = provider.index("DiskIoProcessResponseTimingAnalyzer.Analyze(")
    provider_disk = provider.index("DiskIoResponseTimingAnalyzer.Analyze(")
    attach_disk = provider.index(".WithResponseTimings(responseTimings)")
    attach_process = provider.index(".WithProcessResponseTimings(processResponseTimings)")
    if not provider_disk < provider_process < attach_disk < attach_process:
        raise AssertionError("provider must analyze disk timing before process timing and attach in dependency order")
    checks += 1

    checks += forbid(process_timing, "OrderByDescending", "latency-based owner ranking")
    checks += forbid(process_timing, ".Take(", "independent process timing truncation")
    checks += forbid(ui, "ProcessResponseTimings", "process timing UI exposure")
    checks += forbid(ui, "DiskIoProcessResponseTiming", "process timing UI projection")
    checks += forbid(provider, "Process.GetProcessById", "second process lookup")
    checks += forbid(provider, "PeriodicTimer", "process timing poller")
    checks += forbid(provider, "DispatcherQueueTimer", "process timing poller")
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0xD15C2)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = " and %s source checks" % format(source_checks, ",") if args.repo_root else ""
    print(
        "PASS: DiskIo process response timing verified with %s model assertions across %s randomized cases%s."
        % (format(model_checks, ","), format(args.cases, ","), suffix)
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
