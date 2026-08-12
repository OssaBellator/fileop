#!/usr/bin/env python3
"""Verify explicit volume fragmentation analysis presentation and lifecycle."""
from __future__ import annotations

import argparse
import random
import xml.etree.ElementTree as ET
from pathlib import Path


def drive_root(storage_source: str | None) -> str | None:
    if not storage_source or len(storage_source) < 3:
        return None
    if not storage_source[0].isalpha() or not storage_source[0].isascii():
        return None
    if storage_source[1] != ":" or storage_source[2] not in "\\/":
        return None
    return storage_source[0].upper() + ":\\"


def capture_allowed(
    optimize_visible: bool,
    explicit_capture_active: bool,
    same_size_active: bool,
    storage_source: str | None,
    native_index_available: bool | None = None,
) -> bool:
    # Fragmentation analysis is volume/WMI evidence and deliberately does not
    # depend on native-index availability once a canonical local drive exists.
    _ = native_index_available
    return (
        optimize_visible
        and not explicit_capture_active
        and not same_size_active
        and drive_root(storage_source) is not None
    )


def result_applies(captured_root: str, current_source: str | None) -> bool:
    current_root = drive_root(current_source)
    return current_root is not None and current_root.casefold() == captured_root.casefold()


def present(recommendation: bool, metrics: tuple[int, float, int, int]):
    # The UI preserves the Windows recommendation bit and provider metrics; it
    # does not calculate its own optimization verdict or re-rank evidence.
    return recommendation, metrics


