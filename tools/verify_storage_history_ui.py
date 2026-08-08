#!/usr/bin/env python3
"""Zero-Actions checks for FileOp's native Storage History scheduler and UI."""
from __future__ import annotations

import argparse
import datetime as dt
import random
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

UTC = dt.timezone.utc


def _bucket(timestamp: dt.datetime) -> dt.datetime:
    value = timestamp.astimezone(UTC)
    return value.replace(minute=0, second=0, microsecond=0)


def _capture_due(
    now: dt.datetime,
    last_successful_bucket: dt.datetime | None,
    next_attempt: dt.datetime | None,
) -> bool:
    now_utc = now.astimezone(UTC)
    return (
        _bucket(now) != last_successful_bucket
        and (next_attempt is None or now_utc >= next_attempt)
    )


def check_scheduler_properties(cases: int = 10_000) -> int:
    fixed = dt.datetime(2026, 8, 8, 16, 56, 23, tzinfo=dt.timezone(dt.timedelta(hours=10)))
    fixed_bucket = dt.datetime(2026, 8, 8, 6, 0, tzinfo=UTC)
    assert _bucket(fixed) == fixed_bucket
    assert _capture_due(fixed, None, None)
    assert not _capture_due(fixed, fixed_bucket, None)
    assert not _capture_due(fixed, None, fixed.astimezone(UTC) + dt.timedelta(minutes=1))
    assert _capture_due(fixed, None, fixed.astimezone(UTC) - dt.timedelta(seconds=1))

    rng = random.Random(20260808)
    for _ in range(cases):
        offset_minutes = rng.randint(-48, 56) * 15
        offset = dt.timezone(dt.timedelta(minutes=offset_minutes))
        local = dt.datetime(
            2026,
            rng.randint(1, 12),
            rng.randint(1, 28),
            rng.randint(0, 23),
            rng.randint(0, 59),
            rng.randint(0, 59),
            tzinfo=offset,
        )
        bucket = _bucket(local)
        assert bucket.minute == 0 and bucket.second == 0 and bucket.microsecond == 0
        assert bucket.tzinfo == UTC
        assert not _capture_due(local, bucket, None)
        assert _capture_due(local + dt.timedelta(hours=1), bucket, None)

    return cases + 4


def _timeline_uses_physical(allocated_values: list[int | None]) -> bool:
    return bool(allocated_values) and all(value is not None for value in allocated_values)


def _growth_uses_physical(older: int | None, newer: int | None) -> bool:
    return older is not None and newer is not None


def check_presentation_properties() -> int:
    assert _timeline_uses_physical([100, 200, 300])
    assert not _timeline_uses_physical([100, None, 300])
    assert not _timeline_uses_physical([])
    assert _growth_uses_physical(100, 200)
    assert not _growth_uses_physical(None, 200)
    assert not _growth_uses_physical(100, None)

    logical = [100, 200, 300]
    allocated = [128, None, 384]
    use_physical = _timeline_uses_physical(allocated)
    weights = [allocated[i] if use_physical else logical[i] for i in range(3)]
    assert weights == logical
    return 7


def _method_names(source: str) -> set[str]:
    return set(re.findall(r"\b(?:async\s+)?(?:void|Task|ValueTask|bool|string|int|long|double)\s+([A-Za-z_]\w*)\s*\(", source))


