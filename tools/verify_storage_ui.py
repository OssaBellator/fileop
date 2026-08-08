#!/usr/bin/env python3
"""Offline structural/property checks for FileOp's WinUI Storage view.

No third-party packages, .NET SDK, Windows runtime, or GitHub Actions required.
Run from the repository root:

    python tools/verify_storage_ui.py

The structural checks inspect the actual XAML/code-behind when those files exist.
The property tests always run and mirror the current binary treemap/path logic.
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
}
REQUIRED_STORAGE_NAMES = {
    "StorageView",
    "StorageScopeText",
    "StorageUpButton",
    "StoragePathText",
    "StorageRefreshButton",
    "StorageLogicalText",
    "StorageAllocatedText",
    "StorageFilesText",
    "StorageAliasesText",
    "StorageTreemapModeText",
    "StorageTreemapCanvas",
    "StorageTreemapEmptyText",
    "StorageList",
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
    """Python transcription of MainWindow.LayoutTreemap for property testing."""
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

    # Explicit regression for the old 5%-floor distortion.
    items = [_Item(999), _Item(1)]
    rectangles = []
    _layout_treemap(items, 0, 2, 0, 0, 1000, 500, rectangles)
    fractions = [rect.width * rect.height / 500_000 for rect in rectangles]
    assert math.isclose(fractions[0], 0.999, rel_tol=1e-12)
    assert math.isclose(fractions[1], 0.001, rel_tol=1e-12)
    return cases + 1


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
    xaml_path = repo_root / "src" / "FileOp.App" / "MainWindow.xaml"
    code_path = repo_root / "src" / "FileOp.App" / "MainWindow.xaml.cs"
    if not xaml_path.is_file() or not code_path.is_file():
        raise FileNotFoundError(
            "Run this script from a FileOp checkout, or pass --repo-root. "
            f"Missing {xaml_path} or {code_path}."
        )

    xaml_text = xaml_path.read_text(encoding="utf-8")
    code_text = code_path.read_text(encoding="utf-8")
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

    # Guard against accidentally restoring the old visually-distorting split floor.
    assert "var ratio = leftWeight / (double)total;" in code_text
    assert "Math.Clamp(leftWeight / (double)total" not in code_text

    # Storage must be reachable and must retain a non-rescan fallback implementation.
    assert 'x:Name="StorageNavigationButton"' in xaml_text
    assert "_fallbackIndex.AnalyzeDirectoryAsync(" in code_text
    assert "session.Client.AnalyzeStorageAsync(" in code_text

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
    path_cases = check_path_properties()
    print(f"PASS treemap properties: {treemap_cases} cases")
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
