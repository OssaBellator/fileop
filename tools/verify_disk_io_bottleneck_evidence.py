#!/usr/bin/env python3
"""Verify threshold-free storage I/O bottleneck evidence without hosted Actions."""
from __future__ import annotations

import argparse
import random
from pathlib import Path
from typing import List, Optional, Tuple

from verify_nvme_health_evidence import (
    check_repository as check_nvme_health_repository,
    run_model as run_nvme_health_model,
)
from verify_physical_disk_device_context import (
    check_repository as check_physical_disk_repository,
    run_model as run_physical_disk_model,
)

P95Cue = Tuple[int, int, int]
DiskCue = Tuple[int, int, int]
OwnerCue = Tuple[int, int, Optional[int], int, int]


def highest_p95(cues: List[P95Cue]) -> Optional[P95Cue]:
    if not cues:
        return None
    return sorted(cues, key=lambda cue: (-cue[2], cue[0], cue[1]))[0]


def largest_byte_disk(cues: List[DiskCue]) -> Optional[DiskCue]:
    eligible = [cue for cue in cues if cue[1] > 0]
    if not eligible:
        return None
    return sorted(eligible, key=lambda cue: (-cue[1], -cue[2], cue[0]))[0]


def largest_owner(cues: List[OwnerCue]) -> Optional[OwnerCue]:
    eligible = [cue for cue in cues if cue[3] > 0]
    if not eligible:
        return None
    return sorted(
        eligible,
        key=lambda cue: (
            -cue[3],
            -cue[4],
            cue[0],
            cue[1],
            -1 if cue[2] is None else cue[2],
        ),
    )[0]


def expected_p95(cues: List[P95Cue]) -> Optional[P95Cue]:
    if not cues:
        return None
    maximum = max(cue[2] for cue in cues)
    tied = [cue for cue in cues if cue[2] == maximum]
    return min(tied, key=lambda cue: (cue[0], cue[1]))


def expected_disk(cues: List[DiskCue]) -> Optional[DiskCue]:
    eligible = [cue for cue in cues if cue[1] > 0]
    if not eligible:
        return None
    maximum_bytes = max(cue[1] for cue in eligible)
    byte_tied = [cue for cue in eligible if cue[1] == maximum_bytes]
    maximum_operations = max(cue[2] for cue in byte_tied)
    operation_tied = [cue for cue in byte_tied if cue[2] == maximum_operations]
    return min(operation_tied, key=lambda cue: cue[0])


def expected_owner(cues: List[OwnerCue]) -> Optional[OwnerCue]:
    eligible = [cue for cue in cues if cue[3] > 0]
    if not eligible:
        return None
    maximum_bytes = max(cue[3] for cue in eligible)
    byte_tied = [cue for cue in eligible if cue[3] == maximum_bytes]
    maximum_operations = max(cue[4] for cue in byte_tied)
    operation_tied = [cue for cue in byte_tied if cue[4] == maximum_operations]
    return min(
        operation_tied,
        key=lambda cue: (
            cue[0],
            cue[1],
            -1 if cue[2] is None else cue[2],
        ),
    )


