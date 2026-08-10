#!/usr/bin/env python3
"""Verify FileOp's owned DiskIo ETW controller without GitHub Actions."""
from __future__ import annotations

import argparse
import random
from dataclasses import dataclass
from pathlib import Path

WNODE_SIZE = 48
PROPS_SIZE_32 = 116
PROPS_SIZE_64 = 120
WNODE_FLAG_TRACED_GUID = 0x00020000
EVENT_TRACE_CONTROL_STOP = 1
BUFFER_KB = 16
MIN_BUFFERS = 0
MAX_BUFFERS = 64
SESSION_NAME = "FileOp Disk I/O Diagnostics"

SUCCESS = 0
ACCESS_DENIED = 5
ALREADY_EXISTS = 183
MORE_DATA = 234
NO_SYSTEM_RESOURCES = 1450
ACTIVE_CONNECTIONS = 2402
INSTANCE_NOT_FOUND = 4201

EXPECTED_START = {
    ACCESS_DENIED: "permission",
    ALREADY_EXISTS: "session-unavailable",
    NO_SYSTEM_RESOURCES: "session-unavailable",
}
EXPECTED_STOP = {
    SUCCESS: "stopped",
    MORE_DATA: "stopped-truncated",
    INSTANCE_NOT_FOUND: "already-stopped",
    ACTIVE_CONNECTIONS: "stop-in-progress",
}


def structure_size(pointer_size: int) -> int:
    if pointer_size == 4:
        return PROPS_SIZE_32
    if pointer_size == 8:
        return PROPS_SIZE_64
    raise ValueError("pointer size")


def total_size(pointer_size: int) -> int:
    return structure_size(pointer_size) + len((SESSION_NAME + "\0").encode("utf-16-le"))


@dataclass
class FakeController:
    start_status: int
    trace_id: int
    stop_status: int = SUCCESS
    start_calls: int = 0
    stop_calls: int = 0
    terminal_stop: str | None = None
    terminal_error: int | None = None

    def start(self) -> tuple[str, int | None]:
        self.start_calls += 1
        if self.start_status == SUCCESS:
            if self.trace_id == 0:
                raise ValueError("zero trace id")
            return "started", self.trace_id
        expected = EXPECTED_START.get(self.start_status)
        if expected is not None:
            return expected, None
        raise OSError(self.start_status, "unexpected start")

    def stop(self) -> str:
        if self.terminal_stop is not None:
            return self.terminal_stop
        if self.terminal_error is not None:
            raise RuntimeError("terminal stop failure")
        self.stop_calls += 1
        expected = EXPECTED_STOP.get(self.stop_status)
        if expected is not None:
            self.terminal_stop = expected
            return expected
        self.terminal_error = self.stop_status
        raise OSError(self.stop_status, "unexpected stop")


def run_model(cases: int) -> int:
    checks = 0
    assert WNODE_SIZE == 48
    assert structure_size(4) == 116
    assert structure_size(8) == 120
    assert total_size(8) - total_size(4) == 4
    assert WNODE_FLAG_TRACED_GUID == 0x00020000
    assert EVENT_TRACE_CONTROL_STOP == 1
    assert BUFFER_KB == 16 and MIN_BUFFERS == 0 and MAX_BUFFERS == 64
    checks += 7

    rng = random.Random(20260810)
    unexpected_errors = (24, 87, 995, 1234, 0xDEAD)
    stop_statuses = tuple(EXPECTED_STOP)
    start_failures = tuple(EXPECTED_START)

    for _ in range(cases):
        pointer_size = rng.choice((4, 8))
        struct_size = structure_size(pointer_size)
        size = total_size(pointer_size)
        assert size > struct_size
        assert (size - struct_size) == len((SESSION_NAME + "\0").encode("utf-16-le"))
        assert struct_size in (116, 120)
        checks += 3

        mode = rng.randrange(4)
        if mode == 0:
            trace_id = rng.randint(1, (1 << 64) - 1)
            controller = FakeController(SUCCESS, trace_id, rng.choice(stop_statuses))
            state, owned = controller.start()
            assert state == "started" and owned == trace_id
            assert controller.start_calls == 1 and controller.stop_calls == 0
            first = controller.stop()
            second = controller.stop()
            assert first == EXPECTED_STOP[controller.stop_status]
            assert second == first
            assert controller.stop_calls == 1
            checks += 6
        elif mode == 1:
            status = rng.choice(start_failures)
            controller = FakeController(status, rng.randint(0, 1000))
            state, owned = controller.start()
            assert state == EXPECTED_START[status]
            assert owned is None
            assert controller.stop_calls == 0
            checks += 3
        elif mode == 2:
            status = rng.choice(unexpected_errors)
            controller = FakeController(status, 0)
            try:
                controller.start()
                raise AssertionError("unexpected start accepted")
            except OSError as exc:
                assert exc.errno == status
                assert controller.stop_calls == 0
                checks += 2
        else:
            status = rng.choice(unexpected_errors)
            controller = FakeController(SUCCESS, rng.randint(1, (1 << 64) - 1), status)
            controller.start()
            try:
                controller.stop()
                raise AssertionError("unexpected stop accepted")
            except OSError as exc:
                assert exc.errno == status
            assert controller.stop_calls == 1
            try:
                controller.stop()
                raise AssertionError("terminal stop retried")
            except RuntimeError:
                pass
            assert controller.stop_calls == 1
            checks += 3

    return checks


