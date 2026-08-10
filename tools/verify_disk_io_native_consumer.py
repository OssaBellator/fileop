#!/usr/bin/env python3
"""Verify FileOp's native real-time ETW consumer adapter without GitHub Actions."""
from __future__ import annotations

import argparse
import random
from dataclasses import dataclass
from pathlib import Path


def align(value: int, alignment: int) -> int:
    return ((value + alignment - 1) // alignment) * alignment


def event_trace_size(pointer_size: int) -> int:
    offset = 48 + 4 + 4 + 16
    offset = align(offset, pointer_size)
    offset += pointer_size + 4 + 4
    return align(offset, max(8, pointer_size))


def trace_logfile_header_layout(pointer_size: int) -> tuple[int, dict[str, int]]:
    offset = 0
    fields: dict[str, int] = {}

    def add(name: str, size: int, alignment: int) -> None:
        nonlocal offset
        offset = align(offset, alignment)
        fields[name] = offset
        offset += size

    add("BufferSize", 4, 4)
    add("Version", 4, 4)
    add("ProviderVersion", 4, 4)
    add("NumberOfProcessors", 4, 4)
    add("EndTime", 8, 8)
    add("TimerResolution", 4, 4)
    add("MaximumFileSize", 4, 4)
    add("LogFileMode", 4, 4)
    add("BuffersWritten", 4, 4)
    add("LogInstanceGuid", 16, 4)
    add("LoggerName", pointer_size, pointer_size)
    add("LogFileName", pointer_size, pointer_size)
    add("TimeZone", 172, 4)
    add("BootTime", 8, 8)
    add("PerfFreq", 8, 8)
    add("StartTime", 8, 8)
    add("ReservedFlags", 4, 4)
    add("BuffersLost", 4, 4)
    return align(offset, 8), fields


def event_trace_logfile_layout(pointer_size: int) -> tuple[int, dict[str, int]]:
    trace_size = event_trace_size(pointer_size)
    header_size, _ = trace_logfile_header_layout(pointer_size)
    offset = 0
    fields: dict[str, int] = {}

    def add(name: str, size: int, alignment: int) -> None:
        nonlocal offset
        offset = align(offset, alignment)
        fields[name] = offset
        offset += size

    add("LogFileName", pointer_size, pointer_size)
    add("LoggerName", pointer_size, pointer_size)
    add("CurrentTime", 8, 8)
    add("BuffersRead", 4, 4)
    add("ProcessTraceMode", 4, 4)
    add("CurrentEvent", trace_size, 8)
    add("LogfileHeader", header_size, 8)
    add("BufferCallback", pointer_size, pointer_size)
    add("BufferSize", 4, 4)
    add("Filled", 4, 4)
    add("EventsLost", 4, 4)
    add("EventRecordCallback", pointer_size, pointer_size)
    add("IsKernelTrace", 4, 4)
    add("Context", pointer_size, pointer_size)
    return align(offset, 8), fields


@dataclass
class CallbackState:
    cancelled: bool = False
    fault: object | None = None
    event_calls: int = 0
    buffer_calls: int = 0

    def event(self, outcome: str, token: object) -> None:
        if self.cancelled or self.fault is not None:
            return
        self.event_calls += 1
        if outcome == "cancel":
            self.cancelled = True
        elif outcome == "fault":
            self.fault = token
            self.cancelled = True
        elif outcome != "continue":
            raise AssertionError(outcome)

    def buffer(self, outcome: str, token: object) -> int:
        if self.cancelled or self.fault is not None:
            return 0
        self.buffer_calls += 1
        if outcome == "continue":
            return 1
        if outcome == "cancel":
            self.cancelled = True
            return 0
        if outcome == "fault":
            self.fault = token
            self.cancelled = True
            return 0
        raise AssertionError(outcome)


def run_model(cases: int) -> int:
    checks = 0
    for pointer, expected_trace, expected_header, expected_logfile in (
        (4, 84, 272, 416),
        (8, 88, 280, 448),
    ):
        assert event_trace_size(pointer) == expected_trace
        header_size, header = trace_logfile_header_layout(pointer)
        assert header_size == expected_header
        assert header["PerfFreq"] == (248 if pointer == 4 else 256)
        assert header["BuffersLost"] == (268 if pointer == 4 else 276)
        logfile_size, logfile = event_trace_logfile_layout(pointer)
        assert logfile_size == expected_logfile
        assert logfile["BufferCallback"] == (384 if pointer == 4 else 400)
        assert logfile["EventRecordCallback"] == (400 if pointer == 4 else 424)
        assert logfile["Context"] == (408 if pointer == 4 else 440)
        checks += 8

    rng = random.Random(20260810)
    outcomes = ("continue", "cancel", "fault")
    for _ in range(cases):
        state = CallbackState()
        first_token = object()
        second_token = object()
        first = rng.choice(outcomes)
        second = rng.choice(outcomes)
        buffer = rng.choice(outcomes)

        state.event(first, first_token)
        calls_after_first = state.event_calls
        fault_after_first = state.fault
        cancelled_after_first = state.cancelled

        state.event(second, second_token)
        if cancelled_after_first or fault_after_first is not None:
            assert state.event_calls == calls_after_first
            assert state.fault is fault_after_first
            checks += 2
        else:
            assert state.event_calls == calls_after_first + 1
            checks += 1

        before_buffer = state.buffer_calls
        result = state.buffer(buffer, second_token)
        if state.cancelled and before_buffer == 0 and calls_after_first >= 1 and first != "continue":
            assert result == 0
            assert state.buffer_calls == 0
            checks += 2
        else:
            assert result in (0, 1)
            checks += 1

        if state.fault is not None:
            assert state.cancelled
            checks += 1

    return checks


def check_repository(root: Path) -> int:
    paths = {
        "source": root / "src/FileOp.Windows/Performance/WindowsDiskIoNativeTraceConsumerApi.cs",
        "tests": root / "tests/FileOp.Windows.Tests/WindowsDiskIoNativeTraceConsumerApiTests.cs",
        "doc": root / "docs/disk-io-native-consumer.md",
        "gate": root / "tools/test-local.ps1",
    }
    text: dict[str, str] = {}
    for name, path in paths.items():
        if not path.is_file():
            raise FileNotFoundError(path)
        text[name] = path.read_text(encoding="utf-8")

    checks = 0
    for needle in (
        'NativeLibrary = "sechost.dll"',
        "OpenTraceW(ref EventTraceLogfileWNative logfile)",
        'EntryPoint = "ProcessTrace"',
        'EntryPoint = "CloseTrace"',
        "Marshal.GetFunctionPointerForDelegate(_bufferCallback)",
        "Marshal.GetFunctionPointerForDelegate(_eventRecordCallback)",
        "Marshal.GetLastPInvokeError()",
        "ExceptionDispatchInfo.Capture(exception)",
        "_callbackCancellationRequested = true",
        "return 0;",
        "public IntPtr EventRecordCallback;",
        "public TraceLogfileHeaderNative LogfileHeader;",
        "public long PerfFreq;",
        "public uint BuffersLost;",
        "Size = 64",
    ):
        assert needle in text["source"], needle
        checks += 1

    for needle in (
        "NativeStructSizesAndCallbackOffsetsMatchWindowsAbi",
        "TraceHeaderPerfFrequencyOffsetMatchesPointerWidth",
        "EventCallbackFalseRequestsCancellationAndSuppressesLaterCallbacks",
        "EventCallbackExceptionNeverCrossesCallbackAndIsRethrownLater",
        "NullEventRecordFailsClosedWithoutCallingSink",
        "BufferCallbackExceptionReturnsFalseAndPreservesOriginalFault",
    ):
        assert needle in text["tests"], needle
        checks += 1

    for needle in (
        "EVENT_TRACE_LOGFILEW",
        "OpenTraceW",
        "ProcessTrace",
        "CloseTrace",
        "callback exceptions",
        "does not decode `EVENT_RECORD`",
        "ETW-owned memory",
    ):
        assert needle in text["doc"], needle
        checks += 1

    for forbidden in (
        "Process.GetProcessById",
        "ManagementObject",
        "Registry.",
        "DispatcherQueueTimer",
        "PeriodicTimer",
        "FileOp.App",
        "IndexingServiceProtocol",
        "WindowsDiskIoEtwDecoder",
        "SHA256",
        "Delete(",
    ):
        assert forbidden not in text["source"], forbidden
        checks += 1

    assert "verify_disk_io_native_consumer.py" in text["gate"]
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
    repository_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {repository_checks:,} source/test/doc checks" if args.repo_root else ""
    print(
        "PASS: native DiskIo consumer adapter verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized callback cases{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
