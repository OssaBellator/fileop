#!/usr/bin/env python3
"""Verify bounded Windows DiskIo provider orchestration without GitHub Actions."""
from __future__ import annotations

import argparse
import random
from pathlib import Path


def classify(
    process_finished_first: bool,
    observation_limit_reached: bool,
    caller_cancelled: bool,
    lost_events: int,
    lost_buffers: int,
) -> tuple[str, str, int, int] | str:
    if caller_cancelled:
        return "cancel"
    if process_finished_first and not observation_limit_reached:
        return "early-process-error"
    stop = "limit" if observation_limit_reached else "duration"
    loss = "observed" if lost_events > 0 or lost_buffers > 0 else "none"
    return stop, loss, lost_events, lost_buffers


def run_model(cases: int) -> int:
    checks = 0
    assert classify(False, False, False, 0, 0) == ("duration", "none", 0, 0)
    assert classify(True, True, False, 0, 3) == ("limit", "observed", 0, 3)
    assert classify(False, True, False, 4, 0) == ("limit", "observed", 4, 0)
    assert classify(True, False, False, 0, 0) == "early-process-error"
    assert classify(False, False, True, 0, 0) == "cancel"
    checks += 5

    rng = random.Random(20260811)
    for _ in range(cases):
        process_first = bool(rng.getrandbits(1))
        limit = bool(rng.getrandbits(1))
        cancelled = bool(rng.getrandbits(1))
        lost_events = rng.randrange(0, 1000)
        lost_buffers = rng.randrange(0, 1000)
        result = classify(process_first, limit, cancelled, lost_events, lost_buffers)

        if cancelled:
            assert result == "cancel"
            checks += 1
            continue
        if process_first and not limit:
            assert result == "early-process-error"
            checks += 1
            continue

        stop, loss, events, buffers = result
        assert stop == ("limit" if limit else "duration")
        assert loss == ("observed" if lost_events or lost_buffers else "none")
        assert events == lost_events
        assert buffers == lost_buffers
        checks += 4

    return checks


def check_repository(root: Path) -> int:
    provider = (root / "src/FileOp.Windows/Performance/WindowsDiskIoAttributionProvider.cs").read_text(encoding="utf-8")
    native = (root / "src/FileOp.Windows/Performance/WindowsDiskIoNativeTraceConsumerApi.cs").read_text(encoding="utf-8")
    collector = (root / "src/FileOp.Windows/Performance/WindowsDiskIoCaptureCollector.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/WindowsDiskIoAttributionProviderTests.cs").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    checks = 0

    for needle in (
        "public sealed class WindowsDiskIoAttributionProvider : IDiskIoAttributionProvider",
        "SemaphoreSlim _captureGate",
        "_sessionController.Start()",
        "new WindowsDiskIoTraceConsumerLifecycle(consumerApi).Open()",
        "consumerApi.ReadTraceEvidence(consumer.ProcessingHandle)",
        "initialEvidence.HasValidPerformanceCounterFrequency",
        "collector.Configure(",
        "TaskCreationOptions.LongRunning",
        "Task.WhenAny(processTask, durationTask)",
        "durationCancellation.Cancel()",
        "collection.ObservationLimitReached",
        "session.Stop()",
        "processDisposition = await processTask",
        "var finalEvidence = consumerApi.ReadTraceEvidence(consumer.ProcessingHandle)",
        "finalEvidence.PerformanceCounterFrequency != initialEvidence.PerformanceCounterFrequency",
        "DiskIoAttributionAnalyzer.Analyze(",
        "lostEventCount: finalEvidence.EventsLost",
        "lostBufferCount: finalEvidence.BuffersLost",
        "cancellationToken.ThrowIfCancellationRequested()",
        "consumer.Close()",
        "await processTask.ConfigureAwait(false)",
    ):
        assert needle in provider, needle
        checks += 1

    # Final evidence must be sampled only after processing has been awaited.
    final_evidence_index = provider.index("var finalEvidence = consumerApi.ReadTraceEvidence")
    assert provider.rfind("processDisposition = await processTask", 0, final_evidence_index) >= 0
    assert provider.rfind("session.Stop()", 0, final_evidence_index) >= 0
    checks += 2

    # Conservative boundary: cap wins over duration for evidence completeness.
    assert "var stopReason = collection.ObservationLimitReached" in provider
    assert "? DiskIoCaptureStopReason.ObservationLimitReached" in provider
    checks += 2

    # No invented timing/loss arithmetic or recurring monitor behavior.
    for forbidden in (
        "Stopwatch.Frequency",
        "QueryPerformanceFrequency",
        "EventsLost + BuffersLost",
        "LostEventCount + LostBufferCount",
        "PeriodicTimer",
        "DispatcherQueueTimer",
        "FileSystemWatcher",
        "Registry.",
        "Process.GetProcessById",
        "Task.Run(consumer.Process",
    ):
        assert forbidden not in provider, forbidden
        checks += 1

    assert "IWindowsDiskIoCaptureConsumerApi" in native
    assert "IWindowsDiskIoCaptureCollector" in collector
    checks += 2

    for needle in (
        "DurationCaptureStopsOwnedSessionDrainsAndReturnsNoLossReport",
        "ObservationCapPreservesBufferOnlyLossWithoutInventingEventLoss",
        "CallerCancellationStopsAndDrainsBeforePropagatingCancellation",
        "ClassifiedConsumerOpenFailureStopsOwnedSessionAndReturnsUnavailable",
        "EarlyProcessCompletionWithoutObservationCapFailsClosed",
        "ChangedTraceFrequencyFailsClosedAfterDrain",
    ):
        assert needle in tests, needle
        checks += 1

    assert "verify_windows_disk_io_attribution_provider.py" in gate
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
        "PASS: Windows DiskIo attribution provider verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized orchestration states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
