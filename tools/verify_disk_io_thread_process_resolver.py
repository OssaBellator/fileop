#!/usr/bin/env python3
"""Verify FileOp's reuse-aware DiskIo thread/process resolver without GitHub Actions."""
from __future__ import annotations

import argparse
import random
from dataclasses import dataclass
from pathlib import Path

MAX_PID = 0x7FFFFFFF
DEFAULT_THREADS = 2048
DEFAULT_PROCESSES = 1024
ACTIVE = "active"
TERMINATED = "terminated"
UNKNOWN = "unknown"


@dataclass(frozen=True)
class Process:
    pid: int
    started: int
    generation: int


@dataclass(frozen=True)
class Thread:
    tid: int
    pid: int
    started: int
    generation: int


@dataclass
class CachedProcess:
    process: Process
    state: str = ACTIVE


@dataclass
class CachedThread:
    thread: Thread
    process: CachedProcess
    state: str = ACTIVE


class ResolverModel:
    def __init__(self, thread_cap: int = DEFAULT_THREADS, process_cap: int = DEFAULT_PROCESSES) -> None:
        if thread_cap <= 0 or process_cap <= 0:
            raise ValueError("caps")
        self.thread_cap = thread_cap
        self.process_cap = process_cap
        self.threads: dict[int, CachedThread] = {}
        self.processes: dict[int, CachedProcess] = {}
        self.thread_opens = 0
        self.process_opens = 0
        self.thread_closes = 0
        self.process_closes = 0

    def mark_thread(self, tid: int, state: str) -> None:
        self.threads[tid].state = state

    def mark_process(self, pid: int, state: str) -> None:
        self.processes[pid].state = state

    def resolve(self, thread: Thread, process: Process, event_time: int) -> str:
        if thread.tid == 0:
            return "invalid-thread"

        cached_thread = self.threads.get(thread.tid)
        if cached_thread is not None:
            if cached_thread.state == UNKNOWN:
                return "thread-state-unavailable"
            if cached_thread.state == ACTIVE:
                return self._validate(cached_thread, event_time)
            if cached_thread.state != TERMINATED:
                raise AssertionError(cached_thread.state)
            self.thread_closes += 1
            del self.threads[thread.tid]

        if len(self.threads) >= self.thread_cap:
            return "thread-cap"

        self.thread_opens += 1
        if thread.pid == 0:
            return "process-unavailable"
        if thread.pid > MAX_PID:
            return "pid-range"

        cached_process = self.processes.get(thread.pid)
        if cached_process is not None:
            if cached_process.state == UNKNOWN:
                return "process-state-unavailable"
            if cached_process.state == TERMINATED:
                self.process_closes += 1
                del self.processes[thread.pid]
                cached_process = None
            elif cached_process.state != ACTIVE:
                raise AssertionError(cached_process.state)

        if cached_process is None:
            if len(self.processes) >= self.process_cap:
                return "process-cap"
            self.process_opens += 1
            if process.started > event_time:
                return "process-after-event"
            cached_process = CachedProcess(process)
            self.processes[thread.pid] = cached_process

        current_thread = CachedThread(thread, cached_process)
        self.threads[thread.tid] = current_thread
        return self._validate(current_thread, event_time)

    @staticmethod
    def _validate(thread: CachedThread, event_time: int) -> str:
        if thread.thread.started > event_time:
            return "thread-after-event"
        if thread.process.process.started > event_time:
            return "process-after-event"
        return "resolved"


def run_model(cases: int) -> int:
    checks = 0
    assert DEFAULT_THREADS == 2048
    assert DEFAULT_PROCESSES == 1024
    checks += 2

    # Same numeric TID, distinct object generations.
    model = ResolverModel()
    p1 = Process(50, 100, 1)
    t1 = Thread(100, 50, 110, 1)
    assert model.resolve(t1, p1, 120) == "resolved"
    model.mark_thread(100, TERMINATED)
    p2 = Process(51, 200, 1)
    t2 = Thread(100, 51, 210, 2)
    assert model.resolve(t2, p2, 220) == "resolved"
    assert model.thread_opens == 2 and model.thread_closes == 1
    checks += 3

    # Delayed old event cannot be assigned to the newly reused object.
    model2 = ResolverModel()
    assert model2.resolve(t1, p1, 120) == "resolved"
    model2.mark_thread(100, TERMINATED)
    p3 = Process(51, 300, 1)
    t3 = Thread(100, 51, 310, 2)
    assert model2.resolve(t3, p3, 250) == "process-after-event"
    checks += 2

    # Indeterminate cached state is fail-closed.
    model3 = ResolverModel()
    assert model3.resolve(t1, p1, 120) == "resolved"
    model3.mark_thread(100, UNKNOWN)
    assert model3.resolve(t1, p1, 130) == "thread-state-unavailable"
    checks += 2

    rng = random.Random(20260811)
    for _ in range(cases):
        model = ResolverModel(thread_cap=rng.randrange(1, 9), process_cap=rng.randrange(1, 5))
        pid = rng.randrange(1, 10000)
        tid = rng.randrange(1, 100000)
        process_start = rng.randrange(1_000_000, 2_000_000)
        thread_start = process_start + rng.randrange(0, 50_000)
        event = max(process_start, thread_start) + 1
        p_old = Process(pid, process_start, 1)
        t_old = Thread(tid, pid, thread_start, 1)
        assert model.resolve(t_old, p_old, event) == "resolved"
        checks += 1

        mode = rng.choice((ACTIVE, TERMINATED, UNKNOWN))
        model.mark_thread(tid, mode)
        if mode == ACTIVE:
            assert model.resolve(t_old, p_old, event + 1) == "resolved"
            assert model.thread_opens == 1
            checks += 2
        elif mode == UNKNOWN:
            assert model.resolve(t_old, p_old, event + 1) == "thread-state-unavailable"
            assert model.thread_opens == 1
            checks += 2
        else:
            new_pid = pid + 1
            p_new = Process(new_pid, event + 10, 1)
            t_new = Thread(tid, new_pid, event + 20, 2)
            assert model.resolve(t_new, p_new, event + 30) == "resolved"
            assert model.thread_opens == 2
            assert model.thread_closes == 1
            checks += 3

        # Exercise PID reuse independently with a new TID.
        pid2 = rng.randrange(20_000, 30_000)
        tid2 = rng.randrange(200_000, 300_000)
        p_first = Process(pid2, 100, 1)
        t_first = Thread(tid2, pid2, 110, 1)
        other = ResolverModel()
        assert other.resolve(t_first, p_first, 120) == "resolved"
        state = rng.choice((ACTIVE, TERMINATED, UNKNOWN))
        other.mark_process(pid2, state)
        t_next = Thread(tid2 + 1, pid2, 210, 1)
        p_next = Process(pid2, 200, 2)
        if state == ACTIVE:
            assert other.resolve(t_next, p_next, 220) == "resolved"
            assert other.process_opens == 1
            checks += 2
        elif state == UNKNOWN:
            assert other.resolve(t_next, p_next, 220) == "process-state-unavailable"
            assert other.process_opens == 1
            checks += 2
        else:
            assert other.resolve(t_next, p_next, 220) == "resolved"
            assert other.process_opens == 2
            assert other.process_closes == 1
            checks += 3

    return checks


