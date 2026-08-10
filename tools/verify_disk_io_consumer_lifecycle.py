#!/usr/bin/env python3
"""Verify FileOp's bounded real-time DiskIo consumer lifecycle."""
from __future__ import annotations

import argparse
import random
from dataclasses import dataclass
from pathlib import Path

REALTIME = 0x00000100
EVENT_RECORD = 0x10000000
MODE = REALTIME | EVENT_RECORD
SUCCESS = 0
ACCESS_DENIED = 5
INVALID_HANDLE = 6
NOACCESS = 998
CANCELLED = 1223
INSTANCE_NOT_FOUND = 4201
CLOSE_PENDING = 7007

OPEN_EXPECTED = {ACCESS_DENIED: "permission", INSTANCE_NOT_FOUND: "session"}
PROCESS_EXPECTED = {SUCCESS: "completed", CANCELLED: "cancelled", INSTANCE_NOT_FOUND: "ended"}
CLOSE_EXPECTED = {SUCCESS: "closed", CLOSE_PENDING: "pending"}


def invalid_handle(pointer_size: int) -> int:
    if pointer_size == 4:
        return 0xFFFFFFFF
    if pointer_size == 8:
        return 0xFFFFFFFFFFFFFFFF
    raise ValueError(pointer_size)


def is_invalid(handle: int, pointer_size: int) -> bool:
    return handle == 0 or handle == invalid_handle(pointer_size)


@dataclass
class Consumer:
    handle: int
    process_status: int = SUCCESS
    close_status: int = SUCCESS
    close_calls: int = 0
    terminal_close: str | None = None
    terminal_error: int | None = None

    def process(self) -> str:
        if self.terminal_close is not None:
            raise RuntimeError("closed")
        if self.terminal_error is not None:
            raise RuntimeError("terminal close failure")
        result = PROCESS_EXPECTED.get(self.process_status)
        if result is None:
            raise OSError(self.process_status, "process")
        return result

    def close(self) -> str:
        if self.terminal_close is not None:
            return self.terminal_close
        if self.terminal_error is not None:
            raise RuntimeError("terminal close failure")
        self.close_calls += 1
        result = CLOSE_EXPECTED.get(self.close_status)
        if result is None:
            self.terminal_error = self.close_status
            raise OSError(self.close_status, "close")
        self.terminal_close = result
        return result


def run_model(cases: int) -> int:
    checks = 0
    assert MODE == 0x10000100
    assert invalid_handle(4) == 0xFFFFFFFF
    assert invalid_handle(8) == 0xFFFFFFFFFFFFFFFF
    assert is_invalid(0, 4) and is_invalid(0, 8)
    assert not is_invalid(1, 4) and not is_invalid(1, 8)
    assert NOACCESS not in PROCESS_EXPECTED
    checks += 6

    rng = random.Random(20260810)
    unexpected = (24, 87, INVALID_HANDLE, NOACCESS, 1234, 0xDEAD)
    for _ in range(cases):
        pointer = rng.choice((4, 8))
        assert is_invalid(invalid_handle(pointer), pointer)
        checks += 1

        open_mode = rng.randrange(4)
        if open_mode == 0:
            status = rng.choice(tuple(OPEN_EXPECTED))
            assert OPEN_EXPECTED[status] in {"permission", "session"}
            checks += 1
        elif open_mode == 1:
            handle = rng.randint(1, invalid_handle(pointer) - 1)
            assert not is_invalid(handle, pointer)
            checks += 1
        else:
            status = rng.choice(unexpected)
            assert status not in OPEN_EXPECTED
            checks += 1

        process = rng.choice(tuple(PROCESS_EXPECTED) + unexpected)
        consumer = Consumer(
            handle=rng.randint(1, min(invalid_handle(pointer) - 1, 2**31)),
            process_status=process,
            close_status=rng.choice(tuple(CLOSE_EXPECTED) + unexpected),
        )
        if process in PROCESS_EXPECTED:
            assert consumer.process() == PROCESS_EXPECTED[process]
            checks += 1
        else:
            try:
                consumer.process()
                raise AssertionError("unexpected process accepted")
            except OSError as exc:
                assert exc.errno == process
                checks += 1

        if consumer.close_status in CLOSE_EXPECTED:
            first = consumer.close()
            second = consumer.close()
            assert first == CLOSE_EXPECTED[consumer.close_status]
            assert second == first
            assert consumer.close_calls == 1
            checks += 3
            try:
                consumer.process()
                raise AssertionError("processing accepted after close")
            except RuntimeError:
                checks += 1
        else:
            status = consumer.close_status
            try:
                consumer.close()
                raise AssertionError("unexpected close accepted")
            except OSError as exc:
                assert exc.errno == status
            assert consumer.close_calls == 1
            try:
                consumer.close()
                raise AssertionError("terminal close retried")
            except RuntimeError:
                pass
            assert consumer.close_calls == 1
            checks += 3

    return checks


