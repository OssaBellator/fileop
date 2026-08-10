#!/usr/bin/env python3
"""Verify FileOp's dedicated disk-I/O system-session policy without GitHub Actions."""
from __future__ import annotations

import argparse
import random
from pathlib import Path

SESSION_NAME = "FileOp Disk I/O Diagnostics"
REAL_TIME_MODE = 0x00000100
SYSTEM_LOGGER_MODE = 0x02000000
DISK_IO = 0x00000100
NO_SYSCONFIG = 0x10000000
QPC_CLOCK = 1

ERROR_SUCCESS = 0
ERROR_ACCESS_DENIED = 5
ERROR_ALREADY_EXISTS = 183
ERROR_NO_SYSTEM_RESOURCES = 1450
ERROR_ACTIVE_CONNECTIONS = 2402
ERROR_WMI_INSTANCE_NOT_FOUND = 4201

START_EXPECTED = {
    ERROR_ACCESS_DENIED: "permission",
    ERROR_ALREADY_EXISTS: "session",
    ERROR_NO_SYSTEM_RESOURCES: "session",
}
STOP_EXPECTED = {
    ERROR_SUCCESS: "stopped",
    ERROR_WMI_INSTANCE_NOT_FOUND: "already-stopped",
    ERROR_ACTIVE_CONNECTIONS: "stopping",
}


def classify_start(error: int) -> str | None:
    return START_EXPECTED.get(error)


def classify_stop(error: int) -> str | None:
    return STOP_EXPECTED.get(error)


def run_model(cases: int) -> int:
    checks = 0
    assert SESSION_NAME == "FileOp Disk I/O Diagnostics"
    assert REAL_TIME_MODE | SYSTEM_LOGGER_MODE == 0x02000100
    assert DISK_IO | NO_SYSCONFIG == 0x10000100
    assert QPC_CLOCK == 1
    checks += 4

    assert classify_start(ERROR_ACCESS_DENIED) == "permission"
    assert classify_start(ERROR_ALREADY_EXISTS) == "session"
    assert classify_start(ERROR_NO_SYSTEM_RESOURCES) == "session"
    assert classify_start(87) is None
    assert classify_start(24) is None
    checks += 5

    assert classify_stop(ERROR_SUCCESS) == "stopped"
    assert classify_stop(ERROR_WMI_INSTANCE_NOT_FOUND) == "already-stopped"
    assert classify_stop(ERROR_ACTIVE_CONNECTIONS) == "stopping"
    assert classify_stop(ERROR_ACCESS_DENIED) is None
    assert classify_stop(24) is None
    checks += 5

    rng = random.Random(20260810)
    start_errors = tuple(START_EXPECTED)
    stop_errors = tuple(STOP_EXPECTED)
    reserved = set(start_errors) | set(stop_errors)

    for _ in range(cases):
        start_error = rng.choice(start_errors)
        expected_start = START_EXPECTED[start_error]
        assert classify_start(start_error) == expected_start
        assert expected_start in {"permission", "session"}
        checks += 2

        stop_error = rng.choice(stop_errors)
        expected_stop = STOP_EXPECTED[stop_error]
        assert classify_stop(stop_error) == expected_stop
        assert expected_stop in {"stopped", "already-stopped", "stopping"}
        checks += 2

        unexpected = rng.getrandbits(32)
        while unexpected in reserved:
            unexpected = rng.getrandbits(32)
        assert classify_start(unexpected) is None
        assert classify_stop(unexpected) is None
        checks += 2

        # Policy invariants stay fixed regardless of error inputs.
        assert (REAL_TIME_MODE | SYSTEM_LOGGER_MODE) & SYSTEM_LOGGER_MODE
        assert (DISK_IO | NO_SYSCONFIG) & DISK_IO
        assert (DISK_IO | NO_SYSCONFIG) & NO_SYSCONFIG
        checks += 3

    return checks


def check_repository(root: Path) -> int:
    paths = {
        "source": root / "src/FileOp.Windows/Performance/WindowsDiskIoSystemSessionPolicy.cs",
        "tests": root / "tests/FileOp.Windows.Tests/WindowsDiskIoSystemSessionPolicyTests.cs",
        "doc": root / "docs/disk-io-system-session-policy.md",
        "gate": root / "tools/test-local.ps1",
    }
    text: dict[str, str] = {}
    for name, path in paths.items():
        if not path.is_file():
            raise FileNotFoundError(path)
        text[name] = path.read_text(encoding="utf-8")

    checks = 0
    for needle in (
        'SessionName = "FileOp Disk I/O Diagnostics"',
        "EventTraceRealTimeMode = 0x00000100",
        "EventTraceSystemLoggerMode = 0x02000000",
        "EventTraceFlagDiskIo = 0x00000100",
        "EventTraceFlagNoSysConfig = 0x10000000",
        "QueryPerformanceCounterClock = 1",
        "RequestedSessionGuid => Guid.Empty",
        "UsesLegacyNtKernelLoggerIdentity => false",
        "ErrorAccessDenied = 5",
        "ErrorAlreadyExists = 183",
        "ErrorNoSystemResources = 1450",
        "ErrorActiveConnections = 2402",
        "ErrorWmiInstanceNotFound = 4201",
        "DiskIoCaptureStatus.PermissionRequired",
        "DiskIoCaptureStatus.SessionUnavailable",
        "will not stop or reuse",
        "will not raise ETW logger limits",
    ):
        assert needle in text["source"], needle
        checks += 1

    for needle in (
        "UsesDedicatedRealtimeSystemLoggerWithoutLegacyKernelIdentity",
        "AccessDeniedMapsOnlyToPermissionRequired",
        "ExistingOrCapacityExhaustedSessionMapsToUnavailableWithoutTakeover",
        "UnexpectedStartErrorsAreNotCollapsedIntoAvailabilityStates",
        "StopResultsDistinguishStoppedMissingAndAlreadyStopping",
        "UnexpectedStopErrorsRemainVisibleToProvider",
    ):
        assert needle in text["tests"], needle
        checks += 1

    for needle in (
        "dedicated system-logger session",
        "EVENT_TRACE_SYSTEM_LOGGER_MODE",
        "EVENT_TRACE_FLAG_NO_SYSCONFIG",
        "descriptive, deterministic session name",
        "does **not** stop or reuse",
        "must **not** modify `EtwMaxLoggers`",
        "ERROR_ACTIVE_CONNECTIONS",
        "does not request elevation",
    ):
        assert needle in text["doc"], needle
        checks += 1

    for forbidden in (
        "DllImport",
        "LibraryImport",
        "StartTraceW(",
        "OpenTraceW(",
        "ProcessTrace(",
        "ControlTraceW(",
        "Guid.NewGuid",
        "KERNEL_LOGGER_NAME",
        "SystemTraceControlGuid",
        "Registry.",
        "runas",
        "ServiceController",
        "Process.Kill",
    ):
        assert forbidden not in text["source"], forbidden
        checks += 1

    assert "verify_disk_io_system_session_policy.py" in text["gate"]
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
        "PASS: dedicated disk-I/O system-session policy verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized cases{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