def run_model(cases: int, seed: int) -> int:
    rng = random.Random(seed)
    checks = 0

    assert drive_root(r"c:\Users\Example") == "C:\\"
    assert drive_root("D:/data") == "D:\\"
    assert drive_root(r"\\server\share") is None
    assert drive_root("relative") is None
    assert capture_allowed(True, False, False, r"C:\Users\Example")
    assert not capture_allowed(False, False, False, r"C:\Users\Example")
    checks += 6

    for _ in range(cases):
        optimize = bool(rng.getrandbits(1))
        active = bool(rng.getrandbits(1))
        same_size = bool(rng.getrandbits(1))
        native_index_available = bool(rng.getrandbits(1))
        drive = chr(ord("A") + rng.randrange(26))
        source_kind = rng.randrange(4)
        source = {
            0: f"{drive}:\\Users\\Example",
            1: f"{drive}:/data",
            2: r"\\server\share\folder",
            3: "relative/path",
        }[source_kind]
        allowed = capture_allowed(
            optimize,
            active,
            same_size,
            source,
            native_index_available,
        )
        assert allowed == (
            optimize
            and not active
            and not same_size
            and source_kind in (0, 1)
        )
        checks += 1

        captured = drive_root(source)
        if captured is None:
            captured = "C:\\"
        current_kind = rng.randrange(3)
        current = (
            f"{captured[0]}:\\other" if current_kind == 0
            else f"{chr(((ord(captured[0]) - ord('A') + 1) % 26) + ord('A'))}:\\other"
            if current_kind == 1
            else r"\\server\share"
        )
        assert result_applies(captured, current) == (current_kind == 0)
        checks += 1

        recommendation = bool(rng.getrandbits(1))
        metrics = (
            rng.randint(0, 100),
            rng.random() * 20,
            rng.randint(0, 1_000_000),
            rng.randint(0, 1_000_000),
        )
        shown_recommendation, shown_metrics = present(recommendation, metrics)
        assert shown_recommendation == recommendation
        assert shown_metrics == metrics
        checks += 2

        # Toggling native-index availability alone must never change whether the
        # same explicit drive-root analysis is eligible.
        assert capture_allowed(
            optimize, active, same_size, source, True
        ) == capture_allowed(
            optimize, active, same_size, source, False
        )
        assert allowed == capture_allowed(
            optimize, active, same_size, source, native_index_available
        )
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
    engine = (root / "src/FileOp.App/DesktopSearchEngine.VolumeFragmentationAnalysis.cs").read_text(encoding="utf-8")
    main = (root / "src/FileOp.App/MainWindow.VolumeFragmentationAnalysis.cs").read_text(encoding="utf-8")
    machine_main = (root / "src/FileOp.App/MainWindow.MachineProcessActivity.cs").read_text(encoding="utf-8")
    storage = (root / "src/FileOp.App/StorageOptimizationView.VolumeFragmentationAnalysis.cs").read_text(encoding="utf-8")
    performance = (root / "src/FileOp.App/PerformanceDiagnosticsView.VolumeFragmentationAnalysis.cs").read_text(encoding="utf-8")
    performance_base = (root / "src/FileOp.App/PerformanceDiagnosticsView.xaml.cs").read_text(encoding="utf-8")
    performance_xaml_path = root / "src/FileOp.App/PerformanceDiagnosticsView.xaml"
    performance_xaml = performance_xaml_path.read_text(encoding="utf-8")
    view_xaml_path = root / "src/FileOp.App/VolumeFragmentationAnalysisView.xaml"
    view_xaml = view_xaml_path.read_text(encoding="utf-8")
    view = (root / "src/FileOp.App/VolumeFragmentationAnalysisView.xaml.cs").read_text(encoding="utf-8")
    docs = (root / "docs/volume-fragmentation-analysis-ui.md").read_text(encoding="utf-8")
    parent = (root / "tools/verify_volume_fragmentation_analysis.py").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")

    ET.parse(performance_xaml_path)
    ET.parse(view_xaml_path)
    checks = 2

    required = [
        (engine, "new WindowsVolumeFragmentationAnalysisProvider()", "engine-owned provider"),
        (engine, "VolumeFragmentationAnalysisRoot", "derived volume root"),
        (engine, "Path.GetPathRoot(storageRoot)", "containing drive derivation"),
        (engine, "VolumeFragmentationDriveRoot.RequireCanonical", "canonical drive boundary"),
        (engine, "CaptureVolumeFragmentationAnalysisAsync", "engine capture bridge"),
        (engine, "VolumeFragmentationAnalysisBudget.Default", "default analysis budget"),
        (engine, "_lifetimeCancellation.Token", "engine lifetime cancellation"),
        (main, "_performanceDiskIoCaptureActive", "shared explicit capture exclusion"),
        (main, "var volumeRoot = _searchEngine.VolumeFragmentationAnalysisRoot", "captured drive identity"),
        (main, "CaptureVolumeFragmentationAnalysisAsync(volumeRoot)", "captured root passed to engine"),
        (main, "var currentVolumeRoot = _searchEngine.VolumeFragmentationAnalysisRoot", "post-analysis source recheck"),
        (main, "The stale result was discarded", "stale result discard"),
        (main, "SetMachineProcessActivityReadyForCapture(false)", "machine exclusion"),
        (main, "_performanceDiskIoCaptureActive = true;", "capture lock acquisition"),
        (main, "_performanceDiskIoCaptureActive = false;", "capture lock release"),
        (main, "finally", "capture lock finally"),
        (machine_main, "SetVolumeFragmentationAnalysisReadyForCapture(false)", "fragmentation excluded during machine sample"),
        (machine_main, "VolumeFragmentationAnalysisRoot is not null", "fragmentation readiness restored by source"),
        (storage, "_sameSizeVerificationControlsBlocked = true", "same-size UI exclusion"),
        (storage, "SetMachineProcessActivityReadyForCapture(false)", "machine UI exclusion"),
        (performance, "VolumeFragmentationAnalysis.SetLoading", "performance forwarding"),
        (performance_xaml, "VolumeFragmentationAnalysisView", "fragmentation card in Performance tree"),
        (performance_xaml, 'x:Name="VolumeFragmentationAnalysis"', "named fragmentation card"),
        (view_xaml, 'Content="Analyze fragmentation"', "explicit analysis action"),
        (view_xaml, "may be unsupported on current Windows clients", "legacy support warning"),
        (view_xaml, "does not run Defrag, ReTrim, Optimize-Volume", "no-action UI wording"),
        (view_xaml, "Windows legacy recommendation", "provider recommendation label"),
        (view, "reported yes · evidence only", "neutral positive recommendation rendering"),
        (view, "reported no · evidence only", "neutral negative recommendation rendering"),
        (view, "result.ProviderReturnCode", "raw provider result rendering"),
        (view, "result.Elapsed.TotalMilliseconds", "elapsed rendering"),
        (view, "window.CapturePerformanceVolumeFragmentationAnalysisAsync()", "MainWindow-owned async route"),
        (docs, "does **not** require `StorageOptimizationAvailable`", "index independence"),
        (docs, "discards the stale result", "documented source stability"),
        (docs, "does not invent a volume-to-physical-disk mapping", "TRIM evidence independence"),
        (parent, "from verify_volume_fragmentation_analysis_ui import (", "parent imports UI verifier"),
        (parent, "run_volume_fragmentation_ui_model(args.cases", "parent runs UI model"),
        (parent, "check_volume_fragmentation_ui_repository(root)", "parent runs UI source checks"),
        (gate, "verify_performance_diagnostics.py --repo-root $repoRoot --cases 50000", "existing transitive offline gate"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
    ]
    for text, needle, label in required:
        checks += require(text, needle, label)

    capture = method_body(main, "internal async Task CapturePerformanceVolumeFragmentationAnalysisAsync")
    checks += forbid(capture, "StorageOptimizationAvailable", "native-index dependency")
    checks += forbid(capture, "Task.Run", "detached fragmentation analysis")

    refresh = method_body(performance_base, "private void RefreshButton_Click")
    checks += forbid(refresh, "Fragmentation", "ordinary Performance refresh invoking fragmentation")

    apply = method_body(view, "public void Apply(VolumeFragmentationAnalysisResult result)")
    checks += forbid(apply, ".OrderBy", "UI ranking")
    checks += forbid(apply, "WindowsDefragRecommended ? \"Defrag", "recommendation promotion")

    application_code = "\n".join((engine, main, machine_main, storage, performance, view))
    for needle in (
        "Process.Start",
        "PowerShell.Create",
        "System.Management",
        "PeriodicTimer",
        "DispatcherQueueTimer",
        "FileSystemWatcher",
        "FSCTL_FILE_LEVEL_TRIM",
        "IOCTL_STORAGE_PROTOCOL_COMMAND",
        "HealthScore",
        "OptimizationScore",
        "UrgencyScore",
    ):
        checks += forbid(application_code, needle, "action/poller/score path")

    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0xF6A6A11)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = " and %s source/UI checks" % format(source_checks, ",") if args.repo_root else ""
    print(
        "PASS: volume fragmentation analysis UI verified with %s model assertions across %s randomized states%s."
        % (format(model_checks, ","), format(args.cases, ","), suffix)
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
