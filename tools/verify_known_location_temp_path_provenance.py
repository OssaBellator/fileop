#!/usr/bin/env python3
"""Verify Temp known-location provenance is checked before path normalization."""
from __future__ import annotations

import argparse
import ntpath
from pathlib import Path
import random


def normalize_review_path(path: str | None) -> str | None:
    if path is None or not path.strip() or "\x00" in path:
        return None
    value = path.replace("/", "\\")
    drive, tail = ntpath.splitdrive(value)
    fully_qualified = (
        (drive.startswith("\\\\") and tail.startswith("\\"))
        or (len(drive) == 2 and drive[1] == ":" and tail.startswith("\\"))
    )
    if not fully_qualified:
        return None
    return ntpath.normcase(ntpath.normpath(value))


def run_model(cases: int, seed: int) -> int:
    checks = 0
    for raw, accepted in (
        (r"C:\Users\U\AppData\Local\Temp", True),
        (r"D:Temp", False),
        (r"\Temp", False),
        (r"Temp", False),
        ("C:\\Temp\\bad\x00name", False),
        (r"\\server\share\Temp", True),
    ):
        assert (normalize_review_path(raw) is not None) == accepted
        checks += 1

    rng = random.Random(seed)
    drives = "CDEFGH"
    for index in range(cases):
        drive = rng.choice(drives)
        mode = rng.randrange(6)
        if mode == 0:
            raw = f"{drive}:\\Users\\U{index % 101}\\Temp"
            expected = True
        elif mode == 1:
            raw = f"{drive}:Temp\\U{index % 101}"
            expected = False
        elif mode == 2:
            raw = f"\\Temp\\U{index % 101}"
            expected = False
        elif mode == 3:
            raw = f"Temp\\U{index % 101}"
            expected = False
        elif mode == 4:
            raw = f"{drive}:\\Temp\\bad\x00{index}"
            expected = False
        else:
            raw = f"\\\\server\\share\\Temp\\U{index % 101}"
            expected = True
        assert (normalize_review_path(raw) is not None) == expected
        checks += 1
    return checks


def check_repository(root: Path) -> int:
    source = (root / "src/FileOp.App/DesktopSearchEngine.KnownLocationReview.cs").read_text(encoding="utf-8")
    checks = 0
    required = (
        ("tempPath = Path.GetTempPath();", "raw Temp resolver output"),
        ("if (!Path.IsPathFullyQualified(locationPath))", "fully-qualified provenance check"),
        ("fullPath = Path.GetFullPath(locationPath);", "normalization after qualification"),
    )
    for needle, label in required:
        if needle not in source:
            raise AssertionError(f"missing {label}: {needle}")
        checks += 1
    if "Path.GetFullPath(Path.GetTempPath())" in source:
        raise AssertionError("Temp resolver output must not be normalized before provenance validation")
    checks += 1

    capture = source.index("tempPath = Path.GetTempPath();")
    call = source.index("AnalyzeKnownLocationAsync(\n                StorageReviewProvenance.UserTemp", capture)
    qualification = source.index("if (!Path.IsPathFullyQualified(locationPath))", call)
    normalization = source.index("fullPath = Path.GetFullPath(locationPath);", qualification)
    if not capture < call < qualification < normalization:
        raise AssertionError("Temp path must flow raw into qualification before normalization")
    checks += 1
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0x151)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {source_checks:,} source checks" if args.repo_root else ""
    print(
        "PASS: known-location Temp provenance verifier: "
        f"{model_checks:,} model checks across {args.cases:,} randomized states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
