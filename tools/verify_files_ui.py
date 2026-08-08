#!/usr/bin/env python3
"""Zero-Actions checks for FileOp's exact paged Files UI."""
from __future__ import annotations

import argparse
import random
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def _normalize(path: str) -> str:
    value = path.replace("/", "\\").rstrip("\\")
    if len(value) == 2 and value[1] == ":":
        value += "\\"
    return value.casefold()


def _within(path: str, root: str) -> bool:
    normalized_path = _normalize(path)
    normalized_root = _normalize(root)
    if normalized_path == normalized_root:
        return True
    prefix = normalized_root if normalized_root.endswith("\\") else normalized_root + "\\"
    return normalized_path.startswith(prefix)


def _append_unique(existing: list[str], page: list[str]) -> list[str]:
    seen = {value.casefold() for value in existing}
    result = list(existing)
    for value in page:
        key = value.casefold()
        if key in seen:
            continue
        seen.add(key)
        result.append(value)
    return result


def check_properties(cases: int = 10_000) -> int:
    assert _within(r"C:\Data", r"C:\Data")
    assert _within(r"C:\Data\Child\file.bin", r"c:\data\")
    assert not _within(r"C:\Database\file.bin", r"C:\Data")
    assert not _within(r"D:\Data\file.bin", r"C:\Data")

    assert _append_unique([], [r"C:\A", r"C:\B"]) == [r"C:\A", r"C:\B"]
    assert _append_unique([r"C:\A"], [r"c:\a", r"C:\B"]) == [r"C:\A", r"C:\B"]

    rng = random.Random(20260808)
    checks = 6
    for case in range(cases):
        count = rng.randint(0, 500)
        reference = [rf"C:\Root\entry-{case}-{index}" for index in range(count)]
        page_size = rng.randint(1, 64)
        loaded: list[str] = []
        offset = 0
        while offset < len(reference):
            page = reference[offset:offset + page_size]
            if loaded and rng.random() < 0.3:
                page = [loaded[-1], *page]
            loaded = _append_unique(loaded, page)
            offset += page_size
        assert [value.casefold() for value in loaded] == [value.casefold() for value in reference]
        assert len({value.casefold() for value in loaded}) == len(loaded)
        checks += 2

    return checks


def _method_names(source: str) -> set[str]:
    return set(re.findall(
        r"\b(?:async\s+)?(?:void|Task|ValueTask|bool|string|int|long|double)\s+([A-Za-z_]\w*)\s*\(",
        source,
    ))


def check_repository(repo_root: Path) -> int:
    app_path = repo_root / "src/FileOp.App/App.xaml.cs"
    main_path = repo_root / "src/FileOp.App/MainWindow.Files.cs"
    view_xaml_path = repo_root / "src/FileOp.App/FilesView.xaml"
    view_code_path = repo_root / "src/FileOp.App/FilesView.xaml.cs"
    main_xaml_path = repo_root / "src/FileOp.App/MainWindow.xaml"
    engine_path = repo_root / "src/FileOp.App/DesktopSearchEngine.DirectoryBrowse.cs"

    paths = [app_path, main_path, view_xaml_path, view_code_path, main_xaml_path, engine_path]
    missing = [str(path) for path in paths if not path.is_file()]
    if missing:
        raise FileNotFoundError("Missing repository files: " + ", ".join(missing))

    app = app_path.read_text(encoding="utf-8")
    main = main_path.read_text(encoding="utf-8")
    view_xaml = view_xaml_path.read_text(encoding="utf-8")
    view_code = view_code_path.read_text(encoding="utf-8")
    main_xaml = main_xaml_path.read_text(encoding="utf-8")
    engine = engine_path.read_text(encoding="utf-8")

    ET.fromstring(view_xaml)
    click_handlers = set(re.findall(r'\b(?:Click|ItemClick)="([A-Za-z_]\w*)"', view_xaml))
    code_methods = _method_names(view_code)
    assert click_handlers <= code_methods, (
        "FilesView XAML references missing handlers: "
        + ", ".join(sorted(click_handlers - code_methods))
    )

    launch = app.index("var window = new MainWindow();")
    initialize = app.index("window.InitializeFilesFeature();")
    activate = app.index("window.Activate();")
    assert launch < initialize < activate

    required = [
        (main, "private const int FilesPageSize = 256;"),
        (main, "private readonly List<FileBrowserRow> _filesRows = [];"),
        (main, "private FileDirectoryBrowseCursor? _filesNextCursor;"),
        (main, "private int _filesLoadingGeneration;"),
        (main, "private bool _filesLoadedForSource;"),
        (main, "button.Content as string, \"Files\""),
        (main, "SearchView.Parent is not Grid contentGrid"),
        (main, "contentGrid.Children.Add(_filesView)"),
        (main, "_filesView.LoadMoreRequested += FilesView_LoadMoreRequested"),
        (main, "_filesView.LoadMoreRequested -= FilesView_LoadMoreRequested"),
        (main, "_searchEngine.StateChanged += FilesEngine_StateChanged"),
        (main, "_storageGate.WaitAsync(_lifetimeCancellation.Token)"),
        (main, "_searchEngine.BrowseDirectoryAsync("),
        (main, "FilesPageSize"),
        (main, "preserveRows: append"),
        (main, "ApplyFilesPage(page, root, append)"),
        (main, "new HashSet<string>(_filesRows.Select(static row => row.Path), StringComparer.OrdinalIgnoreCase)"),
        (main, "_filesNextCursor = page.NextCursor;"),
        (main, "_filesLoadedForSource = true;"),
        (main, "_filesLoadingGeneration == 0"),
        (main, "generation != Volatile.Read(ref _filesGeneration)"),
        (main, "DesktopSearchMode.Native"),
        (main, "DesktopSearchMode.Fallback"),
        (main, "SetStorageViewMode(StorageViewMode.Folders)"),
        (view_code, "public event EventHandler? LoadMoreRequested;"),
        (view_code, "public static FileBrowserRow FromRecord(FileRecord record)"),
        (view_code, "record.IsDirectory ? \"—\" : ByteFormatter.Format(record.Length)"),
        (view_code, "record.LastWriteTime.ToLocalTime().ToString(\"g\")"),
        (view_code, "FilesList.ItemsSource = null;"),
        (view_code, "The live directory changed while pages were being read."),
        (view_xaml, 'Content="Load more"'),
        (view_xaml, 'Text="Modified"'),
        (view_xaml, 'Text="{Binding ModifiedText}"'),
        (engine, "public async ValueTask<FileDirectoryBrowsePage> BrowseDirectoryAsync("),
    ]
    for source, needle in required:
        assert needle in source, f"required exact Files invariant missing: {needle}"

    forbidden_main = [
        "AnalyzeStorageAsync(",
        "StorageDirectoryAnalysis",
        "StorageDirectoryEntry",
        "FilesDirectoryEntryLimit",
        "OrderByDescending(",
        "ThenBy(",
        "Directory.Enumerate",
        "Directory.GetFiles",
        "Directory.GetDirectories",
        "EnumerateFileSystemEntries",
        "FileSystemWatcher",
    ]
    for needle in forbidden_main:
        assert needle not in main, f"exact Files coordinator must not use legacy/rescan path: {needle}"

    forbidden_view = [
        "StorageDirectoryEntry",
        "Indexed contents",
        "omitted entries",
        "bounded",
    ]
    for needle in forbidden_view:
        assert needle not in view_code + view_xaml, f"exact Files view still exposes legacy bounded semantics: {needle}"

    assert "Content=\"Files\"" in main_xaml and "IsEnabled=\"False\"" in main_xaml
    assert "FilesView" not in main_xaml
    assert main.count("Interlocked.Increment(ref _filesGeneration)") >= 4
    assert "_filesVisible" in main
    assert view_xaml.count("LoadMoreButton") >= 1

    return len(required) + len(forbidden_main) + len(forbidden_view) + len(click_handlers) + 6


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--self-test-only", action="store_true")
    parser.add_argument("--cases", type=int, default=10_000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error("--cases must be greater than zero")

    properties = check_properties(args.cases)
    print(f"PASS exact paged Files UI properties: {properties} checks")

    if not args.self_test_only:
        source = check_repository(args.repo_root.resolve())
        print(f"PASS exact paged Files UI/source wiring: {source} checks")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ET.ParseError, ValueError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
