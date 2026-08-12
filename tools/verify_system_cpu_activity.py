#!/usr/bin/env python3
"""Verify bounded Windows system CPU interval evidence."""
from __future__ import annotations

import argparse
import random
from pathlib import Path

from verify_system_cpu_activity_ui import (
    check_repository as check_system_cpu_activity_ui_repository,
    run_model as run_system_cpu_activity_ui_model,
)


def analyze(
    start_idle: int,
    start_kernel: int,
    start_user: int,
    end_idle: int,
    end_kernel: int,
    end_user: int,
):
    if start_idle > start_kernel or end_idle > end_kernel:
        raise ValueError("idle exceeds kernel")
    if end_idle < start_idle or end_kernel < start_kernel or end_user < start_user:
        raise ValueError("cumulative counter decreased")
    idle_delta = end_idle - start_idle
    kernel_delta = end_kernel - start_kernel
    user_delta = end_user - start_user
    if idle_delta > kernel_delta:
        raise ValueError("idle delta exceeds kernel delta")
    total = kernel_delta + user_delta
    busy = total - idle_delta
    percent = None if total == 0 else busy * 100.0 / total
    return total, idle_delta, busy, percent


def run_model(cases: int, seed: int) -> int:
    rng = random.Random(seed)
    checks = 0

    assert analyze(100, 300, 200, 150, 400, 260) == (160, 50, 110, 68.75)
    assert analyze(100, 200, 300, 100, 200, 300) == (0, 0, 0, None)
    try:
        analyze(100, 300, 200, 99, 300, 200)
        raise AssertionError("counter regression accepted")
    except ValueError:
        pass
    try:
        analyze(100, 300, 200, 250, 400, 200)
        raise AssertionError("idle delta above kernel accepted")
    except ValueError:
        pass
    try:
        analyze(301, 300, 200, 301, 300, 200)
        raise AssertionError("snapshot idle above kernel accepted")
    except ValueError:
        pass
    assert analyze(10_100, 10_300, 10_200, 10_150, 10_400, 10_260) == (160, 50, 110, 68.75)
    checks += 6

    for _ in range(cases):
        start_idle = rng.randint(0, 2**50)
        start_kernel = start_idle + rng.randint(0, 2**40)
        start_user = rng.randint(0, 2**50)
        kernel_delta = rng.randint(0, 2**32)
        idle_delta = rng.randint(0, kernel_delta)
        user_delta = rng.randint(0, 2**32)

        result = analyze(
            start_idle,
            start_kernel,
            start_user,
            start_idle + idle_delta,
            start_kernel + kernel_delta,
            start_user + user_delta,
        )
        total, idle, busy, percent = result
        assert total == kernel_delta + user_delta
        assert idle == idle_delta
        assert busy == kernel_delta + user_delta - idle_delta
        if total == 0:
            assert percent is None
        else:
            assert percent is not None and abs(percent - busy * 100.0 / total) < 1e-12
        assert 0 <= busy <= total
        checks += 5

        shift = rng.randint(0, 2**40)
        shifted = analyze(
            start_idle + shift,
            start_kernel + shift,
            start_user + shift,
            start_idle + shift + idle_delta,
            start_kernel + shift + kernel_delta,
            start_user + shift + user_delta,
        )
        assert shifted == result
        checks += 1

        try:
            analyze(
                start_idle,
                start_kernel,
                start_user,
                start_idle + kernel_delta + 1,
                start_kernel + kernel_delta,
                start_user + user_delta,
            )
            raise AssertionError("impossible idle delta accepted")
        except ValueError:
            pass
        checks += 1

        try:
            analyze(
                start_idle,
                start_kernel,
                start_user,
                start_idle,
                start_kernel,
                start_user - 1 if start_user > 0 else -1,
            )
            raise AssertionError("user counter regression accepted")
        except ValueError:
            pass
        checks += 1

    return checks


def run_all_models(cases: int, seed: int) -> tuple[int, int]:
    return (
        run_model(cases, seed),
        run_system_cpu_activity_ui_model(cases, seed ^ 0x55AA55),
    )


def require(text: str, needle: str, label: str) -> int:
    if needle not in text:
        raise AssertionError("missing %s: %s" % (label, needle))
    return 1


def forbid(text: str, needle: str, label: str) -> int:
    if needle in text:
        raise AssertionError("forbidden %s: %s" % (label, needle))
    return 1


