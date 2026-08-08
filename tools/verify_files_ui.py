#!/usr/bin/env python3
"""Zero-Actions checks for FileOp's first indexed Files browsing surface."""
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


def _browser_sort_key(entry: tuple[bool, str, str]) -> tuple[int, str, str]:
    is_directory, name, path = entry
    return (0 if is_directory else 1, name.casefold(), path.casefold())


def check_properties(cases: int = 10_000) -> int:
    fixed = [
        (False, "z.txt", r"C:\Data\z.txt"),
        (True, "beta", r"C:\Data\beta"),
        (False, "A.txt", r"C:\Data\A.txt"),
        (True, "Alpha", r"C:\Data\Alpha"),
    ]
    ordered = sorted(fixed, key=_browser_sort_key)
    assert [item[1] for item in ordered] == ["Alpha", "beta", "A.txt", "z.txt"]

    assert _within(r"C:\Data", r"C:\Data")
    assert _within(r"C:\Data\Child\file.bin", r"c:\data\")
    assert not _within(r"C:\Database\file.bin", r"C:\Data")
    assert not _within(r"D:\Data\file.bin", r"C:\Data")

    rng = random.Random(20260808)
    alphabet = "abcdefghijklmnopqrstuvwxyz"
    for _ in range(cases):
        entries: list[tuple[bool, str, str]] = []
        for ordinal in range(rng.randint(1, 80)):
            is_directory = bool(rng.getrandbits(1))
            name = "".join(rng.choice(alphabet) for _ in range(rng.randint(1, 10)))
            if not is_directory:
                name += rng.choice([".txt", ".bin", ".jpg", ""])
            entries.append((is_directory, name, rf"C:\Root\{name}-{ordinal}"))
        ordered = sorted(entries, key=_browser_sort_key)
        seen_file = False
        previous_name: str | None = None
        previous_kind: bool | None = None
        for is_directory, name, _ in ordered:
            if not is_directory:
                seen_file = True
            else:
                assert not seen_file, "a directory sorted after a file"
            if previous_kind == is_directory and previous_name is not None:
                assert previous_name.casefold() <= name.casefold()
            previous_kind = is_directory
            previous_name = name

    return cases + 5


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

    paths = [app_path, main_path, view_xaml_path, view_code_path, main_xaml_path]
    missing = [str(path) for path in paths if not path.is_file()]
    if missing:
        raise FileNotFoundError("Missing repository files: " + ", ".join(missing))

    app = app_path.read_text(encoding="utf-8")
    main = main_path.read_text(encoding="utf-8")
    view_xaml = view_xaml_path.read_text(encoding="utf-8")
    view_code = view_code_path.read_text(encoding="utf-8")
    main_xaml = main_xaml_path.read_text(encoding="utf-8")

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
        (main, "private const int FilesDirectoryEntryLimit = 4_096;"),
        (main, "button.Content as string, \"Files\""),
        (main, "SearchView.Parent is not Grid contentGrid"),
        (main, "contentGrid.Children.Add(_filesView)"),
        (main, "SearchNavigationButton.Click += FilesOtherNavigationButton_Click"),
        (main, "StorageNavigationButton.Click += FilesOtherNavigationButton_Click"),
        (main, "_searchEngine.StateChanged += FilesEngine_StateChanged"),
        (main, "_storageGate.WaitAsync(_lifetimeCancellation.Token)"),
        (main, "_searchEngine.AnalyzeStorageAsync("),
        (main, "FilesDirectoryEntryLimit"),
        (main, "analysis.DirectEntryCount"),
        (main, "OrderByDescending(static entry => entry.IsDirectory)"),
        (main, "ThenBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)"),
        (main, "DesktopSearchMode.Native"),
        (main, "DesktopSearchMode.Fallback"),
        (main, "SetStorageViewMode(StorageViewMode.Folders)"),
        (main, "if (state.IsBusy)"),
        (main, "_filesAnalysis = null;"),
        (main, "var target = _filesCurrentPath;"),
        (view_code, "StorageDirectoryEntry entry"),
        (view_xaml, "Indexed contents"),
    ]
    for source, needle in required:
        assert needle in source, f"required Files invariant missing: {needle}"

    forbidden = [
        "Directory.Enumerate",
        "Directory.GetFiles",
        "Directory.GetDirectories",
        "EnumerateFileSystemEntries",
        "FileSystemWatcher",
    ]
    for needle in forbidden:
        assert needle not in main, f"Files browser must not rescan the filesystem: {needle}"

    assert "Content=\"Files\"" in main_xaml and "IsEnabled=\"False\"" in main_xaml
    assert "FilesView" not in main_xaml
    assert main.count("Interlocked.Increment(ref _filesGeneration)") >= 4
    assert "generation != Volatile.Read(ref _filesGeneration)" in main
    assert "_filesVisible" in main
    assert "omitted entries remain available through Search and future paging work" in view_code

    return len(required) + len(forbidden) + len(click_handlers) + 7


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--self-test-only", action="store_true")
    parser.add_argument("--cases", type=int, default=10_000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error("--cases must be greater than zero")

    properties = check_properties(args.cases)
    print(f"PASS indexed Files browser properties: {properties} checks")

    if not args.self_test_only:
        source = check_repository(args.repo_root.resolve())
        print(f"PASS indexed Files UI/source wiring: {source} checks")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ET.ParseError, ValueError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
