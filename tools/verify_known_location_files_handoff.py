#!/usr/bin/env python3
"""Verify non-destructive known-location review handoff to Files without Actions."""
from __future__ import annotations

import argparse
import ntpath
import random
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def canon(path: str) -> str:
    value = ntpath.normpath(path)
    drive, tail = ntpath.splitdrive(value)
    if not drive or not tail.startswith("\\"):
        raise ValueError(path)
    return ntpath.normcase(value)


def same(left: str, right: str) -> bool:
    try:
        return canon(left) == canon(right)
    except ValueError:
        return False


def within(path: str, root: str) -> bool:
    try:
        path_value = canon(path)
        root_value = canon(root)
        return ntpath.commonpath([path_value, root_value]) == root_value
    except ValueError:
        return False


def resolve_parent(
    active_root: str,
    review_root: str,
    candidate: str,
    candidates: set[str],
    *,
    native: bool,
    busy: bool,
    optimize_ready: bool,
    disk_io_active: bool,
    verification_active: bool,
    storage_busy: bool,
) -> str | None:
    if (
        not native
        or busy
        or not optimize_ready
        or disk_io_active
        or verification_active
        or storage_busy
        or not same(active_root, review_root)
        or not any(same(candidate, item) for item in candidates)
        or not within(candidate, active_root)
    ):
        return None
    parent = ntpath.dirname(ntpath.normpath(candidate))
    return parent if parent and within(parent, active_root) else None


def choose_tab(existing_paths: list[str | None], parent: str) -> tuple[int, bool]:
    for index, value in enumerate(existing_paths):
        if value is not None and same(value, parent):
            return index, False
    return len(existing_paths), True


def may_apply_selection(
    *,
    same_review: bool,
    same_root: bool,
    native: bool,
    busy: bool,
    optimize_ready: bool,
) -> bool:
    return same_review and same_root and native and not busy and optimize_ready


def run_model(cases: int, seed: int) -> int:
    rng = random.Random(seed)
    checks = 0
    fixed = [
        ("C:\\", "C:\\", "C:\\Users\\A\\Downloads\\old.zip", True),
        ("C:\\", "D:\\", "C:\\Users\\A\\Downloads\\old.zip", False),
        ("C:\\Data", "C:\\Data", "C:\\Database\\old.zip", False),
        ("C:\\Data", "C:\\Data", "C:\\Data\\old.zip", True),
    ]
    for active, review, candidate, expected in fixed:
        result = resolve_parent(
            active,
            review,
            candidate,
            {candidate},
            native=True,
            busy=False,
            optimize_ready=True,
            disk_io_active=False,
            verification_active=False,
            storage_busy=False,
        )
        assert (result is not None) == expected
        checks += 1

    tab_index, created = choose_tab([r"C:\One", r"C:\Two"], r"c:\two")
    assert tab_index == 1 and not created
    tab_index, created = choose_tab([r"C:\One"], r"C:\Three")
    assert tab_index == 1 and created
    assert may_apply_selection(
        same_review=True,
        same_root=True,
        native=True,
        busy=False,
        optimize_ready=True,
    )
    assert not may_apply_selection(
        same_review=False,
        same_root=True,
        native=True,
        busy=False,
        optimize_ready=True,
    )
    checks += 4

    for index in range(cases):
        drive = rng.choice("CDE")
        root = f"{drive}:\\Indexed"
        good = f"{root}\\Users\\U{index % 97}\\Downloads\\candidate-{index}.zip"
        review_root = root if rng.random() < 0.84 else f"{rng.choice('FGH')}:\\Indexed"
        candidate = good if rng.random() < 0.90 else f"{drive}:\\IndexedElsewhere\\candidate-{index}.zip"
        listed = rng.random() < 0.91
        native = rng.random() < 0.94
        busy = rng.random() < 0.04
        optimize_ready = rng.random() < 0.96
        disk_io_active = rng.random() < 0.03
        verification_active = rng.random() < 0.03
        storage_busy = rng.random() < 0.05
        result = resolve_parent(
            root,
            review_root,
            candidate,
            {candidate} if listed else {good + ".other"},
            native=native,
            busy=busy,
            optimize_ready=optimize_ready,
            disk_io_active=disk_io_active,
            verification_active=verification_active,
            storage_busy=storage_busy,
        )
        expected = (
            native
            and not busy
            and optimize_ready
            and not disk_io_active
            and not verification_active
            and not storage_busy
            and same(root, review_root)
            and listed
            and within(candidate, root)
        )
        assert (result is not None) == expected
        checks += 1
        if result is not None:
            assert within(result, root)
            assert same(result, ntpath.dirname(candidate))
            checks += 2

            existing = [
                f"{root}\\Other{slot}"
                for slot in range(rng.randint(0, 4))
            ]
            insert_match = rng.random() < 0.55
            if insert_match:
                match_at = rng.randrange(len(existing) + 1)
                existing.insert(match_at, result.swapcase())
            chosen, created = choose_tab(existing, result)
            assert created is (not insert_match)
            if insert_match:
                assert chosen == match_at
            else:
                assert chosen == len(existing)
            checks += 2

        page_count = rng.randint(1, 5)
        target_page = rng.randrange(page_count + 1)
        target = f"{root}\\Dir\\target-{index}.bin"
        loaded: list[str] = []
        for page in range(page_count):
            rows = [f"{root}\\Dir\\p{page}-{item}-{index}.bin" for item in range(rng.randint(1, 8))]
            if page == target_page:
                rows.insert(rng.randrange(len(rows) + 1), target)
            loaded.extend(rows)
            selected = [row for row in loaded if same(row, target)]
            assert len(selected) <= 1
            assert bool(selected) == (target_page <= page)
            checks += 2

        same_review_after_load = rng.random() < 0.93
        same_root_after_load = rng.random() < 0.96
        native_after_load = rng.random() < 0.97
        busy_after_load = rng.random() < 0.04
        optimize_ready_after_load = rng.random() < 0.96
        can_select = may_apply_selection(
            same_review=same_review_after_load,
            same_root=same_root_after_load,
            native=native_after_load,
            busy=busy_after_load,
            optimize_ready=optimize_ready_after_load,
        )
        assert can_select == (
            same_review_after_load
            and same_root_after_load
            and native_after_load
            and not busy_after_load
            and optimize_ready_after_load
        )
        if not same_review_after_load or not same_root_after_load or not native_after_load:
            assert not can_select
        checks += 2
    return checks