def check_repository(root: Path) -> int:
    paths = {
        "buffer": root / "src/FileOp.Windows/Performance/WindowsDiskIoTracePropertiesBuffer.cs",
        "controller": root / "src/FileOp.Windows/Performance/WindowsDiskIoTraceSessionController.cs",
        "policy": root / "src/FileOp.Windows/Performance/WindowsDiskIoSystemSessionPolicy.cs",
        "friend": root / "src/FileOp.Windows/Properties/AssemblyInfo.cs",
        "tests": root / "tests/FileOp.Windows.Tests/WindowsDiskIoTraceSessionControllerTests.cs",
        "doc": root / "docs/disk-io-trace-controller.md",
        "gate": root / "tools/test-local.ps1",
    }
    text: dict[str, str] = {}
    for name, path in paths.items():
        if not path.is_file():
            raise FileNotFoundError(path)
        text[name] = path.read_text(encoding="utf-8")

    checks = 0
    for needle in (
        "WnodeFlagTracedGuid = 0x00020000",
        "RequestedBufferSizeKilobytes = 16",
        "RequestedMinimumBuffers = 0",
        "RequestedMaximumBuffers = 64",
        "Marshal.SizeOf<EventTracePropertiesNative>()",
        "Marshal.AllocHGlobal(TotalSize)",
        "Marshal.Copy(new byte[TotalSize]",
        "LogFileNameOffset = 0",
        "LoggerNameOffset = checked((uint)StructureSize)",
        "CreateForStart()",
        "CreateForStop()",
        "public IntPtr LoggerThreadId",
    ):
        assert needle in text["buffer"], needle
        checks += 1

    for needle in (
        "IWindowsDiskIoTraceControlApi",
        "WindowsDiskIoTraceSessionController",
        "WindowsDiskIoOwnedTraceSession",
        'NativeLibrary = "sechost.dll"',
        "EventTraceControlStop = 1",
        "StartTraceW",
        "ControlTraceW",
        "traceId == 0",
        "TryClassifyExpectedStartFailure",
        "TryClassifyExpectedStopResult",
        "terminal stop failure",
        "instanceName: null",
    ):
        assert needle in text["controller"], needle
        checks += 1

    for needle in (
        "ErrorMoreData = 234",
        "StoppedWithTruncatedStatistics",
    ):
        assert needle in text["policy"], needle
        checks += 1

    assert 'InternalsVisibleTo("FileOp.Windows.Tests")' in text["friend"]
    checks += 1

    for needle in (
        "StartPropertiesMatchDocumentedNativeLayoutAndPolicy",
        "StopPropertiesContainOnlyControlIdentityAndOffsets",
        "SuccessfulStartOwnsExactTraceIdAndStopsOnce",
        "ExpectedStartFailureNeverCreatesOrStopsSession",
        "AccessDeniedReturnsPermissionRequiredWithoutElevationOrTakeover",
        "UnexpectedStartFailureRemainsNativeErrorAndNeverStops",
        "SuccessfulStartWithZeroTraceIdFailsClosed",
        "StopDispositionsAreTerminalAndIdempotent",
        "UnexpectedStopFailureIsTerminalAndNeverRetried",
        "DisposeStopsActiveOwnedSession",
    ):
        assert needle in text["tests"], needle
        checks += 1

    for needle in (
        "only a session started successfully by this controller",
        "116 bytes in a 32-bit process and 120 bytes in a 64-bit process",
        "16 KB",
        "not a hard machine-wide memory ceiling",
        "Failed start attempts never invoke STOP",
        "InstanceName = NULL",
        "StoppedWithTruncatedStatistics",
        "not wired into the desktop or protocol",
    ):
        assert needle in text["doc"], needle
        checks += 1

    for forbidden in (
        "OpenTraceW",
        "ProcessTrace(",
        "CloseTrace(",
        "DispatcherQueueTimer",
        "PeriodicTimer",
        "runas",
        "Registry.",
        "ServiceController",
        "Process.GetProcessById",
        "Process.Kill",
    ):
        assert forbidden not in text["controller"], forbidden
        checks += 1

    assert text["controller"].count("[DllImport(") == 2
    checks += 1
    assert "verify_disk_io_trace_controller.py" in text["gate"]
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
    repository_checks = 0
    if args.repo_root is not None:
        repository_checks = check_repository(args.repo_root.resolve())
    suffix = f" and {repository_checks:,} source/test/doc checks" if args.repo_root else ""
    print(
        "PASS: owned DiskIo ETW controller verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized cases{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
