#!/usr/bin/env python3
"""Verify system CPU interval presentation beside bounded process activity."""
from __future__ import annotations

import argparse
import random
import xml.etree.ElementTree as ET
from pathlib import Path

ProcessRow = tuple[int, int]


def cpu_present(total: int, idle: int, overhead: int, start: int, end: int):
    if total < 0 or idle < 0 or idle > total or overhead < 0 or end < start:
        raise ValueError("invalid CPU presentation evidence")
    busy = total - idle
    percent = None if total == 0 else busy * 100.0 / total
    return busy, total, idle, percent, overhead, start, end


def present(
    process_rows: list[ProcessRow] | None,
    cpu_available: bool,
    total: int,
    idle: int,
    overhead: int,
    start: int,
    end: int,
):
    shown_rows = None if process_rows is None else list(process_rows)
    cpu = cpu_present(total, idle, overhead, start, end) if cpu_available else None
    return shown_rows, cpu


def run_model(cases: int, seed: int) -> int:
    rng = random.Random(seed)
    checks = 0

    rows = [(1, 100), (2, 50)]
    shown, cpu = present(rows, True, 200, 50, 3, 10, 20)
    assert shown == rows
    assert cpu == (150, 200, 50, 75.0, 3, 10, 20)
    shown, cpu = present(rows, False, 0, 0, 0, 0, 0)
    assert shown == rows and cpu is None
    shown, cpu = present(None, True, 0, 0, 2, 5, 6)
    assert shown is None and cpu == (0, 0, 0, None, 2, 5, 6)
    assert cpu_present(0, 0, 0, 0, 0)[3] is None
    assert cpu_present(100, 100, 0, 0, 1)[3] == 0.0
    checks += 6

    for _ in range(cases):
        count = rng.randint(0, 20)
        process_rows = [
            (index, rng.randint(0, 5_000_000))
            for index in range(count)
        ]
        process_rows.sort(key=lambda row: (-row[1], row[0]))
        process_available = bool(rng.getrandbits(1))
        cpu_available = bool(rng.getrandbits(1))
        total = rng.randint(0, 2**40)
        idle = rng.randint(0, total)
        overhead = rng.randint(0, 1_000_000)
        start = rng.randint(0, 10**12)
        end = start + rng.randint(0, 10_000_000)

        shown, cpu = present(
            process_rows if process_available else None,
            cpu_available,
            total,
            idle,
            overhead,
            start,
            end,
        )
        assert shown == (process_rows if process_available else None)
        checks += 1

        if cpu_available:
            assert cpu is not None
            assert cpu[0] == total - idle
            assert cpu[1] == total and cpu[2] == idle
            assert cpu[3] == (None if total == 0 else (total - idle) * 100.0 / total)
            assert cpu[4] == overhead
            assert cpu[5:] == (start, end)
        else:
            assert cpu is None
        checks += 5

        remapped_total = rng.randint(0, 2**40)
        remapped_idle = rng.randint(0, remapped_total)
        remapped_shown, remapped_cpu = present(
            process_rows if process_available else None,
            True,
            remapped_total,
            remapped_idle,
            overhead,
            start,
            end,
        )
        assert remapped_shown == shown
        assert remapped_cpu is not None
        checks += 2

        remapped_rows = [
            (pid, rng.randint(0, 9_000_000))
            for pid, _ in process_rows
        ]
        remapped_rows.sort(key=lambda row: (-row[1], row[0]))
        _, stable_cpu = present(
            remapped_rows if process_available else None,
            cpu_available,
            total,
            idle,
            overhead,
            start,
            end,
        )
        assert stable_cpu == cpu
        checks += 1

        unavailable_rows, unavailable_cpu = present(
            process_rows if process_available else None,
            False,
            0,
            0,
            0,
            0,
            0,
        )
        assert unavailable_rows == shown
        assert unavailable_cpu is None
        checks += 1

    return checks