def check_repository(root: Path) -> int:
    core = (root / "src/FileOp.Core/Performance/SystemCpuActivity.cs").read_text(encoding="utf-8")
    provider = (root / "src/FileOp.Windows/Performance/WindowsSystemCpuActivityProvider.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/SystemCpuActivityTests.cs").read_text(encoding="utf-8")
    docs = (root / "docs/system-cpu-activity.md").read_text(encoding="utf-8")
    parent = (root / "tools/verify_machine_process_activity.py").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")

    checks = 0
    required = [
        (core, "DefaultSamplingDelay = TimeSpan.FromSeconds(1)", "default bounded delay"),
        (core, "MinimumSamplingDelay = TimeSpan.FromMilliseconds(250)", "minimum delay"),
        (core, "MaximumSamplingDelay = TimeSpan.FromSeconds(3)", "maximum delay"),
        (core, "idleTime100Nanoseconds > kernelTime100Nanoseconds", "snapshot idle/kernel invariant"),
        (core, "BusyProcessorTimeDelta => TotalProcessorTimeDelta - IdleProcessorTimeDelta", "busy delta formula"),
        (core, "BusyProcessorTimeDelta.Ticks * 100d / TotalProcessorTimeDelta.Ticks", "busy percentage formula"),
        (core, "TotalProcessorTimeDelta.Ticks == 0", "zero-total percentage suppression"),
        (core, "end.IdleTime100Nanoseconds < start.IdleTime100Nanoseconds", "cumulative regression guard"),
        (core, "idleDelta > kernelDelta", "interval idle/kernel guard"),
        (core, "var totalDelta = (UInt128)kernelDelta + userDelta", "overflow-safe total arithmetic"),
        (provider, "private static extern bool GetSystemTimes", "native GetSystemTimes query"),
        (provider, 'DllImport("kernel32.dll", SetLastError = true)', "Win32 error preservation"),
        (provider, "((ulong)value.HighDateTime << 32) | value.LowDateTime", "FILETIME unsigned conversion"),
        (provider, "private readonly SemaphoreSlim _captureGate", "capture serialization"),
        (provider, "var startQuery = _source.Query();", "first native observation"),
        (provider, "var endQuery = _source.Query();", "second native observation"),
        (provider, "await _delayAsync(budget.SamplingDelay, cancellationToken)", "single bounded delay"),
        (provider, "elapsed - budget.SamplingDelay", "provider overhead measurement"),
        (provider, "kernel time includes idle time", "provider interpretation wording"),
        (provider, "more than 64 logical processors", "processor-group runtime caveat"),
        (tests, "AnalyzerSubtractsIdleFromKernelPlusUser", "formula regression"),
        (tests, "ZeroTotalIntervalDoesNotInventBusyPercentage", "zero-total regression"),
        (tests, "ProviderUsesExactlyTwoQueriesAndOneDelayOnWindows", "provider lifecycle regression"),
        (tests, "FirstQueryFailureDoesNotStartDelayOnWindows", "first-failure regression"),
        (tests, "SecondQueryFailurePreservesOneDelayOnWindows", "second-failure regression"),
        (tests, "ProviderFailsClosedOnInconsistentIntervalOnWindows", "malformed interval regression"),
        (docs, "kernel time includes idle time", "documented Windows counter semantics"),
        (docs, "primary processor group", "documented >64 processor scope"),
        (docs, "separate evidence stream", "process/system provenance separation"),
        (docs, "does not universally label this evidence `whole-machine CPU`", "scope wording boundary"),
        (parent, "run_all_models as run_system_cpu_activity_models", "machine parent imports combined CPU models"),
        (parent, "check_all_repository as check_system_cpu_activity_repository", "machine parent imports combined CPU source checks"),
        (parent, "run_system_cpu_activity_models(", "machine parent runs combined CPU models"),
        (parent, "check_system_cpu_activity_repository(root)", "machine parent runs combined CPU checks"),
        (gate, "verify_background_process_activity.py --repo-root $repoRoot --cases 50000", "existing direct machine gate"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
    ]
    for text, needle, label in required:
        checks += require(text, needle, label)

    if provider.count("_source.Query()") != 2:
        raise AssertionError("system CPU provider must issue exactly two native-source observations")
    checks += 1
    if provider.count("_delayAsync(budget.SamplingDelay, cancellationToken)") != 1:
        raise AssertionError("system CPU provider must await exactly one bounded delay")
    checks += 1

    executable = core + "\n" + provider
    for needle in (
        "PerformanceCounter",
        "System.Management",
        "ManagementObject",
        "EventLog",
        "Process.GetProcesses",
        "Process.GetProcessById",
        "PriorityClass =",
        "ProcessorAffinity =",
        "Kill(",
        "CloseMainWindow",
        "CpuHealthScore",
        "CpuPressureScore",
        "HealthScore",
        "PeriodicTimer",
        "DispatcherQueueTimer",
        "FileSystemWatcher",
    ):
        checks += forbid(executable, needle, "alternate sampler/control/score/poller")

    for needle in (
        "Math.Clamp",
        "0.8",
        "80d",
        "90d",
        "95d",
    ):
        checks += forbid(core + "\n" + provider, needle, "CPU threshold/clamping")

    return checks


def check_all_repository(root: Path) -> int:
    return (
        check_repository(root) +
        check_system_cpu_activity_ui_repository(root)
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0xC0FFEE)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks, ui_model_checks = run_all_models(args.cases, args.seed)
    source_checks = check_all_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = " and %s source/UI checks" % format(source_checks, ",") if args.repo_root else ""
    print(
        "PASS: system CPU activity verified with %s provider-model assertions and %s UI-model assertions across %s randomized intervals/states%s."
        % (
            format(model_checks, ","),
            format(ui_model_checks, ","),
            format(args.cases, ","),
            suffix,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