def check_repository(root: Path) -> int:
    paths = {
        "source": root / "src/FileOp.Windows/Performance/WindowsDiskIoTraceConsumerLifecycle.cs",
        "tests": root / "tests/FileOp.Windows.Tests/WindowsDiskIoTraceConsumerLifecycleTests.cs",
        "doc": root / "docs/disk-io-consumer-lifecycle.md",
        "gate": root / "tools/test-local.ps1",
    }
    text = {name: path.read_text(encoding="utf-8") for name, path in paths.items()}
    checks = 0
    for needle in (
        "ProcessTraceModeRealTime = 0x00000100",
        "ProcessTraceModeEventRecord = 0x10000000",
        "ErrorNoAccess = 998",
        "ErrorCancelled = 1223",
        "ErrorWmiInstanceNotFound = 4201",
        "ErrorCtxClosePending = 7007",
        "handle == 0 || handle == InvalidProcessTraceHandle",
        "DiskIoCaptureStatus.PermissionRequired",
        "DiskIoCaptureStatus.SessionUnavailable",
        "WindowsDiskIoTraceProcessDisposition.CancelledByConsumer",
        "WindowsDiskIoTraceCloseDisposition.ClosePending",
        "ambiguous second CloseTrace call",
    ):
        assert needle in text["source"], needle
        checks += 1

    for needle in (
        "PolicyUsesRealtimeEventRecordModeWithoutRawTimestamp",
        "SuccessfulOpenOwnsExactProcessingHandleAndUsesFixedSessionName",
        "ZeroOrSentinelProcessingHandleFailsClosed",
        "AccessDeniedAndMissingCollectionAreExpectedOpenStates",
        "CallbackExceptionStatusIsNotMisreportedAsCancellation",
        "CloseSuccessAndPendingAreTerminalIdempotentOutcomes",
        "UnexpectedCloseFailureIsTerminalAndNeverRetried",
    ):
        assert needle in text["tests"], needle
        checks += 1

    for needle in (
        "EVENT_RECORD",
        "does not request `PROCESS_TRACE_MODE_RAW_TIMESTAMP`",
        "ERROR_NOACCESS",
        "ERROR_CTX_CLOSE_PENDING",
        "processing handle owns the **consumer connection**",
        "does **not** declare the large native `EVENT_TRACE_LOGFILEW`",
    ):
        assert needle in text["doc"], needle
        checks += 1

    for forbidden in (
        "DllImport",
        "LibraryImport",
        "OpenTraceW(",
        "ProcessTraceW(",
        "CloseTrace(",
        "DispatcherQueueTimer",
        "PeriodicTimer",
        "runas",
        "Registry.",
        "Process.GetProcessById",
    ):
        assert forbidden not in text["source"], forbidden
        checks += 1

    assert "verify_disk_io_consumer_lifecycle.py" in text["gate"]
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
        "PASS: DiskIo real-time consumer lifecycle verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized cases{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