def method_body(source: str, signature_fragment: str) -> str:
    start = source.index(signature_fragment)
    brace = source.index("{", start)
    depth = 0
    for index in range(brace, len(source)):
        if source[index] == "{":
            depth += 1
        elif source[index] == "}":
            depth -= 1
            if depth == 0:
                return source[brace : index + 1]
    raise AssertionError(signature_fragment)


def require(text: str, needle: str, label: str) -> int:
    if needle not in text:
        raise AssertionError("missing %s: %s" % (label, needle))
    return 1


def forbid(text: str, needle: str, label: str) -> int:
    if needle in text:
        raise AssertionError("forbidden %s: %s" % (label, needle))
    return 1


def check_repository(root: Path) -> int:
    engine = (root / "src/FileOp.App/DesktopSearchEngine.MachineProcessActivity.cs").read_text(encoding="utf-8")
    main = (root / "src/FileOp.App/MainWindow.MachineProcessActivity.cs").read_text(encoding="utf-8")
    storage = (root / "src/FileOp.App/StorageOptimizationView.MachineProcessActivity.cs").read_text(encoding="utf-8")
    performance = (root / "src/FileOp.App/PerformanceDiagnosticsView.MachineProcessActivity.cs").read_text(encoding="utf-8")
    view_xaml_path = root / "src/FileOp.App/MachineProcessActivityView.xaml"
    view_xaml = view_xaml_path.read_text(encoding="utf-8")
    view = (root / "src/FileOp.App/MachineProcessActivityView.xaml.cs").read_text(encoding="utf-8")
    docs = (root / "docs/system-cpu-activity-ui.md").read_text(encoding="utf-8")
    parent = (root / "tools/verify_system_cpu_activity.py").read_text(encoding="utf-8")
    machine_parent = (root / "tools/verify_machine_process_activity.py").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")

    ET.parse(view_xaml_path)
    checks = 1

    required = [
        (engine, "new WindowsMachineProcessActivityProvider()", "process provider composition"),
        (engine, "new WindowsSystemCpuActivityProvider()", "system CPU provider composition"),
        (engine, "var processTask = _machineProcessActivityProvider.CaptureAsync", "process capture task starts"),
        (engine, "var systemCpuTask = CaptureSystemCpuActivitySafelyAsync", "CPU capture task starts"),
        (engine, "Task.WhenAll(processTask, systemCpuTask)", "parallel bounded wait"),
        (engine, "MachineProcessActivityCaptureBundle", "combined result bundle"),
        (engine, "Supplementary Windows system CPU interval failed", "CPU fail-isolation"),
        (main, "capture.ProcessActivity", "MainWindow process result"),
        (main, "capture.SystemCpuActivity", "MainWindow CPU result"),
        (main, "_performanceDiskIoCaptureActive = true;", "existing explicit capture exclusion"),
        (main, "_performanceDiskIoCaptureActive = false;", "existing exclusion release"),
        (storage, "SystemCpuActivityResult systemCpuActivity", "Storage CPU forwarding"),
        (performance, "SystemCpuActivityResult systemCpuActivity", "Performance CPU forwarding"),
        (view_xaml, 'Text="Windows system CPU interval"', "CPU interval heading"),
        (view_xaml, 'x:Name="SystemCpuBusyText"', "busy share metric"),
        (view_xaml, 'x:Name="SystemCpuTimeText"', "busy/total metric"),
        (view_xaml, 'x:Name="SystemCpuWallText"', "wall interval metric"),
        (view_xaml, 'x:Name="SystemCpuOverheadText"', "CPU overhead metric"),
        (view_xaml, 'Content="Capture machine activity"', "single existing action"),
        (view_xaml, "FileOp does not derive per-process CPU percentages", "process percentage boundary"),
        (view, "ApplySystemCpu(systemCpuActivity)", "CPU result applied independently"),
        (view, "evidence.BusyPercent", "Core busy share rendering"),
        (view, "evidence.BusyProcessorTimeDelta", "busy time rendering"),
        (view, "evidence.TotalProcessorTimeDelta", "total time rendering"),
        (view, "evidence.IdleProcessorTimeDelta", "idle time rendering"),
        (view, "evidence.ObservationWallDuration", "wall interval rendering"),
        (view, "result.ProviderOverheadDuration", "CPU overhead rendering"),
        (view, "evidence.StartObservedAt.ToLocalTime()", "start timestamp rendering"),
        (view, "evidence.EndObservedAt.ToLocalTime()", "end timestamp rendering"),
        (view, "does not treat visible process deltas as a decomposition of Windows busy time", "separate provenance wording"),
        (docs, "starts both captures before awaiting either result", "documented concurrency"),
        (docs, "not identical measurement boundaries", "documented interval separation"),
        (docs, "does not divide process CPU-time deltas by the system CPU interval", "no process percentage derivation"),
        (docs, "primary-processor-group caveat", "processor group presentation boundary"),
        (parent, "from verify_system_cpu_activity_ui import (", "CPU parent imports UI child"),
        (parent, "def run_all_models", "CPU parent exposes combined models"),
        (parent, "run_system_cpu_activity_ui_model(cases", "combined CPU path runs UI model"),
        (parent, "def check_all_repository", "CPU parent exposes combined source checks"),
        (parent, "check_system_cpu_activity_ui_repository(root)", "combined CPU path runs UI source checks"),
        (machine_parent, "run_all_models as run_system_cpu_activity_models", "machine parent imports combined CPU models"),
        (machine_parent, "check_all_repository as check_system_cpu_activity_repository", "machine parent imports combined CPU checks"),
        (machine_parent, "run_system_cpu_activity_models(", "machine parent runs combined CPU models"),
        (machine_parent, "check_system_cpu_activity_repository(root)", "machine parent runs combined CPU source checks"),
        (gate, "verify_background_process_activity.py --repo-root $repoRoot --cases 50000", "existing direct machine gate"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
    ]
    for text, needle, label in required:
        checks += require(text, needle, label)

    process_start = engine.index("var processTask = _machineProcessActivityProvider.CaptureAsync")
    cpu_start = engine.index("var systemCpuTask = CaptureSystemCpuActivitySafelyAsync")
    wait = engine.index("Task.WhenAll(processTask, systemCpuTask)")
    if process_start > wait or cpu_start > wait:
        raise AssertionError("both bounded captures must start before Task.WhenAll")
    checks += 1

    if view_xaml.count('Content="Capture machine activity"') != 1:
        raise AssertionError("machine card must keep exactly one explicit capture button")
    checks += 1

    apply = method_body(view, "public void Apply(")
    checks += forbid(apply, ".OrderBy", "CPU context changing process row order")
    checks += forbid(apply, "ProcessorTimeDelta.TotalSeconds /", "process CPU percentage derivation")
    checks += forbid(apply, "BusyPercent /", "secondary CPU percentage math")

    cpu_apply = method_body(view, "private void ApplySystemCpu")
    checks += forbid(cpu_apply, "MachineProcessActivityRow", "CPU context consuming process rows")
    checks += forbid(cpu_apply, "OrderBy", "CPU context ranking process rows")
    checks += forbid(cpu_apply, "Math.Clamp", "CPU threshold/clamping")

    application_code = "\n".join((engine, main, storage, performance, view))
    for needle in (
        "PeriodicTimer",
        "DispatcherQueueTimer",
        "FileSystemWatcher",
        "PerformanceCounter",
        "PriorityClass =",
        "ProcessorAffinity =",
        "Kill(",
        "CloseMainWindow",
        "CpuPressureScore",
        "CpuHealthScore",
    ):
        checks += forbid(application_code, needle, "poller/control/score path")

    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0xC0FFEE55)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = " and %s source/UI checks" % format(source_checks, ",") if args.repo_root else ""
    print(
        "PASS: system CPU activity UI verified with %s model assertions across %s randomized states%s."
        % (format(model_checks, ","), format(args.cases, ","), suffix)
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
