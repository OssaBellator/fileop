#!/usr/bin/env python3
"""Verify bounded measured background-process activity without hosted Actions."""
from __future__ import annotations

import argparse
import random
from pathlib import Path
from typing import Dict, List, Optional, Tuple

Process = Tuple[int, int, int, int, int]
Row = Tuple[int, int, int, int, int, int]


def key(process: Process) -> Tuple[int, int]:
    return process[0], process[1]


def analyze_reference(
    start: List[Process],
    end: List[Process],
    max_rows: int,
    capture_pid: int,
):
    start_by: Dict[Tuple[int, int], Process] = {key(process): process for process in start}
    end_by: Dict[Tuple[int, int], Process] = {key(process): process for process in end}
    rows: List[Row] = []
    for process_key, start_process in start_by.items():
        end_process = end_by.get(process_key)
        if end_process is None:
            continue
        if end_process[2] < start_process[2]:
            raise ValueError("processor time regressed")
        rows.append(
            (
                end_process[0],
                end_process[1],
                end_process[2] - start_process[2],
                end_process[3],
                end_process[4],
                1 if end_process[0] == capture_pid else 0,
            )
        )

    ordered = sorted(
        rows,
        key=lambda row: (-row[2], -row[3], -row[4], row[0], row[1]),
    )
    visible = ordered[:max_rows]
    hidden = ordered[max_rows:]
    start_keys = set(start_by)
    end_keys = set(end_by)
    capture = next((row for row in ordered if row[5]), None)
    return {
        "ordered": ordered,
        "visible": visible,
        "hidden": hidden,
        "hidden_cpu": sum(row[2] for row in hidden),
        "total_cpu": sum(row[2] for row in ordered),
        "started": len(end_keys - start_keys),
        "exited": len(start_keys - end_keys),
        "capture": capture,
    }


def expected_order(rows: List[Row]) -> List[Row]:
    remaining = list(rows)
    result: List[Row] = []
    while remaining:
        best = min(
            remaining,
            key=lambda row: (-row[2], -row[3], -row[4], row[0], row[1]),
        )
        result.append(best)
        remaining.remove(best)
    return result