def check_repository(repo_root: Path) -> int:
    engine_path = repo_root / "src/FileOp.App/DesktopSearchEngine.StorageHistory.cs"
    main_path = repo_root / "src/FileOp.App/MainWindow.StorageHistory.cs"
    modes_path = repo_root / "src/FileOp.App/MainWindow.StorageTypes.cs"
    view_xaml_path = repo_root / "src/FileOp.App/StorageHistoryView.xaml"
    view_code_path = repo_root / "src/FileOp.App/StorageHistoryView.xaml.cs"
    main_xaml_path = repo_root / "src/FileOp.App/MainWindow.xaml"

    paths = [engine_path, main_path, modes_path, view_xaml_path, view_code_path, main_xaml_path]
    missing = [str(path) for path in paths if not path.is_file()]
    if missing:
        raise FileNotFoundError("Missing repository files: " + ", ".join(missing))

    engine = engine_path.read_text(encoding="utf-8")
    main = main_path.read_text(encoding="utf-8")
    modes = modes_path.read_text(encoding="utf-8")
    view_xaml = view_xaml_path.read_text(encoding="utf-8")
    view_code = view_code_path.read_text(encoding="utf-8")
    main_xaml = main_xaml_path.read_text(encoding="utf-8")

    ET.fromstring(view_xaml)
    click_handlers = set(re.findall(r'\bClick="([A-Za-z_]\w*)"', view_xaml))
    code_methods = _method_names(view_code)
    assert click_handlers <= code_methods, (
        "StorageHistoryView XAML references missing handlers: "
        + ", ".join(sorted(click_handlers - code_methods))
    )

    required = [
        (engine, "internal DesktopSearchEngine()"),
        (engine, "StateChanged += StorageHistoryCapture_StateChanged"),
        (engine, "HistoryPostSyncYieldDelay"),
        (engine, "StorageHistoryCaptured?.Invoke"),
        (engine, "IndexingStorageHistoryCaptureRequest"),
        (engine, "IndexingStorageHistoryQueryRequest"),
        (engine, "State.Mode == DesktopSearchMode.Native"),
        (main, "_searchEngine.StorageHistoryCaptured += SearchEngine_StorageHistoryCaptured"),
        (main, "_storageHistoryLoadedForSource"),
        (main, "_storageHistoryLoadingGeneration"),
        (main, "_storageHistoryView = new StorageHistoryView"),
        (main, "StorageViewMode.History"),
        (main, "fallback snapshot is not mixed"),
        (modes, "InitializeStorageHistoryView()"),
        (modes, "StorageViewMode.History"),
        (modes, "await LoadStorageHistoryAsync(forceRefresh)"),
        (view_code, "chronological.All(static snapshot => snapshot.AllocatedBytes.HasValue)"),
        (view_code, "StorageHistoryDelta.Between"),
        (view_xaml, "Usage timeline"),
        (view_xaml, "What grew?"),
    ]
    for text, needle in required:
        assert needle in text, f"required history UI invariant missing: {needle}"

    assert engine.count("WaitAsync(0, _lifetimeCancellation.Token)") >= 2
    query_start = engine.index("public async ValueTask<IReadOnlyList<StorageHistorySnapshot>> GetStorageHistoryAsync")
    query_text = engine[query_start:]
    assert "_searchOperationGate.WaitAsync(_lifetimeCancellation.Token)" in query_text
    assert "_nativeOperationGate.WaitAsync(_lifetimeCancellation.Token)" in query_text

    capture_start = engine.index("new IndexingStorageHistoryCaptureRequest(")
    capture_end = engine.index("_lifetimeCancellation.Token", capture_start)
    capture_text = engine[capture_start:capture_end]
    assert capture_text.count("volume.RootPath") == 2

    assert "TryCaptureStorageHistoryAsync" not in main
    assert "StorageHistoryCapture_StateChanged" in engine

    assert "StorageHistoryView" not in main_xaml
    assert "StorageFolderPanel.Parent is Grid" in main
    assert "StorageFoldersButton.Parent is StackPanel" in main
    assert "private StorageHistoryView _storageHistoryView = null!;" in main
    assert "private Button _storageHistoryButton = null!;" in main

    # A loaded-empty source is cached, and recurring native sync status updates
    # cannot reset the view or stop an active history load.
    assert "if (sourceChanged)" in main
    assert "_storageHistoryLoadedForSource = true;" in main
    assert "if (_storageHistoryLoadingGeneration == 0)" in main
    assert "sourceChanged || !_storageHistoryLoadedForSource" in main

    assert "Capture now" not in view_xaml
    assert 'Content="Refresh history"' in view_xaml

    return len(required) + 17 + len(click_handlers)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--self-test-only", action="store_true")
    parser.add_argument("--cases", type=int, default=10_000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error("--cases must be greater than zero")

    scheduler = check_scheduler_properties(args.cases)
    presentation = check_presentation_properties()
    print(f"PASS storage history scheduler properties: {scheduler} cases")
    print(f"PASS storage history presentation properties: {presentation} checks")

    if not args.self_test_only:
        source = check_repository(args.repo_root.resolve())
        print(f"PASS storage history UI/source wiring: {source} checks")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ET.ParseError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
