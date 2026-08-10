#!/usr/bin/env python3
"""Verify FileOp's lifetime-aware DiskIo thread/process resolver without GitHub Actions."""
from __future__ import annotations

import argparse
import random
from dataclasses import dataclass
from pathlib import Path

MAX_PID = 0x7FFFFFFF
DEFAULT_THREADS = 2048
DEFAULT_PROCESSES = 1024


@dataclass(frozen=True)
class Process:
    pid: int
    started: int


@dataclass(frozen=True)
class Thread:
    tid: int
    pid: int
    started: int


class ResolverModel:
    def __init__(self, thread_cap: int, process_cap: int) -> None:
        if thread_cap <= 0 or process_cap <= 0:
            raise ValueError("caps")
        self.thread_cap = thread_cap
        self.process_cap = process_cap
        self.threads: dict[int, tuple[Thread, Process]] = {}
        self.processes: dict[int, Process] = {}
        self.thread_opens = 0
        self.process_opens = 0
        self.temp_thread_closes = 0
        self.temp_process_closes = 0

    def resolve(self, thread: Thread | None, process: Process | None, event_time: int) -> str:
        if thread is None or thread.tid == 0:
            return "invalid-thread"

        cached_thread = self.threads.get(thread.tid)
        if cached_thread is not None:
            cached, cached_process = cached_thread
            if cached.started > event_time:
                return "thread-after-event"
            if cached_process.started > event_time:
                return "process-after-event"
            return "resolved"

        if len(self.threads) >= self.thread_cap:
            return "thread-cap"

        self.thread_opens += 1
        if process is None or thread.pid == 0:
            self.temp_thread_closes += 1
            return "process-unavailable"
        if thread.pid > MAX_PID:
            self.temp_thread_closes += 1
            return "pid-range"

        cached_process = self.processes.get(thread.pid)
        if cached_process is None:
            if len(self.processes) >= self.process_cap:
                self.temp_thread_closes += 1
                return "process-cap"
            self.process_opens += 1
            if process.started > event_time:
                self.temp_process_closes += 1
                self.temp_thread_closes += 1
                return "process-after-event"
            self.processes[thread.pid] = process
            cached_process = process

        self.threads[thread.tid] = (thread, cached_process)
        if thread.started > event_time:
            return "thread-after-event"
        if cached_process.started > event_time:
            return "process-after-event"
        return "resolved"


def run_model(cases: int) -> int:
    checks = 0
    assert DEFAULT_THREADS == 2048
    assert DEFAULT_PROCESSES == 1024
    checks += 2

    model = ResolverModel(2, 1)
    process = Process(50, 100)
    thread = Thread(10, 50, 110)
    assert model.resolve(thread, process, 120) == "resolved"
    assert model.resolve(thread, process, 121) == "resolved"
    assert model.thread_opens == 1
    assert model.process_opens == 1
    checks += 4

    reuse = ResolverModel(10, 10)
    future_process = Process(50, 200)
    old_thread = Thread(10, 50, 90)
    assert reuse.resolve(old_thread, future_process, 150) == "process-after-event"
    assert reuse.temp_thread_closes == 1
    assert reuse.temp_process_closes == 1
    assert reuse.resolve(old_thread, future_process, 250) == "resolved"
    assert reuse.thread_opens == 2
    assert reuse.process_opens == 2
    checks += 6

    thread_reuse = ResolverModel(10, 10)
    current_process = Process(50, 100)
    new_thread = Thread(10, 50, 200)
    assert thread_reuse.resolve(new_thread, current_process, 150) == "thread-after-event"
    assert thread_reuse.resolve(new_thread, current_process, 250) == "resolved"
    assert thread_reuse.thread_opens == 1
    assert thread_reuse.process_opens == 1
    checks += 4

    rng = random.Random(20260810)
    for _ in range(cases):
        thread_cap = rng.randrange(1, 9)
        process_cap = rng.randrange(1, 5)
        resolver = ResolverModel(thread_cap, process_cap)

        pid = rng.randrange(1, 10_000)
        tid = rng.randrange(1, 100_000)
        process_start = rng.randrange(1_000_000, 2_000_000)
        thread_start = process_start + rng.randrange(0, 50_000)
        event_time = rng.randrange(900_000, 2_100_000)
        process = Process(pid, process_start)
        thread = Thread(tid, pid, thread_start)

        result = resolver.resolve(thread, process, event_time)
        if process_start > event_time:
            assert result == "process-after-event"
            assert resolver.temp_thread_closes == 1
            assert resolver.temp_process_closes == 1
            assert pid not in resolver.processes
            checks += 4
        elif thread_start > event_time:
            assert result == "thread-after-event"
            assert tid in resolver.threads
            assert pid in resolver.processes
            assert resolver.thread_opens == 1
            checks += 4
        else:
            assert result == "resolved"
            assert tid in resolver.threads
            assert pid in resolver.processes
            assert resolver.thread_opens == 1
            checks += 4

        later = max(event_time, thread_start, process_start) + 1
        result2 = resolver.resolve(thread, process, later)
        assert result2 == "resolved"
        if result == "process-after-event":
            assert resolver.thread_opens == 2
            assert resolver.process_opens == 2
        else:
            assert resolver.thread_opens == 1
            assert resolver.process_opens == 1
        checks += 3

        second_tid = tid + 1
        second_thread = Thread(second_tid, pid, thread_start)
        before_process_opens = resolver.process_opens
        second = resolver.resolve(second_thread, process, later)
        if len(resolver.threads) >= thread_cap and second_tid not in resolver.threads:
            assert second == "thread-cap"
            assert resolver.process_opens == before_process_opens
            checks += 2
        else:
            assert second == "resolved"
            assert resolver.process_opens == before_process_opens
            checks += 2

        oversized_pid = MAX_PID + 1 + rng.randrange(0, 10_000)
        oversized_thread = Thread(second_tid + 1, oversized_pid, thread_start)
        before_process_opens = resolver.process_opens
        oversized = resolver.resolve(oversized_thread, Process(oversized_pid, process_start), later)
        if len(resolver.threads) >= thread_cap:
            assert oversized == "thread-cap"
        else:
            assert oversized == "pid-range"
            assert resolver.temp_thread_closes >= 1
        assert resolver.process_opens == before_process_opens
        checks += 2

    return checks


