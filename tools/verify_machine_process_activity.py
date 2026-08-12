#!/usr/bin/env python3
"""Verify bounded machine process activity without hosted Actions."""
from __future__ import annotations

import argparse
import random
from pathlib import Path
from typing import Dict, List, Tuple

from verify_machine_process_activity_ui import (
    check_repository as check_machine_process_activity_ui_repository,
    run_model as run_machine_process_activity_ui_model,
)
from verify_startup_application_degradation import (
    check_all_repository as check_startup_degradation_repository,
    run_all_models as run_startup_degradation_models,
)
from verify_system_cpu_activity import (
    check_all_repository as check_system_cpu_activity_repository,
    run_all_models as run_system_cpu_activity_models,
)

Process = Tuple[int, int, int, int, int]
Row = Tuple[int, int, int, int, int, int]


def key(process: Process) -> Tuple[int, int]:
    return process[0], process[1]


def analyze(start: List[Process], end: List[Process], max_rows: int, capture_pid: int):
    start_by: Dict[Tuple[int, int], Process] = {key(process): process for process in start}
    end_by: Dict[Tuple[int, int], Process] = {key(process): process for process in end}
    rows: List[Row] = []
    for stable_key, before in start_by.items():
        after = end_by.get(stable_key)
        if after is None:
            continue
        if after[2] < before[2]:
            raise ValueError("processor time regressed")
        rows.append(
            (after[0], after[1], after[2] - before[2], after[3], after[4], int(after[0] == capture_pid))
        )
    ordered = sorted(rows, key=lambda row: (-row[2], -row[3], -row[4], row[0], row[1]))
    visible = ordered[:max_rows]
    hidden = ordered[max_rows:]
    start_keys, end_keys = set(start_by), set(end_by)
    return {
        "ordered": ordered,
        "visible": visible,
        "hidden": hidden,
        "hidden_cpu": sum(row[2] for row in hidden),
        "total_cpu": sum(row[2] for row in ordered),
        "appeared": len(end_keys - start_keys),
        "disappeared": len(start_keys - end_keys),
        "capture": next((row for row in ordered if row[5]), None),
    }


