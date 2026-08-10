#!/usr/bin/env python3
"""Verify bounded DiskIo callback collection without GitHub Actions."""
from __future__ import annotations

import argparse
import random
from pathlib import Path


def collect(max_observations: int, events: list[tuple[bool, bool, bool]]) -> tuple[int, int, int, bool]:
    """Each event is (target_completion, in_window, owner_resolved)."""
    if max_observations <= 0:
        raise ValueError("max_observations")
    accepted = 0
    ignored = 0
    unresolved = 0
    stopped = False
    for target, in_window, owner_resolved in events:
        if stopped:
            break
        if not target or not in_window:
            ignored += 1
            continue
        accepted += 1
        if not owner_resolved:
            unresolved += 1
        if accepted >= max_observations:
            stopped = True
    return accepted, ignored, unresolved, stopped


def run_model(cases: int) -> int:
    checks = 0
    assert collect(2, [(True, True, True), (True, True, False), (True, True, True)]) == (2, 0, 1, True)
    assert collect(3, [(False, True, True), (True, False, True)]) == (0, 2, 0, False)
    checks += 2

    rng = random.Random(20260811)
    for _ in range(cases):
        cap = rng.randint(1, 100)
        source = [
            (bool(rng.getrandbits(1)), bool(rng.getrandbits(1)), bool(rng.getrandbits(1)))
            for _ in range(rng.randint(0, 200))
        ]
        accepted, ignored, unresolved, stopped = collect(cap, source)
        assert 0 <= accepted <= cap
        assert 0 <= unresolved <= accepted
        assert ignored >= 0
        assert stopped == (accepted == cap)
        checks += 4

        eligible = sum(1 for target, in_window, _ in source if target and in_window)
        assert accepted == min(cap, eligible)
        checks += 1
        if not stopped:
            assert ignored == sum(1 for target, in_window, _ in source if not target or not in_window)
            checks += 1
    return checks


def check_repository(root: Path) -> int:
    collector = (root / "src/FileOp.Windows/Performance/WindowsDiskIoCaptureCollector.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/WindowsDiskIoCaptureCollectorTests.cs").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    checks = 0

    for needle in (
        "IWindowsDiskIoNativeTraceCallbackSink",
        "WindowsEtwEventRecordSnapshot.CopyFrom(eventRecord)",
        "WindowsDiskIoEventRecordBridge.TryDecodeCompletion",
        "WindowsDiskIoIssuingThreadResolver.ConvertEventTimestamp(",
        "observationTimestamp < windowStart",
        "observationTimestamp > windowEnd",
        "_ownerResolver.Resolve(",
        "new DiskIoEventObservation(",
        "ownerResolution.Owner",
        "_observations.Count >= _maxObservations",
        "_observationLimitReached = true",
        "return false;",
        "UnresolvedOwnerCounts",
        "IgnoredEventCount",
        "ObservationLimitReached",
    ):
        assert needle in collector, needle
        checks += 1

    # Window filtering must happen before any thread/process lookup work.
    assert collector.index("observationTimestamp < windowStart") < collector.index("_ownerResolver.Resolve(")
    checks += 1

    for forbidden in (
        "StartTrace",
        "OpenTraceW",
        "ProcessTrace(",
        "CloseTrace(",
        "ControlTrace",
        "Task.Delay",
        "PeriodicTimer",
        "DispatcherQueueTimer",
        "Process.GetProcessById",
        "BinaryPrimitives",
    ):
        assert forbidden not in collector, forbidden
        checks += 1

    for needle in (
        "TargetCompletionBecomesResolvedObservation",
        "UnresolvedOwnerRemainsVisibleAndReasonIsCounted",
        "UnrelatedAndOutOfWindowEventsAreIgnoredBeforeOwnerLookup",
        "lifetime.OpenThreadCalls",
        "ExactObservationLimitStopsFurtherCallbacks",
        "ConfigurationIsRequiredAndSingleShot",
    ):
        assert needle in tests, needle
        checks += 1

    for verifier in (
        "verify_disk_io_thread_process_resolver.py",
        "verify_disk_io_trace_evidence.py",
        "verify_disk_io_trace_evidence_source.py",
        "verify_disk_io_loss_evidence.py",
        "verify_disk_io_capture_collector.py",
    ):
        assert verifier in gate, verifier
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
        "PASS: bounded DiskIo capture collector verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized streams{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