def run_model(cases: int, seed: int) -> int:
    rng = random.Random(seed)
    checks = 0

    assert highest_p95([]) is None
    assert highest_p95([(2, 1, 10), (0, 2, 10), (0, 0, 10)]) == (0, 0, 10)
    assert highest_p95([(0, 0, 9), (1, 0, 10)]) == (1, 0, 10)
    assert largest_byte_disk([]) is None
    assert largest_byte_disk([(0, 0, 10), (1, 0, 20)]) is None
    assert largest_byte_disk([(2, 100, 5), (1, 100, 6), (0, 100, 6)]) == (0, 100, 6)
    assert largest_owner([]) is None
    assert largest_owner([(1, 20, 100, 1_000, 3), (0, 10, 100, 2_000, 1)]) == (0, 10, 100, 2_000, 1)
    checks += 8

    for case in range(cases):
        p95_cues: List[P95Cue] = []
        for _ in range(rng.randint(0, 18)):
            p95_cues.append((rng.randint(0, 7), rng.randrange(3), rng.randint(0, 20_000_000)))

        selected_p95 = highest_p95(p95_cues)
        assert selected_p95 == expected_p95(p95_cues)
        checks += 1

        disk_cues: List[DiskCue] = []
        for disk in range(rng.randint(0, 8)):
            byte_count = rng.randint(0, 50_000_000)
            operation_count = rng.randint(1, 10_000) if byte_count > 0 else rng.randint(0, 10_000)
            disk_cues.append((disk, byte_count, operation_count))

        selected_disk = largest_byte_disk(disk_cues)
        assert selected_disk == expected_disk(disk_cues)
        checks += 1

        owner_cues: List[OwnerCue] = []
        seen = set()
        for _ in range(rng.randint(0, 24)):
            disk = rng.randint(0, 7)
            pid = rng.randint(1, 100_000)
            started = None if rng.random() < 0.15 else case * 10_000 - rng.randint(0, 5_000)
            key = (disk, pid, started)
            if key in seen:
                continue
            seen.add(key)
            byte_count = rng.randint(0, 10_000_000)
            operation_count = rng.randint(1, 5_000) if byte_count > 0 else rng.randint(0, 5_000)
            owner_cues.append((disk, pid, started, byte_count, operation_count))

        selected_owner = largest_owner(owner_cues)
        assert selected_owner == expected_owner(owner_cues)
        checks += 1

        # Timing changes cannot alter byte-volume or owner selection, while the changed
        # timing selection is still independently cross-checked.
        retimed = [
            (disk, operation, rng.randint(0, 500_000_000))
            for disk, operation, _ in p95_cues
        ]
        assert largest_byte_disk(disk_cues) == selected_disk
        assert largest_owner(owner_cues) == selected_owner
        assert highest_p95(retimed) == expected_p95(retimed)
        checks += 3

        # Byte/operation changes cannot alter the original p95 selection, while both
        # reweighted selectors are independently cross-checked.
        reweighted_disks = [
            (disk, rng.randint(0, 500_000_000), rng.randint(0, 50_000))
            for disk, _, _ in disk_cues
        ]
        reweighted_owners = [
            (disk, pid, started, rng.randint(0, 100_000_000), rng.randint(0, 50_000))
            for disk, pid, started, _, _ in owner_cues
        ]
        assert highest_p95(p95_cues) == selected_p95
        assert largest_byte_disk(reweighted_disks) == expected_disk(reweighted_disks)
        assert largest_owner(reweighted_owners) == expected_owner(reweighted_owners)
        checks += 3

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
    core = (root / "src/FileOp.Core/Performance/DiskIoInvestigationSummary.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/DiskIoInvestigationSummaryTests.cs").read_text(encoding="utf-8")
    view = (root / "src/FileOp.App/DiskIoAttributionView.xaml.cs").read_text(encoding="utf-8")
    xaml = (root / "src/FileOp.App/DiskIoAttributionView.xaml").read_text(encoding="utf-8")
    docs = (root / "docs/disk-io-bottleneck-evidence.md").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")

    checks = 0
    required = [
        (core, "public sealed record DiskIoObservedP95Cue", "p95 cue contract"),
        (core, "public sealed record DiskIoObservedByteDiskCue", "byte disk cue contract"),
        (core, "public sealed record DiskIoObservedOwnerBytesCue", "owner byte cue contract"),
        (core, "public sealed record DiskIoInvestigationSummary", "summary contract"),
        (core, "public static class DiskIoInvestigationSummaryAnalyzer", "summary analyzer"),
        (core, "result.Status != DiskIoCaptureStatus.Completed", "completed capture prerequisite"),
        (core, "timing?.P95 is not null", "reuse of existing p95 eligibility"),
        (core, ".OrderByDescending(static cue => cue.P95)", "highest observed p95 selection"),
        (core, ".ThenBy(static cue => cue.PhysicalDiskNumber)", "p95 deterministic disk tie"),
        (core, ".ThenBy(static cue => cue.Operation)", "p95 deterministic operation tie"),
        (core, ".Where(static disk => disk.TotalBytes > 0)", "positive byte disk selection"),
        (core, ".OrderByDescending(static disk => disk.TotalBytes)", "largest byte disk selection"),
        (core, ".SelectMany(static disk => disk.Owners.Select", "owner candidates sourced from attribution"),
        (core, ".Where(static candidate => candidate.Owner.TotalBytes > 0)", "positive owner byte selection"),
        (core, ".OrderByDescending(static candidate => candidate.Owner.TotalBytes)", "largest owner byte selection"),
        (core, "result.EvidenceMayBeIncomplete", "capture incompleteness preservation"),
        (tests, "KeepsHighestP95SeparateFromLargestByteDiskAndOwner", "metric separation regression"),
        (tests, "P95CueRequiresExistingFiveSampleEligibilityInsteadOfNewThreshold", "existing p95 floor regression"),
        (tests, "EqualP95UsesDiskThenOperationOnlyAsDeterministicTieBreakers", "p95 tie regression"),
        (tests, "IncompleteCaptureFlagIsPreservedWithoutChangingCueSelection", "incomplete capture regression"),
        (tests, "EmptyCompletedCaptureProducesNoComparativeCues", "empty capture regression"),
        (tests, "PublicCueContractsRejectMalformedManualEvidence", "manual cue invariant regression"),
        (view, "DiskIoInvestigationSummaryAnalyzer.Analyze(result)", "UI summary analysis"),
        (view, "InvestigationSummaryText.Text = FormatInvestigationSummary(investigation);", "UI summary rendering"),
        (view, "Highest observed eligible p95", "UI p95 wording"),
        (view, "largest eligible p95 within this capture, not a device-performance threshold", "no threshold verdict wording"),
        (view, "Largest observed byte volume", "UI byte cue wording"),
        (view, "Largest identified owner by observed bytes", "UI owner cue wording"),
        (view, "Issuing ownership is an association, not proof", "no owner causation wording"),
        (xaml, 'Text="Storage I/O bottleneck evidence"', "bottleneck evidence heading"),
        (xaml, "does not apply a universal slow-disk threshold or declare a bottleneck", "no automatic bottleneck verdict"),
        (xaml, 'x:Name="InvestigationSummaryText"', "summary text target"),
        (docs, "three independent facts", "independent cue documentation"),
        (docs, "does not combine them into a score", "no score documentation"),
        (docs, "not a slow-device threshold", "no device threshold documentation"),
        (docs, "association, not proof", "no causation documentation"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
        (gate, "verify_disk_io_bottleneck_evidence.py --repo-root $repoRoot --cases 50000", "offline gate wiring"),
    ]
    for text, needle, label in required:
        checks += require(text, needle, label)

    for needle in (
        "ProcessResponseTimings",
        "TimeSpan.FromMilliseconds(",
        "TimeSpan.FromSeconds(",
        "HealthScore",
        "LatencyScore",
        "PerformanceScore",
        "Process.GetProcessById",
        "PeriodicTimer",
        "DispatcherQueueTimer",
        "Task.Delay",
    ):
        checks += forbid(core, needle, "new timing-owner selection/threshold/score/lookup/sampler")

    checks += forbid(view, "CaptureDiskIoAttributionAsync", "summary starting a capture")
    checks += forbid(view, "PeriodicTimer", "summary poller")
    checks += forbid(view, "DispatcherQueueTimer", "summary poller")
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0xD15C3)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    physical_model_checks = run_physical_disk_model(args.cases, args.seed ^ 0x50445953)
    nvme_model_checks = run_nvme_health_model(args.cases, args.seed ^ 0x4E564D45)
    if args.repo_root:
        root = args.repo_root.resolve()
        source_checks = (
            check_repository(root)
            + check_physical_disk_repository(root)
            + check_nvme_health_repository(root)
        )
    else:
        source_checks = 0
    suffix = " and %s source checks" % format(source_checks, ",") if args.repo_root else ""
    print(
        "PASS: DiskIo bottleneck evidence verified with %s model assertions, physical-disk context with %s, and standardized NVMe health with %s across %s randomized cases%s."
        % (
            format(model_checks, ","),
            format(physical_model_checks, ","),
            format(nvme_model_checks, ","),
            format(args.cases, ","),
            suffix,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
