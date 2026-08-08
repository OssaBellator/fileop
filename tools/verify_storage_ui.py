#!/usr/bin/env python3
"""Offline structural/property checks for FileOp's WinUI Storage view.

No third-party packages, .NET SDK, Windows runtime, or GitHub Actions required.
Run from the repository root:

    python tools/verify_storage_ui.py

The structural checks inspect the actual XAML/code-behind when those files exist.
The property tests always run and mirror the current binary treemap/path/category logic.
"""
from __future__ import annotations

import argparse
import math
import ntpath
import random
import re
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path

XAML_NS = "http://schemas.microsoft.com/winfx/2006/xaml"
EVENT_ATTRIBUTES = {
    "Click",
    "TextChanged",
    "ItemClick",
    "SizeChanged",
    "Tapped",
    "Loaded",
}
REQUIRED_STORAGE_NAMES = {
    "StorageView",
    "StorageScopeText",
    "StorageFoldersButton",
    "StorageTypesButton",
    "StorageUpButton",
    "StorageTypesUpButton",
    "StoragePathText",
    "StorageRefreshButton",
    "StorageTypesRefreshButton",
    "StorageLogicalText",
    "StorageAllocatedText",
    "StorageFilesText",
    "StorageAliasesText",
    "StorageFolderPanel",
    "StorageTreemapModeText",
    "StorageTreemapCanvas",
    "StorageTreemapEmptyText",
    "StorageList",
    "StorageTypesPanel",
    "StorageCategoryStatusText",
    "StorageCategoryList",
    "StorageTypesList",
}


@dataclass(frozen=True)
class _Item:
    weight: int


@dataclass(frozen=True)
class _Rect:
    index: int
    x: float
    y: float
    width: float
    height: float


def _layout_treemap(
    items: list[_Item],
    start: int,
    count: int,
    x: float,
    y: float,
    width: float,
    height: float,
    output: list[_Rect],
) -> None:
    if count <= 0 or width <= 0 or height <= 0:
        return
    if count == 1:
        output.append(_Rect(start, x, y, width, height))
        return

    total = sum(items[i].weight for i in range(start, start + count))
    if total <= 0:
        return

    target = total / 2.0
    left_weight = 0
    left_count = 0
    while left_count < count - 1:
        candidate = items[start + left_count].weight
        if left_count > 0 and left_weight + candidate > target:
            break
        left_weight += candidate
        left_count += 1

    if left_count == 0:
        left_count = 1
        left_weight = items[start].weight

    ratio = left_weight / float(total)
    if width >= height:
        left_width = width * ratio
        _layout_treemap(items, start, left_count, x, y, left_width, height, output)
        _layout_treemap(
            items,
            start + left_count,
            count - left_count,
            x + left_width,
            y,
            width - left_width,
            height,
            output,
        )
    else:
        top_height = height * ratio
        _layout_treemap(items, start, left_count, x, y, width, top_height, output)
        _layout_treemap(
            items,
            start + left_count,
            count - left_count,
            x,
            y + top_height,
            width,
            height - top_height,
            output,
        )


def _rectangles_overlap(left: _Rect, right: _Rect, epsilon: float = 1e-8) -> bool:
    return not (
        left.x + left.width <= right.x + epsilon
        or right.x + right.width <= left.x + epsilon
        or left.y + left.height <= right.y + epsilon
        or right.y + right.height <= left.y + epsilon
    )


def check_treemap_properties() -> int:
    rng = random.Random(20260808)
    cases = 0
    for count in [*range(1, 65), 100, 256, 1000]:
        for trial in range(30):
            weights = sorted((rng.randint(1, 10**12) for _ in range(count)), reverse=True)
            if trial == 0 and count >= 2:
                weights[0] = 10**15
                weights[-1] = 1
                weights.sort(reverse=True)

            items = [_Item(weight) for weight in weights]
            width = rng.uniform(64, 4096)
            height = rng.uniform(64, 2160)
            rectangles: list[_Rect] = []
            _layout_treemap(items, 0, count, 0, 0, width, height, rectangles)

            assert len(rectangles) == count, (count, len(rectangles))
            canvas_area = width * height
            total_weight = sum(weights)
            assert math.isclose(
                sum(rect.width * rect.height for rect in rectangles),
                canvas_area,
                rel_tol=1e-11,
                abs_tol=1e-5,
            )

            for rect in rectangles:
                assert rect.x >= -1e-8 and rect.y >= -1e-8
                assert rect.x + rect.width <= width + 1e-7
                assert rect.y + rect.height <= height + 1e-7
                expected_fraction = weights[rect.index] / total_weight
                actual_fraction = (rect.width * rect.height) / canvas_area
                assert math.isclose(
                    actual_fraction,
                    expected_fraction,
                    rel_tol=1e-9,
                    abs_tol=1e-12,
                ), (count, rect.index, expected_fraction, actual_fraction)

            if count <= 100:
                for left_index, left in enumerate(rectangles):
                    for right in rectangles[left_index + 1 :]:
                        assert not _rectangles_overlap(left, right)
            cases += 1

    items = [_Item(999), _Item(1)]
    rectangles = []
    _layout_treemap(items, 0, 2, 0, 0, 1000, 500, rectangles)
    fractions = [rect.width * rect.height / 500_000 for rect in rectangles]
    assert math.isclose(fractions[0], 0.999, rel_tol=1e-12)
    assert math.isclose(fractions[1], 0.001, rel_tol=1e-12)
    return cases + 1