def run_model(cases: int, seed: int) -> int:
    rng = random.Random(seed)
    checks = 0
    result = analyze([(10, 100, 20, 1000, 900)], [(10, 200, 5, 1100, 950)], 10, 99)
    assert result["ordered"] == []
    assert result["appeared"] == 1
    assert result["disappeared"] == 1
    checks += 3

    for case in range(cases):
        start: List[Process] = []
        end: List[Process] = []
        pid_count = rng.randint(0, 40)
        capture_pid = rng.randint(1, 50)
        end_pids = set()
        for pid in range(1, pid_count + 1):
            started = case * 100_000 + rng.randint(1, 90_000)
            cpu = rng.randint(0, 5_000_000)
            start.append((pid, started, cpu, rng.randint(0, 5_000_000_000), rng.randint(0, 5_000_000_000)))
            disposition = rng.random()
            if disposition < 0.68:
                end.append((pid, started, cpu + rng.randint(0, 2_000_000), rng.randint(0, 5_000_000_000), rng.randint(0, 5_000_000_000)))
                end_pids.add(pid)
            elif disposition < 0.83:
                end.append((pid, started + rng.randint(1, 1000), rng.randint(0, 100_000), rng.randint(0, 5_000_000_000), rng.randint(0, 5_000_000_000)))
                end_pids.add(pid)
        for _ in range(rng.randint(0, 8)):
            pid = rng.randint(pid_count + 1, pid_count + 100)
            if pid in end_pids:
                continue
            end_pids.add(pid)
            end.append((pid, case * 100_000 + rng.randint(90_001, 99_999), rng.randint(0, 100_000), rng.randint(0, 5_000_000_000), rng.randint(0, 5_000_000_000)))

        assert len({p[0] for p in start}) == len(start)
        assert len({p[0] for p in end}) == len(end)
        checks += 2
        max_rows = rng.randint(1, 20)
        actual = analyze(start, end, max_rows, capture_pid)
        stable = set(map(key, start)) & set(map(key, end))
        assert len(actual["ordered"]) == len(stable)
        assert actual["appeared"] == len(set(map(key, end)) - set(map(key, start)))
        assert actual["disappeared"] == len(set(map(key, start)) - set(map(key, end)))
        checks += 3
        assert len(actual["visible"]) == min(max_rows, len(stable))
        assert len(actual["hidden"]) == max(0, len(stable) - max_rows)
        assert actual["hidden_cpu"] == sum(row[2] for row in actual["hidden"])
        assert actual["total_cpu"] == sum(row[2] for row in actual["visible"]) + actual["hidden_cpu"]
        checks += 4
        assert actual["ordered"] == sorted(actual["ordered"], key=lambda row: (-row[2], -row[3], -row[4], row[0], row[1]))
        checks += 1
        capture_rows = [row for row in actual["ordered"] if row[0] == capture_pid]
        assert actual["capture"] == (capture_rows[0] if capture_rows else None)
        checks += 1
        rememoried = [(pid, started, cpu, rng.randint(0, 9_000_000_000), rng.randint(0, 9_000_000_000)) for pid, started, cpu, _, _ in end]
        changed = analyze(start, rememoried, max_rows, capture_pid)
        assert {row[:3] for row in changed["ordered"]} == {row[:3] for row in actual["ordered"]}
        assert changed["appeared"] == actual["appeared"]
        assert changed["disappeared"] == actual["disappeared"]
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
    core = (root / "src/FileOp.Core/Performance/MachineProcessActivity.cs").read_text(encoding="utf-8")
    provider = (root / "src/FileOp.Windows/Performance/WindowsMachineProcessActivityProvider.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/MachineProcessActivityTests.cs").read_text(encoding="utf-8")
    docs = (root / "docs/machine-process-activity.md").read_text(encoding="utf-8")
    wrapper = (root / "tools/verify_background_process_activity.py").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    checks = 0
    required = [
        (core, "public sealed record MachineProcessActivityBudget", "machine-wide contract naming"),
        (core, "DefaultSamplingDelay = TimeSpan.FromSeconds(1)", "default one-second sample"),
        (core, "MinimumSamplingDelay = TimeSpan.FromMilliseconds(250)", "minimum bounded delay"),
        (core, "MaximumSamplingDelay = TimeSpan.FromSeconds(3)", "maximum bounded delay"),
        (core, "MaximumProcessSnapshots = 4096", "hard snapshot cap"),
        (core, "cannot contain PID", "same-frame duplicate PID rejection"),
        (core, "processor time decreased for the same stable process instance", "CPU regression fail closed"),
        (core, ".OrderByDescending(static process => process.ProcessorTimeDelta)", "CPU-delta ordering"),
        (core, ".ThenByDescending(static process => process.WorkingSetBytes)", "memory tie break"),
        (core, ".Take(budget.MaxRows)", "visible row budget"),
        (core, ".Skip(budget.MaxRows)", "hidden evidence"),
        (core, "OtherMatchedProcessorTime", "hidden CPU preservation"),
        (core, "CaptureProcessProcessorTime", "observer CPU evidence"),
        (core, "EvidenceMayBeIncomplete", "partial evidence flag"),
        (provider, "Process.GetProcesses()", "machine process enumeration"),
        (provider, "candidate.Process.StartTime", "stable process start read"),
        (provider, "candidate.Process.TotalProcessorTime", "CPU counter read"),
        (provider, "candidate.Process.WorkingSet64", "working-set read"),
        (provider, "candidate.Process.PrivateMemorySize64", "private-memory read"),
        (provider, "candidate.Process.Threads.Count", "thread-count read"),
        (provider, "candidates.Sort", "deterministic capped enumeration"),
        (provider, "Stopwatch.GetElapsedTime(captureStart)", "provider overhead evidence"),
        (provider, "Environment.ProcessId", "observer identity"),
        (tests, "PidReuseIsStartedAndExitedInsteadOfMatched", "PID reuse regression"),
        (tests, "ProviderUsesExactlyTwoSnapshotsAndOneBoundedDelay", "bounded provider regression"),
        (tests, "ContractsRejectImpossibleFrames", "frame invariant regression"),
        (docs, "does **not** classify a process as foreground or background", "no foreground/background classification"),
        (docs, "does not inspect startup registry keys", "no registration heuristic"),
        (docs, "CPU percentage is intentionally", "no fake CPU percent"),
        (docs, "- create an impact/health score;", "no synthetic process score"),
        (wrapper, "from verify_machine_process_activity import main", "stable gate delegation"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
        (gate, "verify_background_process_activity.py --repo-root $repoRoot --cases 50000", "stable offline gate entry"),
    ]
    for text, needle, label in required:
        checks += require(text, needle, label)
    if provider.count("_snapshotSource.Capture(budget.MaxProcessSnapshots)") != 2:
        raise AssertionError("provider must take exactly two snapshots")
    checks += 1
    if provider.count("_delayAsync(budget.SamplingDelay, cancellationToken)") != 1:
        raise AssertionError("provider must use exactly one sampling delay")
    checks += 1
    combined = core + "\n" + provider
    for needle in (
        "BackgroundProcessActivity",
        "Microsoft.Win32",
        "Registry.",
        "RegistryKey",
        "StartupTask",
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
        checks += forbid(combined, needle, "misleading naming/registration/control/poller/score")
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
    ui_model_checks = run_machine_process_activity_ui_model(args.cases, args.seed ^ 0x4D504155)
    startup_model_checks, startup_ui_model_checks = run_startup_degradation_models(
        args.cases,
        args.seed ^ 0x510A710,
    )
    system_cpu_model_checks, system_cpu_ui_model_checks = run_system_cpu_activity_models(
        args.cases,
        args.seed ^ 0xC0FFEE,
    )
    if args.repo_root:
        root = args.repo_root.resolve()
        source_checks = (
            check_repository(root) +
            check_machine_process_activity_ui_repository(root) +
            check_startup_degradation_repository(root) +
            check_system_cpu_activity_repository(root)
        )
    else:
        source_checks = 0
    suffix = " and %s source/UI checks" % format(source_checks, ",") if args.repo_root else ""
    print(
        "PASS: machine/startup/CPU activity verified with %s machine-provider assertions, %s machine-UI assertions, %s startup-provider assertions, %s startup-UI assertions, %s system-CPU assertions, and %s system-CPU-UI assertions across %s randomized captures/histories/intervals%s."
        % (
            format(model_checks, ","),
            format(ui_model_checks, ","),
            format(startup_model_checks, ","),
            format(startup_ui_model_checks, ","),
            format(system_cpu_model_checks, ","),
            format(system_cpu_ui_model_checks, ","),
            format(args.cases, ","),
            suffix,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