def require(text: str, needle: str, label: str) -> int:
    if needle not in text:
        raise AssertionError(f"missing {label}: {needle}")
    return 1


def forbid(text: str, needle: str, label: str) -> int:
    if needle in text:
        raise AssertionError(f"forbidden {label}: {needle}")
    return 1


def run_source_guards(root: Path) -> int:
    checks = 0
    app = (root / "src/FileOp.App/App.xaml.cs").read_text(encoding="utf-8")
    xaml_path = root / "src/FileOp.App/StorageKnownLocationReviewView.xaml"
    xaml = xaml_path.read_text(encoding="utf-8")
    view = (root / "src/FileOp.App/StorageKnownLocationReviewView.ReviewHandoff.cs").read_text(encoding="utf-8")
    pane = (root / "src/FileOp.App/FilesPaneView.ReviewHandoff.cs").read_text(encoding="utf-8")
    coordinator = (root / "src/FileOp.App/MainWindow.FilesReviewHandoff.cs").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")
    docs = (root / "docs/known-location-files-handoff.md").read_text(encoding="utf-8")

    ET.parse(xaml_path)
    checks += 1
    required = [
        (xaml, 'Content="Review in Files"', "review action"),
        (xaml, 'Click="ReviewInFilesButton_Click"', "click handler"),
        (xaml, "does not prepare or queue an operation", "UI safety wording"),
        (view, "StorageKnownLocationCandidateRow row", "typed candidate"),
        (view, "window.ReviewPathInFilesAsync(row.Path)", "handoff routing"),
        (app, "internal MainWindow? MainWindow", "internal window routing"),
        (app, "MainWindow = window;", "window assignment"),
        (coordinator, "_performanceDiskIoCaptureActive || _storageSameSizeVerificationActive", "measurement isolation"),
        (coordinator, "!await _storageGate.WaitAsync(0)", "non-blocking Storage gate acquisition"),
        (coordinator, "_storageGate.Release();", "Storage gate release"),
        (coordinator, "_searchEngine.State.Mode != DesktopSearchMode.Native", "native evidence"),
        (coordinator, "PathsEqual(root, review.ActiveVolumeRootPath)", "review root revalidation"),
        (coordinator, ".SelectMany(static location => location.Candidates)", "candidate membership"),
        (coordinator, "PathsEqual(candidate.Path, requestedPath)", "candidate path binding"),
        (coordinator, "IsPathWithinRoot(candidate.Path, root)", "candidate containment"),
        (coordinator, "IsPathWithinRoot(parentPath, root)", "parent containment"),
        (coordinator, "SetStorageViewMode(StorageViewMode.Folders)", "Optimize exit"),
        (coordinator, "_leftFilesPane.Tabs.FirstOrDefault(existing =>", "matching-tab lookup"),
        (coordinator, "PathsEqual(existing.CurrentPath, parentPath)", "matching-tab path"),
        (coordinator, "if (tab is null)", "create-only-when-needed"),
        (coordinator, "CreateFilesTab(parentPath)", "new Files tab fallback"),
        (coordinator, "_leftFilesPane.Tabs.Add(tab)", "left-pane handoff"),
        (coordinator, "LoadFilesDirectoryAsync(", "existing paged Files loader"),
        (coordinator, "ReferenceEquals(review, _storageKnownLocationReview)", "post-load review identity"),
        (coordinator, "var currentRoot = _searchEngine.StorageRootPath", "post-load root capture"),
        (coordinator, "!PathsEqual(currentRoot, root)", "post-load root comparison"),
        (coordinator, "did not auto-select stale review evidence", "stale-selection suppression"),
        (coordinator, "SetReviewSelectionHint(candidatePath)", "selection hint"),
        (coordinator, "not in the currently loaded page", "page disclosure"),
        (pane, "_selectedPaths.Add(path)", "path-bound selection"),
        (pane, "RestoreSelection();", "selection restoration"),
        (pane, "StringComparison.OrdinalIgnoreCase", "Windows path equality"),
        (gate, "verify_known_location_files_handoff.py --repo-root $repoRoot --cases 50000", "offline gate wiring"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8"),
        (docs, "does not auto-page", "no auto-paging documentation"),
        (docs, "does not prepare or queue", "operation-plan boundary"),
        (docs, "does not authorize or run cleanup", "cleanup boundary"),
    ]
    for text, needle, label in required:
        checks += require(text, needle, label)

    release_at = coordinator.index("_storageGate.Release();")
    load_at = coordinator.index("await LoadFilesDirectoryAsync(")
    selection_at = coordinator.index("SetReviewSelectionHint(candidatePath)")
    freshness_at = coordinator.index("ReferenceEquals(review, _storageKnownLocationReview)")
    if release_at >= load_at:
        raise AssertionError("Storage gate must be released before Files loader reacquires it")
    if freshness_at <= load_at or freshness_at >= selection_at:
        raise AssertionError("post-load source freshness must be checked before applying the selection hint")
    checks += 2

    combined = coordinator + "\n" + pane + "\n" + view
    for token in (
        "Directory.EnumerateFiles",
        "Directory.EnumerateDirectories",
        "Directory.EnumerateFileSystemEntries",
        "Directory.GetFiles",
        "Directory.GetDirectories",
        "File.Delete(",
        "Directory.Delete(",
        "File.Move(",
        "Directory.Move(",
        "File.Copy(",
        "DeleteFileW",
        "MoveFileEx",
    ):
        checks += forbid(combined, token, "scan/mutation API")
    checks += forbid(coordinator, "while (", "automatic page-draining loop")
    checks += forbid(coordinator, "FilesPane_LoadMoreRequested", "implicit Load more")
    checks += forbid(
        coordinator,
        "Interlocked.Increment(ref _storageOptimizationGeneration)",
        "duplicate Optimize invalidation",
    )
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0xF11E0)
    parser.add_argument("--self-test-only", action="store_true")
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")
    model = run_model(args.cases, args.seed)
    source = 0
    if not args.self_test_only:
        if args.repo_root is None:
            parser.error("--repo-root is required unless --self-test-only is used")
        source = run_source_guards(args.repo_root.resolve())
    print(
        f"PASS: known-location Files handoff verifier: {model + source:,} checks "
        f"({model:,} model, {source:,} source) across {args.cases:,} randomized cases"
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except AssertionError as error:
        print(f"FAIL: {error}", file=sys.stderr)
        raise SystemExit(1)
