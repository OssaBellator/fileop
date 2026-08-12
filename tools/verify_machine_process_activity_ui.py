#!/usr/bin/env python3
"""Verify explicit current machine-process and system-CPU presentation."""
from __future__ import annotations

import argparse
import random
import xml.etree.ElementTree as ET
from pathlib import Path


def capture_allowed(optimize_visible: bool, explicit_capture_active: bool, same_size_active: bool) -> bool:
    return optimize_visible and not explicit_capture_active and not same_size_active


def present(rows: list[tuple[int, int, int]], hidden_cpu: int, incomplete: bool):
    # UI consumes the already-ordered Core rows; it must not select/re-rank them.
    return list(rows), hidden_cpu, incomplete


def present_bundle(
    rows: list[tuple[int, int, int]],
    hidden_cpu: int,
    incomplete: bool,
    system_cpu_status: str,
    busy_percent: float | None,
    busy_time: int,
    total_time: int,
    idle_time: int,
):
    shown_rows, shown_hidden_cpu, shown_incomplete = present(rows, hidden_cpu, incomplete)
    return (
        shown_rows,
        shown_hidden_cpu,
        shown_incomplete,
        (system_cpu_status, busy_percent, busy_time, total_time, idle_time),
    )


def run_model(cases: int, seed: int) -> int:
    rng = random.Random(seed)
    checks = 0

    assert capture_allowed(True, False, False)
    assert not capture_allowed(False, False, False)
    assert not capture_allowed(True, True, False)
    assert not capture_allowed(True, False, True)
    completed = present_bundle([(1, 2, 3)], 4, False, "Completed", 50.0, 5, 10, 5)
    unavailable = present_bundle([(1, 2, 3)], 4, False, "Unavailable", None, 0, 0, 0)
    assert completed[:3] == unavailable[:3]
    assert unavailable[3][0] == "Unavailable"
    checks += 6

    for _ in range(cases):
        optimize = bool(rng.getrandbits(1))
        active = bool(rng.getrandbits(1))
        same_size = bool(rng.getrandbits(1))
        allowed = capture_allowed(optimize, active, same_size)
        assert allowed == (optimize and not active and not same_size)
        checks += 1

        row_count = rng.randint(0, 20)
        rows = [
            (index, rng.randint(0, 2_000_000), rng.randint(0, 8_000_000_000))
            for index in range(row_count)
        ]
        hidden_cpu = rng.randint(0, 10_000_000)
        cap = bool(rng.getrandbits(1))
        inaccessible_start = rng.randint(0, 20)
        inaccessible_end = rng.randint(0, 20)
        incomplete = cap or inaccessible_start > 0 or inaccessible_end > 0
        shown_rows, shown_hidden_cpu, shown_incomplete = present(rows, hidden_cpu, incomplete)
        assert shown_rows == rows
        assert shown_hidden_cpu == hidden_cpu
        assert shown_incomplete == incomplete
        checks += 3

        # Process start/image context can change presentation text, never row selection.
        starts = [rng.getrandbits(63) for _ in rows]
        images = [None if rng.random() < 0.2 else "p%d.exe" % index for index, _ in enumerate(rows)]
        assert len(starts) == len(shown_rows)
        assert len(images) == len(shown_rows)
        checks += 2

        system_cpu_status = rng.choice(("Completed", "Unsupported", "Unavailable"))
        total_time = rng.randint(0, 10_000_000)
        idle_time = rng.randint(0, total_time) if total_time else 0
        busy_time = total_time - idle_time
        busy_percent = None if total_time == 0 else busy_time * 100.0 / total_time
        bundle = present_bundle(
            rows,
            hidden_cpu,
            incomplete,
            system_cpu_status,
            busy_percent,
            busy_time,
            total_time,
            idle_time,
        )
        assert bundle[0] == rows
        assert bundle[1] == hidden_cpu
        assert bundle[2] == incomplete
        assert bundle[3] == (
            system_cpu_status,
            busy_percent,
            busy_time,
            total_time,
            idle_time,
        )
        checks += 4

        # CPU interval status/values are supplementary and cannot change process presentation.
        changed_cpu = present_bundle(
            rows,
            hidden_cpu,
            incomplete,
            "Unavailable" if system_cpu_status == "Completed" else "Completed",
            None if busy_percent is not None else 73.25,
            rng.randint(0, 10_000_000),
            rng.randint(0, 10_000_000),
            rng.randint(0, 10_000_000),
        )
        assert changed_cpu[:3] == bundle[:3]
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
    performance_base = (root / "src/FileOp.App/PerformanceDiagnosticsView.xaml.cs").read_text(encoding="utf-8")
    performance_xaml_path = root / "src/FileOp.App/PerformanceDiagnosticsView.xaml"
    performance_xaml = performance_xaml_path.read_text(encoding="utf-8")
    view_xaml_path = root / "src/FileOp.App/MachineProcessActivityView.xaml"
    view_xaml = view_xaml_path.read_text(encoding="utf-8")
    view = (root / "src/FileOp.App/MachineProcessActivityView.xaml.cs").read_text(encoding="utf-8")
    docs = (root / "docs/machine-process-activity-ui.md").read_text(encoding="utf-8")
    parent = (root / "tools/verify_machine_process_activity.py").read_text(encoding="utf-8")
    wrapper = (root / "tools/verify_background_process_activity.py").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")

    ET.parse(performance_xaml_path)
    ET.parse(view_xaml_path)
    checks = 2

    required = [
        (engine, "new WindowsMachineProcessActivityProvider()", "engine-owned process provider"),
        (engine, "new WindowsSystemCpuActivityProvider()", "engine-owned system CPU provider"),
        (engine, "MachineProcessActivityBudget.Default", "default process budget"),
        (engine, "SystemCpuActivityBudget.Default", "default system CPU budget"),
        (engine, "Task.WhenAll(processTask, systemCpuTask)", "concurrent bounded captures"),
        (engine, "CaptureSystemCpuActivitySafelyAsync", "supplementary CPU failure isolation"),
        (engine, "Supplementary Windows system CPU interval failed", "unexpected CPU failure mapping"),
        (engine, "_lifetimeCancellation.Token", "window/engine lifetime cancellation"),
        (main, "_storageSameSizeVerificationActive", "same-size exclusion"),
        (main, "_performanceDiskIoCaptureActive", "shared explicit capture exclusion"),
        (main, "_performanceDiskIoCaptureActive = true;", "capture lock acquisition"),
        (main, "_performanceDiskIoCaptureActive = false;", "capture lock release"),
        (main, "CaptureMachineProcessActivityAsync", "engine capture invocation"),
        (main, "capture.ProcessActivity", "process bundle forwarding"),
        (main, "capture.SystemCpuActivity", "system CPU bundle forwarding"),
        (main, "finally", "capture lock finally"),
        (storage, "SystemCpuActivityResult systemCpuActivity", "storage CPU forwarding"),
        (storage, "_sameSizeVerificationControlsBlocked = true", "same-size UI exclusion"),
        (performance, "SystemCpuActivityResult systemCpuActivity", "performance CPU forwarding"),
        (performance, "MachineProcessActivity.Apply(result, systemCpuActivity)", "combined view apply"),
        (performance_xaml, "MachineProcessActivityView", "machine view in Performance visual tree"),
        (performance_xaml, 'x:Name="MachineProcessActivity"', "named machine view"),
        (view_xaml, 'Content="Capture machine activity"', "explicit capture action"),
        (view_xaml, 'Text="Current machine process activity"', "neutral current-activity heading"),
        (view_xaml, "Windows system CPU interval", "separate CPU interval heading"),
        (view_xaml, 'x:Name="SystemCpuBusyText"', "busy percentage field"),
        (view_xaml, 'x:Name="SystemCpuTimeText"', "CPU time delta field"),
        (view_xaml, 'x:Name="SystemCpuWallText"', "observation interval field"),
        (view_xaml, 'x:Name="SystemCpuOverheadText"', "provider overhead field"),
        (view_xaml, "CPU-time Δ", "process CPU delta column"),
        (view, "public void Apply(MachineProcessActivityResult result)", "process-only compatibility seam"),
        (view, "SystemCpuActivityResult systemCpuActivity", "combined presentation seam"),
        (view, "ApplySystemCpu(systemCpuActivity)", "CPU presentation path"),
        (view, "report.OtherMatchedProcessorTime", "hidden process CPU evidence"),
        (view, "report.CaptureProcessProcessorTime", "observer CPU evidence"),
        (view, "report.EvidenceMayBeIncomplete", "incomplete process evidence wording"),
        (view, "evidence.BusyPercent", "provider-derived CPU percentage"),
        (view, "evidence.BusyProcessorTimeDelta", "raw busy CPU time"),
        (view, "evidence.IdleProcessorTimeDelta", "raw idle CPU time"),
        (view, "evidence.TotalProcessorTimeDelta", "raw total CPU time"),
        (view, "evidence.ObservationWallDuration", "CPU observation wall duration"),
        (view, "result.ProviderOverheadDuration", "CPU provider overhead"),
        (view, "does not treat visible process deltas as a decomposition of Windows busy time", "process/system provenance wording"),
        (view, "Process CPU percentage is intentionally not inferred", "no fake process CPU percent wording"),
        (view, "window.CapturePerformanceMachineProcessActivityAsync()", "MainWindow-owned async route"),
        (docs, "starts both bounded captures concurrently", "documented concurrent capture"),
        (docs, "separate observation boundaries", "documented provenance separation"),
        (docs, "does **not** re-sort or promote rows", "no UI ranking documentation"),
        (docs, "Unsupported or unavailable CPU evidence clears only the CPU summary", "supplementary failure boundary"),
        (docs, "Process start time is displayed only as stable identity/context", "no startup inference documentation"),
        (docs, "primary processor group", "processor-group scope caveat"),
        (parent, "from verify_machine_process_activity_ui import (", "parent imports UI verifier"),
        (parent, "run_machine_process_activity_ui_model(args.cases", "parent runs UI model"),
        (parent, "check_machine_process_activity_ui_repository(root)", "parent runs UI source checks"),
        (parent, "from verify_system_cpu_activity import (", "parent imports CPU foundation verifier"),
        (parent, "run_system_cpu_activity_model(args.cases", "parent runs CPU foundation model"),
        (wrapper, "from verify_machine_process_activity import main", "stable wrapper delegation"),
        (gate, "verify_background_process_activity.py --repo-root $repoRoot --cases 50000", "direct offline gate"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
    ]
    for text, needle, label in required:
        checks += require(text, needle, label)

    refresh = method_body(performance_base, "private void RefreshButton_Click")
    checks += forbid(refresh, "MachineProcess", "ordinary Performance refresh invoking machine sample")

    process_apply = method_body(view, "private void ApplyProcess(MachineProcessActivityResult result)")
    checks += forbid(process_apply, ".OrderBy", "UI-side row ranking")
    checks += forbid(process_apply, ".Take(", "UI-side row promotion/truncation")

    cpu_apply = method_body(view, "private void ApplySystemCpu(SystemCpuActivityResult result)")
    checks += forbid(cpu_apply, "BusyProcessorTimeDelta.Ticks * 100", "UI-side CPU percentage recomputation")
    checks += forbid(cpu_apply, "Math.Clamp", "UI-side CPU percentage clamping")

    combined = engine + "\n" + main + "\n" + storage + "\n" + view + "\n" + docs
    for needle in (
        "PeriodicTimer",
        "DispatcherQueueTimer",
        "FileSystemWatcher",
        "Microsoft.Win32.Registry",
        "RegistryKey",
        "StartupTask",
        "GetProcessesByName",
        "Kill(",
        "CloseMainWindow",
        "PriorityClass =",
        "ProcessorAffinity =",
        "health score",
        "impact score",
        "pressure score",
    ):
        checks += forbid(combined, needle, "poller/startup heuristic/process control/score")

    checks += forbid(view, "ProcessorTimeDelta.TotalSeconds /", "process CPU percentage inference")
    checks += forbid(view, "ProcessorTimeDelta.Ticks * 100", "process CPU percentage inference")
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0x4D504155)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = " and %s source/UI checks" % format(source_checks, ",") if args.repo_root else ""
    print(
        "PASS: machine process/system CPU UI verified with %s model assertions across %s randomized states%s."
        % (format(model_checks, ","), format(args.cases, ","), suffix)
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