def _prepare_treemap_weights(
    returned_weights: list[int],
    root_weight: int,
    direct_entry_count: int,
    maximum_tiles: int = 48,
) -> tuple[list[int], int]:
    weighted = sorted((weight for weight in returned_weights if weight > 0), reverse=True)
    if not weighted:
        return [], 0

    individual_count = min(len(weighted), maximum_tiles)
    if direct_entry_count > individual_count:
        individual_count = min(len(weighted), maximum_tiles - 1)

    tiles = weighted[:individual_count]
    other_entry_count = max(0, direct_entry_count - individual_count)
    if other_entry_count > 0:
        other_weight = max(0, root_weight - sum(tiles))
        if other_weight > 0:
            tiles.append(other_weight)
    return tiles, other_entry_count


def check_treemap_coverage() -> int:
    cases = 0

    tiles, other_count = _prepare_treemap_weights([60, 30, 10], 100, 3)
    assert tiles == [60, 30, 10]
    assert other_count == 0
    cases += 1

    weights = list(range(100, 0, -1))
    tiles, other_count = _prepare_treemap_weights(weights, sum(weights), 100)
    assert len(tiles) == 48
    assert other_count == 53
    assert sum(tiles) == sum(weights)
    cases += 1

    returned = [10] * 256
    full_root_weight = 10 * 1000
    tiles, other_count = _prepare_treemap_weights(returned, full_root_weight, 1000)
    assert len(tiles) == 48
    assert other_count == 953
    assert sum(tiles) == full_root_weight
    cases += 1

    tiles, other_count = _prepare_treemap_weights([128, 0], 128, 2)
    assert tiles == [128]
    assert other_count == 1
    assert sum(tiles) == 128
    cases += 1

    return cases


def _assert_exact_categories(
    categories: list[tuple[str, int, int]],
    root_weight: int,
    complete_type_count: int,
    displayed_type_count: int,
) -> None:
    assert 0 <= displayed_type_count <= complete_type_count
    assert sum(max(0, weight) for _, weight, _ in categories) == root_weight
    assert sum(type_count for _, _, type_count in categories) == complete_type_count
    assert len({name for name, _, _ in categories}) == len(categories)


def check_category_coverage() -> int:
    cases = 0

    _assert_exact_categories(
        [("Images", 75, 2), ("Documents", 25, 1)],
        root_weight=100,
        complete_type_count=3,
        displayed_type_count=3,
    )
    cases += 1

    # Only one extension is displayed, but exact categories still cover all five types.
    _assert_exact_categories(
        [("Images", 400, 2), ("Documents", 300, 1), ("Data", 300, 2)],
        root_weight=1000,
        complete_type_count=5,
        displayed_type_count=1,
    )
    cases += 1

    # A category may legitimately carry zero physical bytes when its names are aliases.
    _assert_exact_categories(
        [("Data", 128, 1), ("Images", 0, 1)],
        root_weight=128,
        complete_type_count=2,
        displayed_type_count=1,
    )
    cases += 1

    _assert_exact_categories([], root_weight=0, complete_type_count=0, displayed_type_count=0)
    cases += 1

    return cases


def _normalize_windows_path(path: str) -> str:
    normalized = ntpath.normpath(path)
    if len(normalized) == 3 and normalized[1:] == ":\\":
        return normalized[:2]
    return normalized.rstrip("\\/")


def _is_path_within_root(path: str, root: str) -> bool:
    normalized_path = _normalize_windows_path(path)
    normalized_root = _normalize_windows_path(root)
    if normalized_path.casefold() == normalized_root.casefold():
        return True
    return normalized_path.casefold().startswith((normalized_root + "\\").casefold())


def check_path_properties() -> int:
    cases = [
        (r"C:\Users\Alice", r"C:\Users\Alice", True),
        (r"C:\Users\Alice\Docs", r"C:\Users\Alice", True),
        (r"C:\Users\ALICE\Docs", r"c:\users\alice", True),
        (r"C:\Users\Alice2", r"C:\Users\Alice", False),
        (r"C:\Users\Alice\..\Bob", r"C:\Users\Alice", False),
        (r"C:\Windows", "C:\\", True),
        (r"D:\Data", "C:\\", False),
        (r"C:\Data2", r"C:\Data", False),
        (r"C:\Data\Sub\..\Other", r"C:\Data", True),
    ]
    for path, root, expected in cases:
        actual = _is_path_within_root(path, root)
        assert actual == expected, (path, root, expected, actual)
    return len(cases)


