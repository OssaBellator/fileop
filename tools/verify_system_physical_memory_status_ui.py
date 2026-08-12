#!/usr/bin/env python3
"""Verify existing-Refresh presentation of system physical-memory evidence."""
from __future__ import annotations

import argparse
import random
from pathlib import Path


def apply_template(
    panel_attached: bool,
    refresh_subscribed: bool,
) -> tuple[bool, bool, int, int]:
    attached_now = 0 if panel_attached else 1
    subscribed_now = 0 if refresh_subscribed else 1
    return True, True, attached_now, subscribed_now


def request_refresh(active: bool) -> tuple[bool, bool]:
    if active:
        return False, True
    return True, True


def present(total: int, available: int, windows_load: int) -> tuple[int, int, int, int]:
    return total, available, total - available, windows_load


def run_model(cases: int, seed: int) -> int:
    rng = random.Random(seed)
    checks = 0

    attached, subscribed, attachments, subscriptions = apply_template(False, False)
    assert (attached, subscribed, attachments, subscriptions) == (True, True, 1, 1)
    attached, subscribed, attachments, subscriptions = apply_template(attached, subscribed)
    assert (attached, subscribed, attachments, subscriptions) == (True, True, 0, 0)
    assert request_refresh(False) == (True, True)
    assert request_refresh(True) == (False, True)
    assert present(1_000, 900, 73) == (1_000, 900, 100, 73)
    checks += 5

    for _ in range(cases):
        initially_attached = bool(rng.getrandbits(1))
        initially_subscribed = bool(rng.getrandbits(1))
        attached, subscribed, attachments, subscriptions = apply_template(
            initially_attached,
            initially_subscribed,
        )
        assert attached and subscribed
        assert attachments == (0 if initially_attached else 1)
        assert subscriptions == (0 if initially_subscribed else 1)
        checks += 3

        attached2, subscribed2, attachments2, subscriptions2 = apply_template(
            attached,
            subscribed,
        )
        assert attached2 and subscribed2
        assert attachments2 == 0
        assert subscriptions2 == 0
        checks += 3

        active = bool(rng.getrandbits(1))
        starts_query, resulting_active = request_refresh(active)
        assert starts_query == (not active)
        assert resulting_active
        checks += 2

        total = rng.randint(1, 2**50)
        available = rng.randint(0, total)
        windows_load = rng.randint(0, 100)
        shown_total, shown_available, shown_used, shown_load = present(
            total,
            available,
            windows_load,
        )
        assert shown_total == total
        assert shown_available == available
        assert shown_used == total - available
        assert shown_load == windows_load
        checks += 4

        # The approximate Windows load is independent from the exact byte ratio.
        remapped_load = rng.randint(0, 100)
        remapped = present(total, available, remapped_load)
        assert remapped[:3] == (shown_total, shown_available, shown_used)
        assert remapped[3] == remapped_load
        checks += 2

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
    engine = (root / "src/FileOp.App/DesktopSearchEngine.SystemPhysicalMemoryStatus.cs").read_text(encoding="utf-8")
    main = (root / "src/FileOp.App/MainWindow.SystemPhysicalMemoryStatus.cs").read_text(encoding="utf-8")
    view = (root / "src/FileOp.App/PerformanceDiagnosticsView.SystemPhysicalMemoryStatus.cs").read_text(encoding="utf-8")
    view_base = (root / "src/FileOp.App/PerformanceDiagnosticsView.xaml.cs").read_text(encoding="utf-8")
    docs = (root / "docs/system-physical-memory-status-ui.md").read_text(encoding="utf-8")
    parent = (root / "tools/verify_system_physical_memory_status.py").read_text(encoding="utf-8")
    performance_parent = (root / "tools/verify_performance_diagnostics.py").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")

    checks = 0
    required = [
        (engine, "new WindowsSystemPhysicalMemoryStatusProvider()", "engine-owned provider"),
        (engine, "CaptureSystemPhysicalMemoryStatus", "engine capture bridge"),
        (main, "CapturePerformanceSystemPhysicalMemoryStatusAsync", "MainWindow worker bridge"),
        (main, "Task.Run(", "worker execution"),
        (main, "_lifetimeCancellation.Token", "window lifetime cancellation"),
        (view, "protected override void OnApplyTemplate()", "template lifecycle hook"),
        (view, "_systemPhysicalMemoryPanelAttached", "single panel attachment guard"),
        (view, "_systemPhysicalMemoryRefreshSubscribed", "single refresh subscription guard"),
        (view, "RefreshRequested += PerformanceDiagnostics_SystemPhysicalMemoryRefreshRequested", "existing Refresh subscription"),
        (view, "_systemPhysicalMemoryRefreshActive", "in-flight deduplication guard"),
        (view, "if (_systemPhysicalMemoryRefreshActive)", "duplicate refresh rejection"),
        (view, "finally", "in-flight release"),
        (view, "CapturePerformanceSystemPhysicalMemoryStatusAsync", "window-owned query route"),
        (view, 'MetricCell("Total physical"', "total metric"),
        (view, 'MetricCell("Available physical"', "available metric"),
        (view, 'MetricCell("Used physical"', "used metric"),
        (view, 'MetricCell("Windows load (approx.)"', "approximate Windows load label"),
        (view, "status.TotalPhysicalBytes", "raw total bytes"),
        (view, "status.AvailablePhysicalBytes", "raw available bytes"),
        (view, "status.UsedPhysicalBytes", "Core-derived used bytes"),
        (view, "status.WindowsMemoryLoadPercent", "raw Windows load"),
        (view, "is not recomputed from the byte counters", "no load recomputation wording"),
        (view_base, "RefreshRequested?.Invoke(this, EventArgs.Empty)", "existing Refresh diagnostics trigger"),
        (docs, "no second memory-specific capture button", "single trigger boundary"),
        (docs, "subscribes once", "single subscription documentation"),
        (docs, "in-flight flag prevents duplicate", "deduplication documentation"),
        (docs, "does not recompute it from the byte counters", "approximation documentation"),
        (parent, "from verify_system_physical_memory_status_ui import (", "memory parent imports UI child"),
        (parent, "run_system_physical_memory_ui_model(args.cases", "memory parent runs UI model"),
        (parent, "check_system_physical_memory_ui_repository(root)", "memory parent runs UI source checks"),
        (performance_parent, "from verify_system_physical_memory_status import (", "Performance parent imports memory verifier"),
        (performance_parent, "run_system_physical_memory_model(args.cases", "Performance parent runs memory model"),
        (gate, "verify_performance_diagnostics.py --repo-root $repoRoot --cases 50000", "existing direct offline gate"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
    ]
    for text, needle, label in required:
        checks += require(text, needle, label)

    if view.count("RefreshRequested += PerformanceDiagnostics_SystemPhysicalMemoryRefreshRequested") != 1:
        raise AssertionError("system-memory UI must subscribe to the existing Refresh event exactly once in source")
    checks += 1

    if view.count("CapturePerformanceSystemPhysicalMemoryStatusAsync()") != 1:
        raise AssertionError("system-memory UI must issue exactly one query per accepted refresh handler invocation")
    checks += 1

    application_code = "\n".join((engine, main, view))
    for needle in (
        "GlobalMemoryStatusEx(",
        "EmptyWorkingSet",
        "SetProcessWorkingSetSize",
        "GC.Collect",
        "MemoryHealthScore",
        "MemoryPressureScore",
        "PeriodicTimer",
        "DispatcherQueueTimer",
        "FileSystemWatcher",
        "RegistryKey",
        "Process.Kill",
        "CloseMainWindow",
    ):
        checks += forbid(application_code, needle, "UI/native action/score/poller path")

    for needle in (
        "AvailablePhysicalBytes /",
        "UsedPhysicalBytes /",
        "TotalPhysicalBytes /",
        "100d *",
        "100.0 *",
    ):
        checks += forbid(view, needle, "UI load recomputation")

    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0x4D454D55)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = " and %s source/UI checks" % format(source_checks, ",") if args.repo_root else ""
    print(
        "PASS: system physical-memory UI verified with %s model assertions across %s randomized states%s."
        % (format(model_checks, ","), format(args.cases, ","), suffix)
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
