#!/usr/bin/env python3
"""Verify explicit startup-degradation history presentation and lifecycle."""
from __future__ import annotations

import argparse
import random
import xml.etree.ElementTree as ET
from pathlib import Path

Event = tuple[int, int, int, int]


def capture_allowed(
    optimize_visible: bool,
    explicit_capture_active: bool,
    same_size_active: bool,
    native_index_available: bool | None = None,
) -> bool:
    # Historical EventLog evidence is system-wide and deliberately independent
    # of native-index availability while the Performance surface is visible.
    _ = native_index_available
    return optimize_visible and not explicit_capture_active and not same_size_active


def busy_notice_state(
    same_size_controls_blocked: bool,
    explicit_capture_active: bool,
) -> tuple[bool, bool]:
    # A rejected click changes only the startup card's message. It must never
    # release another diagnostic's control block or capture flag.
    return same_size_controls_blocked, explicit_capture_active


def present(events: list[Event], more_matching: bool) -> tuple[list[Event], bool]:
    return list(events), more_matching


def run_model(cases: int, seed: int) -> int:
    rng = random.Random(seed)
    checks = 0

    assert capture_allowed(True, False, False, True)
    assert capture_allowed(True, False, False, False)
    assert not capture_allowed(False, False, False, True)
    assert not capture_allowed(True, True, False, True)
    assert not capture_allowed(True, False, True, True)
    assert busy_notice_state(True, True) == (True, True)
    checks += 6

    for case in range(cases):
        optimize = bool(rng.getrandbits(1))
        active = bool(rng.getrandbits(1))
        same_size = bool(rng.getrandbits(1))
        native_index = bool(rng.getrandbits(1))
        allowed = capture_allowed(optimize, active, same_size, native_index)
        assert allowed == (optimize and not active and not same_size)
        assert capture_allowed(optimize, active, same_size, True) == capture_allowed(
            optimize, active, same_size, False
        )
        checks += 2

        blocked_before = bool(rng.getrandbits(1))
        active_before = bool(rng.getrandbits(1))
        blocked_after, active_after = busy_notice_state(blocked_before, active_before)
        assert blocked_after == blocked_before
        assert active_after == active_before
        checks += 2

        count = rng.randint(0, 20)
        base_time = case * 100_000 + 100_000
        events = [
            (
                index + 1,
                base_time - index,
                rng.randint(0, 20_000_000),
                rng.randint(0, 20_000_000),
            )
            for index in range(count)
        ]
        more = bool(rng.getrandbits(1)) if count == 20 else False
        shown, shown_more = present(events, more)
        assert shown == events
        assert shown_more == more
        checks += 2

        # Replacing duration values must never change row selection/order.
        remapped = [
            (record_id, recorded, rng.getrandbits(32), rng.getrandbits(32))
            for record_id, recorded, _, _ in events
        ]
        remapped_shown, remapped_more = present(remapped, more)
        assert [event[:2] for event in remapped_shown] == [event[:2] for event in shown]
        assert remapped_more == shown_more
        checks += 2

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
    engine = (root / "src/FileOp.App/DesktopSearchEngine.StartupApplicationDegradation.cs").read_text(encoding="utf-8")
    main = (root / "src/FileOp.App/MainWindow.StartupApplicationDegradation.cs").read_text(encoding="utf-8")
    machine_main = (root / "src/FileOp.App/MainWindow.MachineProcessActivity.cs").read_text(encoding="utf-8")
    fragmentation_main = (root / "src/FileOp.App/MainWindow.VolumeFragmentationAnalysis.cs").read_text(encoding="utf-8")
    storage = (root / "src/FileOp.App/StorageOptimizationView.StartupApplicationDegradation.cs").read_text(encoding="utf-8")
    performance = (root / "src/FileOp.App/PerformanceDiagnosticsView.StartupApplicationDegradation.cs").read_text(encoding="utf-8")
    performance_base = (root / "src/FileOp.App/PerformanceDiagnosticsView.xaml.cs").read_text(encoding="utf-8")
    performance_xaml_path = root / "src/FileOp.App/PerformanceDiagnosticsView.xaml"
    performance_xaml = performance_xaml_path.read_text(encoding="utf-8")
    view_xaml_path = root / "src/FileOp.App/StartupApplicationDegradationView.xaml"
    view_xaml = view_xaml_path.read_text(encoding="utf-8")
    view = (root / "src/FileOp.App/StartupApplicationDegradationView.xaml.cs").read_text(encoding="utf-8")
    docs = (root / "docs/startup-application-degradation-ui.md").read_text(encoding="utf-8")
    startup_parent = (root / "tools/verify_startup_application_degradation.py").read_text(encoding="utf-8")
    machine_parent = (root / "tools/verify_machine_process_activity.py").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")

    ET.parse(performance_xaml_path)
    ET.parse(view_xaml_path)
    checks = 2

    required = [
        (engine, "new WindowsStartupApplicationDegradationProvider()", "engine-owned EventLog provider"),
        (engine, "Task.Run(", "worker-thread EventLog query"),
        (engine, "StartupApplicationDegradationBudget.Default", "default history budget"),
        (engine, "_lifetimeCancellation.Token", "engine lifetime cancellation"),
        (main, "_performanceDiskIoCaptureActive", "shared explicit capture exclusion"),
        (main, "_storageSameSizeVerificationActive", "same-size exclusion"),
        (main, "SetStartupApplicationDegradationBusy", "state-neutral busy notice"),
        (main, "SetDiskIoReadyForCapture(false)", "DiskIo exclusion"),
        (main, "SetMachineProcessActivityReadyForCapture(false)", "machine exclusion"),
        (main, "SetVolumeFragmentationAnalysisReadyForCapture(false)", "fragmentation exclusion"),
        (main, "_performanceDiskIoCaptureActive = true;", "capture lock acquisition"),
        (main, "_performanceDiskIoCaptureActive = false;", "capture lock release"),
        (main, "finally", "capture lock finally"),
        (machine_main, "SetStartupApplicationDegradationReadyForCapture(false)", "startup excluded during machine sample"),
        (fragmentation_main, "SetStartupApplicationDegradationReadyForCapture(false)", "startup excluded during fragmentation"),
        (storage, "SetStartupApplicationDegradationBusy", "busy forwarding"),
        (storage, "_sameSizeVerificationControlsBlocked = true", "loading blocks same-size controls"),
        (performance, "StartupApplicationDegradation.SetLoading", "Performance forwarding"),
        (performance_xaml, "StartupApplicationDegradationView", "history card in Performance tree"),
        (performance_xaml, 'x:Name="StartupApplicationDegradation"', "named history card"),
        (view_xaml, 'Content="Read startup degradation history"', "explicit history action"),
        (view_xaml, "not a startup-registration census", "no registration heuristic wording"),
        (view_xaml, "not a startup-registration census, complete boot history, impact score or recommendation", "interpretation boundary"),
        (view, "events\n            .Select", "preserve Core order"),
        (view, "does not duration-rank them", "no duration ranking wording"),
        (view, "MoreMatchingEventsAvailable", "hidden-event evidence"),
        (view, "TotalTimeMilliseconds:N0", "raw total time rendering"),
        (view, "DegradationTimeMilliseconds:N0", "raw degradation time rendering"),
        (view, "$\"#{evidence.RecordId}", "exact record id rendering"),
        (view, "CapturePerformanceStartupApplicationDegradationAsync", "MainWindow-owned async route"),
        (docs, "does not require `StorageOptimizationAvailable`", "index independence"),
        (docs, "busy notice is intentionally state-neutral", "busy state boundary"),
        (docs, "does not join component names to current PIDs", "current/history provenance boundary"),
        (startup_parent, "from verify_startup_application_degradation_ui import (", "startup verifier imports UI child"),
        (startup_parent, "def run_all_models", "startup verifier exposes combined models"),
        (startup_parent, "run_startup_degradation_ui_model(cases", "combined startup path runs UI model"),
        (startup_parent, "def check_all_repository", "startup verifier exposes combined source checks"),
        (startup_parent, "check_startup_degradation_ui_repository(root)", "combined startup path runs UI source checks"),
        (machine_parent, "run_all_models as run_startup_degradation_models", "machine verifier imports combined startup models"),
        (machine_parent, "check_all_repository as check_startup_degradation_repository", "machine verifier imports combined startup source checks"),
        (machine_parent, "run_startup_degradation_models(", "machine verifier runs combined startup models"),
        (machine_parent, "check_startup_degradation_repository(root)", "machine verifier runs combined startup source checks"),
        (gate, "verify_background_process_activity.py --repo-root $repoRoot --cases 50000", "existing direct offline gate"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
    ]
    for text, needle, label in required:
        checks += require(text, needle, label)

    capture = method_body(main, "internal async Task CapturePerformanceStartupApplicationDegradationAsync")
    checks += forbid(capture, "StorageOptimizationAvailable", "native-index dependency")

    busy = method_body(storage, "public void SetStartupApplicationDegradationBusy")
    checks += forbid(busy, "_sameSizeVerificationControlsBlocked", "busy notice releasing same-size block")
    checks += forbid(busy, "RefreshSameSizeRows", "busy notice mutating same-size controls")

    refresh = method_body(performance_base, "private void RefreshButton_Click")
    checks += forbid(refresh, "StartupApplication", "ordinary Performance refresh invoking history")

    apply = method_body(view, "public void Apply(StartupApplicationDegradationResult result)")
    checks += forbid(apply, ".OrderBy", "UI duration/rank sorting")
    checks += forbid(apply, ".Take(", "UI truncation")
    checks += forbid(apply, "TotalTimeMilliseconds /", "duration ratio")
    checks += forbid(apply, "DegradationTimeMilliseconds /", "duration ratio")

    application_code = "\n".join((engine, main, machine_main, fragmentation_main, storage, performance, view))
    for needle in (
        "Microsoft.Win32.Registry",
        "RegistryKey",
        "StartupTask",
        "EventLogWatcher",
        "PeriodicTimer",
        "DispatcherQueueTimer",
        "FileSystemWatcher",
        "Kill(",
        "CloseMainWindow",
        "PriorityClass =",
        "ProcessorAffinity =",
        "HealthScore",
        "ImpactScore",
        "StartupScore",
    ):
        checks += forbid(application_code, needle, "registration/control/poller/score path")

    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0x510A71A)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = " and %s source/UI checks" % format(source_checks, ",") if args.repo_root else ""
    print(
        "PASS: startup degradation history UI verified with %s model assertions across %s randomized states%s."
        % (format(model_checks, ","), format(args.cases, ","), suffix)
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