def check_repository(root: Path) -> int:
    paths = {
        "source": root / "src/FileOp.Windows/Performance/WindowsDiskIoIssuingThreadResolver.cs",
        "reuse": root / "tests/FileOp.Windows.Tests/WindowsDiskIoIssuingThreadResolverReuseTests.cs",
        "eviction": root / "tests/FileOp.Windows.Tests/WindowsDiskIoIssuingThreadResolverEvictionFailureTests.cs",
        "fake": root / "tests/FileOp.Windows.Tests/WindowsDiskIoLifetimeFake.cs",
        "doc": root / "docs/disk-io-thread-process-resolver.md",
        "gate": root / "tools/test-local.ps1",
    }
    text = {name: path.read_text(encoding="utf-8") for name, path in paths.items()}
    checks = 0

    for needle in (
        "ThreadStateUnavailable",
        "ProcessStateUnavailable",
        "WindowsDiskIoObjectState",
        "GetObjectState(IntPtr handle)",
        "ThreadQueryLimitedInformation | Synchronize",
        "ProcessQueryLimitedInformation | Synchronize",
        "Synchronize = 0x00100000",
        'EntryPoint = "WaitForSingleObject"',
        "WaitForSingleObjectNative(handle, milliseconds: 0)",
        "WaitObject0 => WindowsDiskIoObjectState.Terminated",
        "WaitTimeout => WindowsDiskIoObjectState.Active",
        "_api.CloseHandle(cachedThread.Handle);\n                        _threads.Remove(issuingThreadId);",
        "_api.CloseHandle(cached.Handle);\n                    _processes.Remove(processId);",
    ):
        assert needle in text["source"], needle
        checks += 1

    for forbidden in (
        "PROCESS_ALL_ACCESS",
        "THREAD_ALL_ACCESS",
        "Process.GetProcessById",
        "CreateToolhelp32Snapshot",
        "ManagementObject",
        "OpenProcessToken",
        "AdjustTokenPrivileges",
        "TerminateProcess",
        "SuspendThread",
        "ReadProcessMemory",
        "PeriodicTimer",
        "DispatcherQueueTimer",
    ):
        assert forbidden not in text["source"], forbidden
        checks += 1

    for needle in (
        "TerminatedCachedThreadIsEvictedBeforeSameTidIsResolvedAgain",
        "TerminatedCachedProcessIsEvictedBeforeSamePidIsReused",
        "OldQueuedEventIsNotAssignedToNewThreadThatReusedTid",
        "IndeterminateCachedThreadStateStaysUnattributedWithoutReopen",
        "IndeterminateCachedProcessStateStaysUnattributedAndClosesNewThread",
    ):
        assert needle in text["reuse"], needle
        checks += 1

    for needle in (
        "FailedTerminatedThreadCloseKeepsOldEntryForCleanupRetry",
        "FailedTerminatedProcessCloseKeepsOldEntryForCleanupRetry",
    ):
        assert needle in text["eviction"], needle
        checks += 1

    for needle in (
        "MarkInactive",
        "MarkStateIndeterminate",
        "GetObjectState",
        "_nextThreadHandle",
        "_nextProcessHandle",
    ):
        assert needle in text["fake"], needle
        checks += 1

    for needle in (
        "reuse detectors",
        "WAIT_TIMEOUT",
        "WAIT_OBJECT_0",
        "SYNCHRONIZE",
        "close-before-remove",
        "does not enumerate processes or threads",
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
        "PASS: reuse-aware DiskIo owner resolver verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized cases{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