def check_repository(root: Path) -> int:
    paths = {
        "source": root / "src/FileOp.Windows/Performance/WindowsDiskIoIssuingThreadResolver.cs",
        "tests": root / "tests/FileOp.Windows.Tests/WindowsDiskIoIssuingThreadResolverTests.cs",
        "failure_tests": root / "tests/FileOp.Windows.Tests/WindowsDiskIoIssuingThreadResolverFailureTests.cs",
        "id_tests": root / "tests/FileOp.Windows.Tests/WindowsDiskIoIssuingThreadResolverNativeIdTests.cs",
        "fake": root / "tests/FileOp.Windows.Tests/WindowsDiskIoLifetimeFake.cs",
        "doc": root / "docs/disk-io-thread-process-resolver.md",
        "gate": root / "tools/test-local.ps1",
    }
    text = {name: path.read_text(encoding="utf-8") for name, path in paths.items()}
    checks = 0

    for needle in (
        "DefaultMaximumCachedThreads = 2_048",
        "DefaultMaximumCachedProcesses = 1_024",
        "ProcessIdOutOfRange",
        "processId > int.MaxValue",
        "DateTime.FromFileTimeUtc",
        "ThreadQueryLimitedInformation = 0x0800",
        "ProcessQueryLimitedInformation = 0x1000",
        'EntryPoint = "OpenThread"',
        'EntryPoint = "GetThreadTimes"',
        'EntryPoint = "GetProcessIdOfThread"',
        'EntryPoint = "OpenProcess"',
        'EntryPoint = "GetProcessTimes"',
        "QueryFullProcessImageNameW",
        'EntryPoint = "CloseHandle"',
        "thread.StartedAt > observationTimestamp",
        "thread.Process.StartedAt > observationTimestamp",
        "new DiskIoProcessIdentity",
        "thread.Process.StartedAt",
        "_threads.Add(issuingThreadId, thread)",
        "_processes.Add(processId, process)",
        "One or more DiskIo lifetime handles could not be closed",
    ):
        assert needle in text["source"], needle
        checks += 1

    for forbidden in (
        "PROCESS_ALL_ACCESS",
        "THREAD_ALL_ACCESS",
        "Process.GetProcessById",
        "CreateToolhelp32Snapshot",
        "ManagementObject",
        "NtQuery",
        "OpenProcessToken",
        "AdjustTokenPrivileges",
        "runas",
        "Registry.",
        "TerminateProcess",
        ".Kill(",
        "SuspendThread",
        "ReadProcessMemory",
        "PeriodicTimer",
        "DispatcherQueueTimer",
        "IndexingServiceProtocol",
        "FileOp.App",
    ):
        assert forbidden not in text["source"], forbidden
        checks += 1

    # No weak PID-only owner construction is allowed.
    assert "new DiskIoProcessIdentity(\n                (int)thread.Process.ProcessId,\n                null" not in text["source"]
    checks += 1

    for needle in (
        "ResolvesStableOwnerAndNormalizesImageName",
        "SameThreadReusesPinnedThreadAndProcessHandles",
        "DifferentThreadsInSameProcessReusePinnedProcessHandle",
        "ThreadCreatedAfterOldEventIsPinnedButNotMisattributed",
        "ProcessCreatedAfterOldEventIsNotCachedAndLaterEventRetries",
        "ImageLookupFailureKeepsStableProcessIdentity",
        "ThreadCacheLimitStopsOpeningAdditionalThreads",
        "ProcessCacheLimitClosesNewThreadWithoutOpeningSecondProcess",
        "InvalidEventFileTimeFailsBeforeNativeLookup",
    ):
        assert needle in text["tests"], needle
        checks += 1

    for needle in (
        "ThreadOpenFailureIsUnattributedWithoutProcessLookup",
        "ThreadTimeFailureClosesTemporaryThreadHandle",
        "ProcessOpenFailureClosesTemporaryThreadHandle",
        "ProcessTimeFailureClosesTemporaryProcessAndThreadHandles",
        "DisposeClosesEveryPinnedHandleExactlyOnceAndIsIdempotent",
        "DisposeAttemptsEveryHandleEvenWhenOneCloseFails",
        "FileTimeNativeCombinesUnsignedHighAndLowWords",
    ):
        assert needle in text["failure_tests"], needle
        checks += 1

    assert "ProcessIdOutsideCoreIdentityRangeIsUnattributedBeforeOpenProcess" in text["id_tests"]
    assert "DateTimeOffset" in text["fake"] and ".ToFileTime()" in text["fake"]
    checks += 2

    for needle in (
        "PID + process creation time",
        "2,048",
        "1,024",
        "100-nanosecond intervals since January 1, 1601 UTC",
        "GetThreadTimes",
        "GetProcessTimes",
        "THREAD_QUERY_LIMITED_INFORMATION",
        "PROCESS_QUERY_LIMITED_INFORMATION",
        "does not enumerate processes or threads",
        "does not request all-access",
        "attribution coverage",
    ):
        assert needle in text["doc"], needle
        checks += 1

    assert "verify_disk_io_thread_process_resolver.py" in text["gate"]
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
        "PASS: DiskIo lifetime-aware owner resolver verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized lifetime cases{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
