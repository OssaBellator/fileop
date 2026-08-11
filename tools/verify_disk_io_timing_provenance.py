#!/usr/bin/env python3
"""Verify DiskIo timing/attribution provenance without hosted Actions."""
from __future__ import annotations

import argparse
import random
from pathlib import Path
from typing import List, Optional, Tuple

Owner = Optional[Tuple[int, Optional[int], Optional[str]]]
Event = Tuple[int, int, int, Owner]


def aligned(observations: List[Event], timings: List[Event]) -> bool:
    if len(observations) != len(timings):
        return False
    return all(left == right for left, right in zip(observations, timings))


def run_model(cases: int, seed: int) -> int:
    rng = random.Random(seed)
    checks = 0

    owner = (41, 1000, "worker.exe")
    exact = [(100, 2, 0, owner)]
    assert aligned(exact, list(exact))
    assert not aligned(exact, [])
    assert not aligned(exact, [(101, 2, 0, owner)])
    assert not aligned(exact, [(100, 3, 0, owner)])
    assert not aligned(exact, [(100, 2, 1, owner)])
    assert not aligned(exact, [(100, 2, 0, (42, 1000, "worker.exe"))])
    assert not aligned(exact, [(100, 2, 0, None)])
    assert aligned([(100, 2, 0, None)], [(100, 2, 0, None)])
    checks += 8

    for case in range(cases):
        count = rng.randint(0, 24)
        observations: List[Event] = []
        for index in range(count):
            timestamp = case * 1000 + index
            disk = rng.randint(0, 7)
            operation = rng.randrange(3)
            if rng.random() < 0.25:
                event_owner: Owner = None
            else:
                pid = rng.randint(1, 200_000)
                started = timestamp - rng.randint(0, 100_000)
                image = None if rng.random() < 0.15 else "p%d.exe" % pid
                event_owner = (pid, started, image)
            observations.append((timestamp, disk, operation, event_owner))

        timings = list(observations)
        assert aligned(observations, timings)
        checks += 1

        if observations:
            target = rng.randrange(len(observations))
            timestamp, disk, operation, event_owner = timings[target]
            mutation = rng.randrange(4)
            if mutation == 0:
                timings[target] = (timestamp + 1, disk, operation, event_owner)
            elif mutation == 1:
                timings[target] = (timestamp, disk + 1, operation, event_owner)
            elif mutation == 2:
                timings[target] = (timestamp, disk, (operation + 1) % 3, event_owner)
            else:
                if event_owner is None:
                    changed_owner: Owner = (1, timestamp - 1, "p1.exe")
                else:
                    changed_owner = (
                        event_owner[0] + 1,
                        event_owner[1],
                        event_owner[2],
                    )
                timings[target] = (timestamp, disk, operation, changed_owner)
            assert not aligned(observations, timings)
            checks += 1

        extra = list(observations)
        extra.append((case * 1000 + 999, 0, 0, None))
        assert not aligned(observations, extra)
        checks += 1

        # Stable process identity includes process start when available; numeric PID reuse
        # must not silently bind timing to a different process instance.
        pid = rng.randint(1, 200_000)
        timestamp = case * 1000 + 500
        old_owner: Owner = (pid, timestamp - 100, "worker.exe")
        new_owner: Owner = (pid, timestamp - 10, "worker.exe")
        assert not aligned(
            [(timestamp, 0, 0, old_owner)],
            [(timestamp, 0, 0, new_owner)],
        )
        checks += 1

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
    timing = (root / "src/FileOp.Core/Performance/DiskIoResponseTiming.cs").read_text(encoding="utf-8")
    attribution = (root / "src/FileOp.Core/Performance/DiskIoAttribution.cs").read_text(encoding="utf-8")
    collector = (root / "src/FileOp.Windows/Performance/WindowsDiskIoCaptureCollector.cs").read_text(encoding="utf-8")
    validator = (root / "src/FileOp.Windows/Performance/WindowsDiskIoTimingProvenanceValidator.cs").read_text(encoding="utf-8")
    provider = (root / "src/FileOp.Windows/Performance/WindowsDiskIoAttributionProvider.cs").read_text(encoding="utf-8")
    collector_tests = (root / "tests/FileOp.Windows.Tests/WindowsDiskIoCaptureCollectorTests.cs").read_text(encoding="utf-8")
    validator_tests = (root / "tests/FileOp.Windows.Tests/WindowsDiskIoTimingProvenanceValidatorTests.cs").read_text(encoding="utf-8")
    owner_tests = (root / "tests/FileOp.Windows.Tests/DiskIoResponseTimingOwnerTests.cs").read_text(encoding="utf-8")
    ui = (root / "src/FileOp.App/DiskIoAttributionView.xaml.cs").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")
    docs = (root / "docs/disk-io-response-timing.md").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")

    checks = 0
    required = [
        (timing, "public sealed record DiskIoResponseTimingObservation(\n    DateTimeOffset Timestamp,\n    uint PhysicalDiskNumber,\n    DiskIoOperationKind Operation,\n    TimeSpan ResponseTime)", "four-field timing observation ABI"),
        (timing, "public DiskIoProcessIdentity? Owner { get; init; }", "optional timing owner"),
        (timing, "owner.ProcessId <= 0", "timing owner PID validation"),
        (timing, "processStart > observation.Timestamp", "timing owner chronology validation"),
        (timing, "string.IsNullOrWhiteSpace(imageName)", "timing owner image validation"),
        (attribution, "DiskIoProcessIdentity? Owner);", "attribution owner retained"),
        (collector, "Owner = ownerResolution.Owner", "shared resolver owner on timing"),
        (collector, "_observations.Add(observation);\n            _responseTimings.Add(responseTiming);", "paired collector append"),
        (validator, "observations.Count != responseTimings.Count", "count validation"),
        (validator, "for (var index = 0; index < observations.Count; index++)", "index alignment validation"),
        (validator, "timing.Timestamp != observation.Timestamp", "timestamp provenance"),
        (validator, "timing.PhysicalDiskNumber != observation.PhysicalDiskNumber", "disk provenance"),
        (validator, "timing.Operation != observation.Operation", "operation provenance"),
        (validator, "timing.Owner != observation.Owner", "process provenance"),
        (provider, "WindowsDiskIoTimingProvenanceValidator.Validate(", "provider provenance gate"),
        (provider, "provenance-bound decoded response-duration sample per completion", "provider provenance wording"),
        (collector_tests, "Assert.AreEqual(observation.Owner, timing.Owner);", "collector resolved-owner regression"),
        (collector_tests, "Assert.IsNull(snapshot.ResponseTimings[0].Owner);", "collector unresolved-owner regression"),
        (validator_tests, "ResolvedOwnerMismatchFailsClosed", "resolved mismatch regression"),
        (validator_tests, "ResolvedAndUnresolvedOwnerMismatchFailsClosed", "null/non-null mismatch regression"),
        (validator_tests, "TimestampDiskAndOperationMismatchesFailClosed", "event identity mismatch regressions"),
        (owner_tests, "InvalidOwnerProcessIdFailsClosed", "invalid owner PID regression"),
        (owner_tests, "OwnerStartingAfterCompletionFailsClosed", "invalid owner chronology regression"),
        (owner_tests, "WhitespaceOwnerImageNameFailsClosed", "invalid owner image regression"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
        (docs, "same resolver result", "provenance documentation"),
        (docs, "does not yet expose process-specific response timing", "no process timing claim"),
        (gate, "verify_disk_io_response_timing.py --repo-root $repoRoot --cases 50000", "existing timing verifier retained"),
        (gate, "verify_disk_io_timing_provenance.py --repo-root $repoRoot --cases 50000", "provenance verifier wiring"),
    ]
    for text, needle, label in required:
        checks += require(text, needle, label)

    provider_gate = provider.index("WindowsDiskIoTimingProvenanceValidator.Validate(")
    attribution_analysis = provider.index("DiskIoAttributionAnalyzer.Analyze(")
    timing_analysis = provider.index("DiskIoResponseTimingAnalyzer.Analyze(")
    if not provider_gate < attribution_analysis < timing_analysis:
        raise AssertionError("provider provenance validation must precede attribution/timing aggregation")
    checks += 1

    combined = timing + "\n" + collector + "\n" + provider + "\n" + ui
    for needle in (
        "Process.GetProcessById",
        "PeriodicTimer",
        "DispatcherQueueTimer",
        "health score",
        "latency score",
        "process bottleneck",
    ):
        checks += forbid(combined.lower(), needle.lower(), "new lookup/poller/process timing claim")

    checks += forbid(ui, ".Owner", "owner-specific timing UI")
    checks += forbid(ui, "Process response", "process response timing UI")
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0xD15C1)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = " and %s source checks" % format(source_checks, ",") if args.repo_root else ""
    print(
        "PASS: DiskIo timing provenance verified with %s model assertions across %s randomized cases%s."
        % (format(model_checks, ","), format(args.cases, ","), suffix)
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
