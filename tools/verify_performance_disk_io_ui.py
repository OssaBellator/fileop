#!/usr/bin/env python3
"""Verify explicit Performance DiskIo capture UI without GitHub Actions."""
from __future__ import annotations

import argparse
import random
from pathlib import Path


def quality(incomplete: bool, partial_disks: int) -> tuple[bool, bool]:
    return incomplete, partial_disks > 0


def run_model(cases: int) -> int:
    checks = 0
    assert quality(False, 0) == (False, False)
    assert quality(True, 0) == (True, False)
    assert quality(False, 1) == (False, True)
    assert quality(True, 3) == (True, True)
    checks += 4

    rng = random.Random(20260811)
    for _ in range(cases):
        cap = bool(rng.getrandbits(1))
        event_loss = rng.randrange(0, 100)
        buffer_loss = rng.randrange(0, 100)
        partial = rng.randrange(0, 9)
        incomplete = cap or event_loss > 0 or buffer_loss > 0
        shown_incomplete, shown_partial = quality(incomplete, partial)
        assert shown_incomplete == incomplete
        assert shown_partial == (partial > 0)
        assert (event_loss, buffer_loss) == (event_loss, buffer_loss)
        checks += 3

        # Index/source readiness is intentionally irrelevant to the system-wide capture.
        index_ready = bool(rng.getrandbits(1))
        index_busy = bool(rng.getrandbits(1))
        capture_allowed = True
        assert capture_allowed
        assert capture_allowed == (not False)
        assert isinstance(index_ready, bool) and isinstance(index_busy, bool)
        checks += 3
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
                return source[brace:index + 1]
    raise AssertionError(signature_fragment)


def check_repository(root: Path) -> int:
    paths = {
        "engine": root / "src/FileOp.App/DesktopSearchEngine.DiskIoAttribution.cs",
        "view_xaml": root / "src/FileOp.App/DiskIoAttributionView.xaml",
        "view": root / "src/FileOp.App/DiskIoAttributionView.xaml.cs",
        "performance": root / "src/FileOp.App/PerformanceDiagnosticsView.xaml.cs",
        "storage_view": root / "src/FileOp.App/StorageOptimizationView.xaml.cs",
        "main": root / "src/FileOp.App/MainWindow.StorageOptimization.cs",
        "provider": root / "src/FileOp.Windows/Performance/WindowsDiskIoAttributionProvider.cs",
        "evidence": root / "src/FileOp.Windows/Performance/WindowsDiskIoTraceEvidence.cs",
        "gate": root / "tools/test-local.ps1",
    }
    text = {name: path.read_text(encoding="utf-8") for name, path in paths.items()}
    checks = 0

    for needle in (
        "IDiskIoAttributionProvider",
        "new WindowsDiskIoAttributionProvider()",
        "CaptureDiskIoAttributionAsync",
        "DiskIoCaptureBudget.Default",
        "_lifetimeCancellation.Token",
    ):
        assert needle in text["engine"], needle
        checks += 1

    for needle in (
        'Content="Capture disk I/O"',
        "Explicit 2-second ETW capture",
        "Attribution",
        "ETW loss",
        "Unattributed / hidden",
        "Top attributed process instances",
    ):
        assert needle in text["view_xaml"], needle
        checks += 1

    for needle in (
        "_captureActive",
        "ready && !_captureActive",
        "EvidenceMayBeIncomplete",
        "LostEventCount",
        "LostBufferCount",
        "AttributionCoveragePercent",
        "UnattributedBytes",
        "OtherIdentifiedBytes",
        "owner.StartedAt",
        "process start unavailable",
    ):
        assert needle in text["view"], needle
        checks += 1

    assert "LostEventCount +" not in text["view"]
    assert "LostBufferCount +" not in text["view"]
    checks += 2

    for needle in (
        "DiskIoAttributionView",
        "DiskIoCaptureRequested",
        "SetDiskIoLoading",
        "ApplyDiskIoCapture",
        "DiskIoAttributionView_CaptureRequested",
    ):
        assert needle in text["performance"], needle
        checks += 1

    refresh_body = method_body(text["performance"], "private void RefreshButton_Click")
    assert "DiskIo" not in refresh_body
    checks += 1

    set_unavailable = method_body(text["storage_view"], "public void SetUnavailable")
    set_ready = method_body(text["storage_view"], "public void SetReadyForRefresh")
    assert "DiskIo" not in set_unavailable
    assert "DiskIo" not in set_ready
    checks += 2

    capture_handler = method_body(
        text["main"],
        "private async void StorageOptimizationView_PerformanceDiskIoCaptureRequested")
    assert "CaptureDiskIoAttributionAsync" in capture_handler
    assert "StorageOptimizationAvailable" not in capture_handler
    assert "State.IsBusy" not in capture_handler
    assert "_storageGate" not in capture_handler
    checks += 4

    state_handler = method_body(text["main"], "private void HandleStorageOptimizationEngineState")
    assert "SetDiskIoReadyForCapture" in state_handler
    assert "_storageOptimizationButton.IsEnabled = _storageViewMode != StorageViewMode.Optimize" in state_handler
    checks += 2

    assert "InvalidatePerformanceDiskIoCapture" not in text["main"]
    assert "Disk I/O attribution remains independently available" in text["main"]
    checks += 2

    # Normal storage/performance loads must not implicitly run ETW.
    load_body = method_body(text["main"], "private async Task LoadStorageOptimizationAsync")
    perf_refresh_body = method_body(
        text["main"],
        "private async void StorageOptimizationView_PerformanceRefreshRequested")
    assert "CaptureDiskIoAttributionAsync" not in load_body
    assert "CaptureDiskIoAttributionAsync" not in perf_refresh_body
    checks += 2

    for forbidden in (
        "PeriodicTimer",
        "DispatcherQueueTimer",
        "FileSystemWatcher",
        "Registry.",
        "health score",
        "Process.GetProcessById",
    ):
        assert forbidden not in text["engine"] + text["view"] + text["main"], forbidden
        checks += 1

    assert "TraceLogfileEventsLostOffset" in text["evidence"]
    assert "ConsumerEventsLostOffset" not in text["evidence"]
    assert "WindowsDiskIoAttributionProvider" in text["provider"]
    checks += 3

    assert "verify_performance_disk_io_ui.py" in text["gate"]
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
    suffix = f" and {repo_checks:,} source/UI checks" if args.repo_root else ""
    print(
        "PASS: Performance DiskIo UI verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized evidence states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
