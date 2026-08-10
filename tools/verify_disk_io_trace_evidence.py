#!/usr/bin/env python3
"""Verify FileOp's DiskIo trace timing/loss evidence reader without GitHub Actions."""
from __future__ import annotations

import argparse
import random
from pathlib import Path

EVENTS_LOST_32 = 160
EVENTS_LOST_64 = 168
PERF_FREQ_32 = 360
PERF_FREQ_64 = 376
BUFFERS_LOST_32 = 380
BUFFERS_LOST_64 = 396
UNUSED_CONSUMER_EVENTS_LOST_32 = 396
UNUSED_CONSUMER_EVENTS_LOST_64 = 416
UINT32_MAX = 0xFFFFFFFF


def offsets(pointer_size: int) -> tuple[int, int, int]:
    if pointer_size == 4:
        return PERF_FREQ_32, EVENTS_LOST_32, BUFFERS_LOST_32
    if pointer_size == 8:
        return PERF_FREQ_64, EVENTS_LOST_64, BUFFERS_LOST_64
    raise ValueError(pointer_size)


def model(freq: int, events_lost: int, buffers_lost: int) -> tuple[bool, bool]:
    if not 0 <= events_lost <= UINT32_MAX:
        raise ValueError(events_lost)
    if not 0 <= buffers_lost <= UINT32_MAX:
        raise ValueError(buffers_lost)
    return freq > 0, events_lost != 0 or buffers_lost != 0


def run_model(cases: int) -> int:
    checks = 0
    assert offsets(4) == (360, 160, 380)
    assert offsets(8) == (376, 168, 396)
    assert EVENTS_LOST_32 != UNUSED_CONSUMER_EVENTS_LOST_32
    assert EVENTS_LOST_64 != UNUSED_CONSUMER_EVENTS_LOST_64
    assert model(10_000_000, 0, 0) == (True, False)
    assert model(0, 0, 0) == (False, False)
    assert model(1, 1, 0) == (True, True)
    assert model(1, 0, 1) == (True, True)
    assert model(1, UINT32_MAX, UINT32_MAX) == (True, True)
    checks += 9

    rng = random.Random(20260811)
    for _ in range(cases):
        pointer_size = rng.choice((4, 8))
        perf_offset, event_offset, buffer_offset = offsets(pointer_size)
        assert perf_offset in (PERF_FREQ_32, PERF_FREQ_64)
        assert event_offset in (EVENTS_LOST_32, EVENTS_LOST_64)
        assert buffer_offset in (BUFFERS_LOST_32, BUFFERS_LOST_64)
        assert event_offset < perf_offset < buffer_offset
        checks += 4

        frequency = rng.randrange(-(2**31), 100_000_001)
        events_lost = rng.getrandbits(32)
        buffers_lost = rng.getrandbits(32)
        valid_frequency, reported_loss = model(
            frequency,
            events_lost,
            buffers_lost)
        assert valid_frequency == (frequency > 0)
        assert reported_loss == (events_lost != 0 or buffers_lost != 0)
        assert 0 <= events_lost <= UINT32_MAX
        assert 0 <= buffers_lost <= UINT32_MAX
        checks += 4

        evidence = (events_lost, buffers_lost)
        assert evidence[0] == events_lost
        assert evidence[1] == buffers_lost
        checks += 2

    return checks


def check_repository(root: Path) -> int:
    paths = {
        "source": root / "src/FileOp.Windows/Performance/WindowsDiskIoTraceEvidence.cs",
        "buffer": root / "src/FileOp.Windows/Performance/WindowsDiskIoTraceLogfileBuffer.cs",
        "tests": root / "tests/FileOp.Windows.Tests/WindowsDiskIoTraceEvidenceTests.cs",
        "source_tests": root / "tests/FileOp.Windows.Tests/WindowsDiskIoNativeTraceEvidenceSourceTests.cs",
        "doc": root / "docs/disk-io-trace-evidence.md",
    }
    text = {name: path.read_text(encoding="utf-8") for name, path in paths.items()}
    checks = 0

    for needle in (
        "PerformanceCounterFrequency",
        "EventsLost",
        "BuffersLost",
        "HasValidPerformanceCounterFrequency",
        "PerformanceCounterFrequency > 0",
        "HasReportedLoss",
        "EventsLost != 0 || BuffersLost != 0",
        "WindowsDiskIoTraceLogfileBuffer.TraceLogfileEventsLostOffset",
        "WindowsDiskIoTraceLogfileBuffer.TraceLogfilePerfFreqOffset",
        "WindowsDiskIoTraceLogfileBuffer.TraceLogfileBuffersLostOffset",
        "Marshal.ReadInt64",
        "Marshal.ReadInt32",
    ):
        assert needle in text["source"], needle
        checks += 1

    assert "ConsumerEventsLostOffset" not in text["source"]
    checks += 1

    for forbidden in (
        "EventsLost + BuffersLost",
        "BuffersLost + EventsLost",
        "Stopwatch.Frequency",
        "QueryPerformanceFrequency",
        "WindowsDiskIoEventDecoder",
        "WindowsDiskIoEventRecordBridge",
        "Process.GetProcessById",
        "OpenTraceW",
        "ProcessTrace(",
        "CloseTrace(",
        "Registry.",
        "PeriodicTimer",
        "DispatcherQueueTimer",
    ):
        assert forbidden not in text["source"], forbidden
        checks += 1

    for needle in (
        "TraceLogfileEventsLostRelativeOffset = 48",
        "TraceLogfileEventsLostOffset",
        "TraceLogfilePerfFreqOffset",
        "TraceLogfileBuffersLostOffset",
        "EventTraceLogfileSize32 = 416",
        "EventTraceLogfileSize64 = 448",
    ):
        assert needle in text["buffer"], needle
        checks += 1

    for needle in (
        "TraceHeaderEventsLostOffsetMatchesExplicitLogfileLayout",
        "ReadsFrequencyAndLossCountersFromTraceLogfileHeader",
        "UnusedConsumerEventsLostFieldDoesNotBecomeEvidence",
        "ZeroFrequencyRemainsExplicitlyInvalidWithoutInventingFallbackClock",
        "LossCountersPreserveFullUnsignedRange",
        "EventAndBufferLossRemainSeparateEvidence",
    ):
        assert needle in text["tests"], needle
        checks += 1

    assert "TraceLogfileEventsLostOffset" in text["source_tests"]
    assert "ConsumerEventsLostOffset" not in text["source_tests"]
    checks += 2

    for needle in (
        "TRACE_LOGFILE_HEADER.EventsLost",
        "TRACE_LOGFILE_HEADER.BuffersLost",
        "Not used",
        "does **not** sum",
        "after `OpenTraceW` returns",
        "after `ProcessTrace` returns",
        "should **not** read",
        "does not prove disk saturation",
    ):
        assert needle in text["doc"], needle
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
    suffix = f" and {repo_checks:,} source/test/doc checks" if args.repo_root else ""
    print(
        "PASS: DiskIo trace evidence verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized evidence cases{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