def run_model(cases: int, seed: int) -> int:
    rng = random.Random(seed)
    checks = 0

    stable = [(10, 100, 20, 1000, 900)]
    reused = [(10, 200, 5, 1100, 950)]
    result = analyze_reference(stable, reused, 10, 99)
    assert result["ordered"] == []
    assert result["started"] == 1
    assert result["exited"] == 1
    checks += 3

    for case in range(cases):
        start: List[Process] = []
        end: List[Process] = []
        pid_count = rng.randint(0, 40)
        capture_pid = rng.randint(1, 50)
        used_end_pids = set()

        for pid in range(1, pid_count + 1):
            started = case * 100_000 + rng.randint(1, 90_000)
            cpu = rng.randint(0, 5_000_000)
            working = rng.randint(0, 5_000_000_000)
            private = rng.randint(0, 5_000_000_000)
            start.append((pid, started, cpu, working, private))

            disposition = rng.random()
            if disposition < 0.68:
                cpu_end = cpu + rng.randint(0, 2_000_000)
                end.append(
                    (
                        pid,
                        started,
                        cpu_end,
                        rng.randint(0, 5_000_000_000),
                        rng.randint(0, 5_000_000_000),
                    )
                )
                used_end_pids.add(pid)
            elif disposition < 0.83:
                end.append(
                    (
                        pid,
                        started + rng.randint(1, 1000),
                        rng.randint(0, 100_000),
                        rng.randint(0, 5_000_000_000),
                        rng.randint(0, 5_000_000_000),
                    )
                )
                used_end_pids.add(pid)

        for _ in range(rng.randint(0, 8)):
            pid = rng.randint(pid_count + 1, pid_count + 100)
            if pid in used_end_pids:
                continue
            used_end_pids.add(pid)
            end.append(
                (
                    pid,
                    case * 100_000 + rng.randint(90_001, 99_999),
                    rng.randint(0, 100_000),
                    rng.randint(0, 5_000_000_000),
                    rng.randint(0, 5_000_000_000),
                )
            )

        assert len({process[0] for process in start}) == len(start)
        assert len({process[0] for process in end}) == len(end)
        checks += 2

        max_rows = rng.randint(1, 20)
        actual = analyze_reference(start, end, max_rows, capture_pid)
        assert actual["ordered"] == expected_order(actual["ordered"])
        checks += 1

        stable_keys = set(map(key, start)) & set(map(key, end))
        assert len(actual["ordered"]) == len(stable_keys)
        assert actual["started"] == len(set(map(key, end)) - set(map(key, start)))
        assert actual["exited"] == len(set(map(key, start)) - set(map(key, end)))
        checks += 3

        assert len(actual["visible"]) == min(max_rows, len(stable_keys))
        assert len(actual["hidden"]) == max(0, len(stable_keys) - max_rows)
        assert actual["hidden_cpu"] == sum(row[2] for row in actual["hidden"])
        assert actual["total_cpu"] == sum(row[2] for row in actual["visible"]) + actual["hidden_cpu"]
        checks += 4

        capture_rows = [row for row in actual["ordered"] if row[0] == capture_pid]
        if capture_rows:
            assert actual["capture"] == capture_rows[0]
        else:
            assert actual["capture"] is None
        checks += 1

        # Memory-only perturbations cannot alter stable matching or CPU deltas, although
        # they may deterministically break equal-CPU ties.
        rememoried_end = [
            (pid, started, cpu, rng.randint(0, 9_000_000_000), rng.randint(0, 9_000_000_000))
            for pid, started, cpu, _, _ in end
        ]
        rememoried = analyze_reference(start, rememoried_end, max_rows, capture_pid)
        assert {row[:3] for row in rememoried["ordered"]} == {row[:3] for row in actual["ordered"]}
        assert rememoried["started"] == actual["started"]
        assert rememoried["exited"] == actual["exited"]
        checks += 3

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
    core = (root / "src/FileOp.Core/Performance/BackgroundProcessActivity.cs").read_text(encoding="utf-8")
    provider = (root / "src/FileOp.Windows/Performance/WindowsBackgroundProcessActivityProvider.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/BackgroundProcessActivityTests.cs").read_text(encoding="utf-8")
    docs = (root / "docs/background-process-activity.md").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")

    checks = 0
    required = [
        (core, "DefaultSamplingDelay = TimeSpan.FromSeconds(1)", "default one-second sample"),
        (core, "MinimumSamplingDelay = TimeSpan.FromMilliseconds(250)", "minimum bounded delay"),
        (core, "MaximumSamplingDelay = TimeSpan.FromSeconds(3)", "maximum bounded delay"),
        (core, "DefaultMaxProcessSnapshots = 1024", "default process snapshot cap"),
        (core, "MaximumProcessSnapshots = 4096", "hard process snapshot cap"),
        (core, "DefaultMaxRows = 20", "default visible row cap"),
        (core, "StartedUtcTicks", "stable PID/start identity key"),
        (core, "cannot contain PID", "same-frame duplicate PID rejection"),
        (core, "processor time decreased for the same stable process instance", "CPU regression fail closed"),
        (core, ".OrderByDescending(static process => process.ProcessorTimeDelta)", "CPU-delta ordering"),
        (core, ".ThenByDescending(static process => process.WorkingSetBytes)", "working-set tie break"),
        (core, ".ThenByDescending(static process => process.PrivateMemoryBytes)", "private-memory tie break"),
        (core, ".Take(budget.MaxRows)", "visible row budget"),
        (core, ".Skip(budget.MaxRows)", "hidden matched evidence"),
        (core, "OtherMatchedProcessorTime", "hidden CPU preservation"),
        (core, "CaptureProcessProcessorTime", "observer CPU evidence"),
        (core, "CaptureProcessWorkingSetBytes", "observer memory evidence"),
        (core, "EvidenceMayBeIncomplete", "partial evidence flag"),
        (core, "StartedDuringSampleCount", "started stable identity count"),
        (core, "ExitedDuringSampleCount", "exited stable identity count"),
        (provider, "Process.GetProcesses()", "Windows process enumeration"),
        (provider, "candidate.Process.StartTime", "stable process start read"),
        (provider, "candidate.Process.TotalProcessorTime", "CPU counter read"),
        (provider, "candidate.Process.WorkingSet64", "working-set read"),
        (provider, "candidate.Process.PrivateMemorySize64", "private-memory read"),
        (provider, "candidate.Process.Threads.Count", "thread-count read"),
        (provider, "candidates.Sort", "deterministic capped enumeration"),
        (provider, "snapshotCapReached = candidates.Count > maxProcessSnapshots", "snapshot cap evidence"),
        (provider, "Stopwatch.GetTimestamp()", "provider overhead stopwatch start"),
        (provider, "Stopwatch.GetElapsedTime(captureStart)", "provider overhead stopwatch elapsed"),
        (provider, "OperatingSystem.IsWindows()", "Windows support boundary"),
        (provider, "Environment.ProcessId", "observer process identity"),
        (tests, "StableInstancesProduceCpuDeltasAndDeterministicRows", "stable delta regression"),
        (tests, "PidReuseIsStartedAndExitedInsteadOfMatched", "PID reuse regression"),
        (tests, "InaccessibleAndCapEvidenceMakesReportExplicitlyIncomplete", "partial evidence regression"),
        (tests, "ProcessorTimeRegressionForSameStableInstanceFailsClosed", "CPU regression test"),
        (tests, "ProviderUsesExactlyTwoSnapshotsAndOneBoundedDelay", "two-frame provider regression"),
        (tests, "NativeSnapshotSourceIncludesCurrentProcessWhenBudgetAllows", "Windows native source regression"),
        (docs, "current process resource activity", "current measured activity documentation"),
        (docs, "does not match two frames by PID alone", "stable identity documentation"),
        (docs, "CPU percentage is intentionally", "no fake CPU percentage"),
        (docs, "does not inspect startup registry keys", "no registry-presence heuristic"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
        (gate, "verify_background_process_activity.py --repo-root $repoRoot --cases 50000", "offline gate wiring"),
    ]
    for text, needle, label in required:
        checks += require(text, needle, label)

    if provider.count("_snapshotSource.Capture(budget.MaxProcessSnapshots)") != 2:
        raise AssertionError("provider must take exactly two bounded snapshots")
    checks += 1
    if provider.count("_delayAsync(budget.SamplingDelay, cancellationToken)") != 1:
        raise AssertionError("provider must use exactly one bounded sampling delay")
    checks += 1

    combined = core + "\n" + provider + "\n" + docs
    for needle in (
        "Microsoft.Win32",
        "Registry.",
        "RegistryKey",
        "StartupTask",
        "Run\\",
        "FileSystemWatcher",
        "PeriodicTimer",
        "DispatcherQueueTimer",
        "Kill(",
        "CloseMainWindow",
        "PriorityClass =",
        "ProcessorAffinity =",
        "health score",
        "impact score",
    ):
        checks += forbid(combined, needle, "registry/startup-control/poller/process-control/score")

    checks += forbid(core, "ProcessorTimeDelta.TotalSeconds /", "CPU percentage inference")
    checks += forbid(provider, "GetProcessesByName", "name-based process matching")
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0xB6A0)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = " and %s source checks" % format(source_checks, ",") if args.repo_root else ""
    print(
        "PASS: background process activity verified with %s model assertions across %s randomized captures%s."
        % (format(model_checks, ","), format(args.cases, ","), suffix)
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