def check_repository(repo_root: Path) -> tuple[int, int]:
    app_root = repo_root / "src" / "FileOp.App"
    xaml_path = app_root / "MainWindow.xaml"
    required_code_paths = [
        app_root / "MainWindow.xaml.cs",
        app_root / "MainWindow.StorageTypes.cs",
        app_root / "DesktopSearchEngine.cs",
        app_root / "DesktopSearchEngine.StorageTypes.cs",
    ]
    missing_files = [path for path in [xaml_path, *required_code_paths] if not path.is_file()]
    if missing_files:
        raise FileNotFoundError(
            "Run this script from a FileOp checkout, or pass --repo-root. "
            f"Missing {', '.join(str(path) for path in missing_files)}."
        )

    xaml_text = xaml_path.read_text(encoding="utf-8")
    code_paths = sorted(app_root.glob("MainWindow*.cs"))
    engine_paths = sorted(app_root.glob("DesktopSearchEngine*.cs"))
    code_text = "\n".join(path.read_text(encoding="utf-8") for path in code_paths)
    engine_text = "\n".join(path.read_text(encoding="utf-8") for path in engine_paths)
    root = ET.fromstring(xaml_text)

    names: set[str] = set()
    handlers: set[str] = set()
    x_name = f"{{{XAML_NS}}}Name"
    for element in root.iter():
        name = element.attrib.get(x_name)
        if name:
            assert name not in names, f"Duplicate x:Name: {name}"
            names.add(name)
        for attribute, value in element.attrib.items():
            local_name = attribute.rsplit("}", 1)[-1]
            if local_name in EVENT_ATTRIBUTES:
                handlers.add(value)

    missing_names = REQUIRED_STORAGE_NAMES - names
    assert not missing_names, f"Missing required Storage x:Name values: {sorted(missing_names)}"

    method_names = set(
        re.findall(
            r"\b(?:private|internal|public|protected)\s+(?:static\s+)?(?:async\s+)?"
            r"[\w<>?\[\],.]+\s+(\w+)\s*\(",
            code_text,
        )
    )
    missing_handlers = handlers - method_names
    assert not missing_handlers, f"XAML handlers missing in code-behind: {sorted(missing_handlers)}"

    assert "var ratio = leftWeight / (double)total;" in code_text
    assert "Math.Clamp(leftWeight / (double)total" not in code_text
    assert "var rootWeight = analysis.AllocatedBytes ?? analysis.LogicalBytes;" in code_text
    assert "analysis.DirectEntryCount - individualTileCount" in code_text
    assert "rootWeight - representedWeight" in code_text

    assert 'x:Name="StorageNavigationButton"' in xaml_text
    assert 'x:Name="StorageFoldersButton"' in xaml_text
    assert 'x:Name="StorageTypesButton"' in xaml_text
    assert "_fallbackIndex.AnalyzeDirectoryAsync(" in engine_text
    assert "session.Client.AnalyzeStorageAsync(" in engine_text
    assert "_fallbackIndex.AnalyzeFileTypesAsync(" in engine_text
    assert "session.Client.AnalyzeStorageTypesAsync(" in engine_text
    assert "AnalyzeStorageFileTypesAsync(" in engine_text
    assert "foreach (var category in analysis.Categories)" in code_text
    assert "Exact category totals" in code_text
    assert "Other {omittedTypeCount:N0} types (not returned)" not in code_text
    assert "omitted remainder is left unclassified" not in code_text
    assert "Interlocked.Increment(ref _storageGeneration);" in code_text
    assert "Interlocked.Increment(ref _storageTypeGeneration);" in code_text

    return len(names), len(handlers)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument(
        "--self-test-only",
        action="store_true",
        help="Run pure property tests without requiring a FileOp checkout.",
    )
    args = parser.parse_args()

    treemap_cases = check_treemap_properties()
    coverage_cases = check_treemap_coverage()
    category_cases = check_category_coverage()
    path_cases = check_path_properties()
    print(f"PASS treemap properties: {treemap_cases} cases")
    print(f"PASS treemap coverage: {coverage_cases} cases")
    print(f"PASS exact category coverage: {category_cases} cases")
    print(f"PASS path containment: {path_cases} cases")

    if not args.self_test_only:
        names, handlers = check_repository(args.repo_root.resolve())
        print(f"PASS XAML/code-behind structure: {names} named elements, {handlers} handlers")

    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, ET.ParseError, FileNotFoundError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
