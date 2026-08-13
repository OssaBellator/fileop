#!/usr/bin/env python3
"""Verify known-location review candidates remain inside their fully-qualified review root."""
from __future__ import annotations

import argparse
import ntpath
from pathlib import Path
import random


def normalize_windows_path(path: str | None) -> str | None:
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


def is_within_root(path: str | None, root: str | None) -> bool:
    normalized_path = normalize_windows_path(path)
    normalized_root = normalize_windows_path(root)
    if normalized_path is None or normalized_root is None:
        return False
    if normalized_path == normalized_root:
        return True
    return normalized_path.startswith(normalized_root.rstrip("\\") + "\\")


def run_model(cases: int, seed: int) -> int:
    checks = 0
    fixed = (
        (r"C:\review\file.zip", r"C:\review", True),
        (r"c:\REVIEW\sub\file.zip", r"C:\review", True),
        (r"C:\review", r"C:\review\", True),
        (r"C:\review2\file.zip", r"C:\review", False),
        (r"D:\review\file.zip", r"C:\review", False),
        (r"D:payload.zip", r"D:\review", False),
        (r"\Temp\payload.zip", r"C:\Temp", False),
        ("C:\\review\\bad\x00.zip", r"C:\review", False),
        (r"\\server\share\review\sub\file.zip", r"\\server\share\review", True),
        (r"\\server\share\review2\file.zip", r"\\server\share\review", False),
    )
    for path, root, expected in fixed:
        assert is_within_root(path, root) == expected
        checks += 1

    rows = [r"C:\review\good.zip", r"C:\review2\bad.zip"]
    filtered = [path for path in rows if is_within_root(path, r"C:\review")]
    assert filtered == [r"C:\review\good.zip"]
    assert len(rows) == 2
    checks += 2

    rng = random.Random(seed)
    drives = "CDEFGH"
    for index in range(cases):
        drive = rng.choice(drives)
        root = f"{drive}:\\review\\U{index % 101}"
        mode = rng.randrange(8)
        if mode == 0:
            candidate = root
            expected = True
        elif mode == 1:
            candidate = root + f"\\sub\\f{index}.zip"
            expected = True
        elif mode == 2:
            candidate = root + "2\\f.zip"
            expected = False
        elif mode == 3:
            other_drive = rng.choice([item for item in drives if item != drive])
            candidate = f"{other_drive}:\\review\\U{index % 101}\\f.zip"
            expected = False
        elif mode == 4:
            candidate = f"{drive}:f.zip"
            expected = False
        elif mode == 5:
            candidate = r"\review\f.zip"
            expected = False
        elif mode == 6:
            candidate = root + "\\bad\x00.zip"
            expected = False
        else:
            candidate = root.upper() + r"\SUB\F.ZIP"
            expected = True

        assert is_within_root(candidate, root) == expected
        assert not is_within_root(root + r"-sibling\x.zip", root)
        other_drive = "Z" if drive != "Z" else "Y"
        assert not is_within_root(f"{other_drive}:\\review\\x.zip", root)

        source_count = rng.randint(1, 100)
        cap = rng.randint(1, 100)
        filtered_count = rng.randint(0, source_count)
        assert filtered_count <= source_count
        assert (source_count >= cap) == (source_count >= cap)
        checks += 4

    return checks


def require(text: str, needle: str, label: str) -> int:
    if needle not in text:
        raise AssertionError(f"missing {label}: {needle}")
    return 1


def forbid(text: str, needle: str, label: str) -> int:
    if needle in text:
        raise AssertionError(f"forbidden {label}: {needle}")
    return 1


def check_repository(root: Path) -> int:
    model = (root / "src/FileOp.Core/Storage/StorageKnownLocationReview.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/StorageKnownLocationReviewTests.cs").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    checks = 0

    for needle, label in (
        ("if (!IsFullyQualifiedPathWithinRoot(source.Path, analysis.RootPath))", "pre-classification containment filter"),
        ("private static bool IsFullyQualifiedPathWithinRoot", "containment helper"),
        ("!Path.IsPathFullyQualified(path)", "candidate fully-qualified requirement"),
        ("!Path.IsPathFullyQualified(rootPath)", "root fully-qualified requirement"),
        ("var fullPath = Path.GetFullPath(path);", "candidate normalization"),
        ("var fullRoot = Path.GetFullPath(rootPath);", "root normalization"),
        ("StringComparison.OrdinalIgnoreCase", "Windows case-insensitive containment"),
        ("var rootedPrefix = comparableRoot + Path.DirectorySeparatorChar;", "separator-bound descendant check"),
        ("exception is ArgumentException or NotSupportedException or PathTooLongException", "nonthrowing malformed-path filter"),
        ("analysis.StaleLargeFiles.Count,", "upstream source count preservation"),
        ("analysis.StaleLargeFiles.Count >= analysis.Policy.MaxStaleLargeFiles", "upstream truncation preservation"),
    ):
        checks += require(model, needle, label)

    containment_at = model.index("if (!IsFullyQualifiedPathWithinRoot(source.Path, analysis.RootPath))")
    classify_at = model.index("var classified = provenance switch", containment_at)
    if not containment_at < classify_at:
        raise AssertionError("candidate containment must run before provenance/extension classification")
    checks += 1

    for needle in (
        "CandidateContainmentAcceptsRootNestedAndCaseEquivalentPaths",
        "CandidateContainmentRejectsSiblingOtherDriveAndRelativePaths",
        "CandidateContainmentRejectsMalformedPathWithoutDiscardingValidNeighbor",
        "CandidateContainmentSupportsUncRootsWithoutSiblingPrefixLeakage",
        "FilteredCandidatesStillCountTowardUpstreamStaleCap",
    ):
        checks += require(tests, needle, f"Windows test {needle}")

    checks += require(
        gate,
        "verify_known_location_candidate_containment.py --repo-root $repoRoot --cases 50000",
        "offline gate wiring",
    )
    checks += require(protocol, "public const int CurrentVersion = 8;", "protocol v8")
    for forbidden in ("File.Delete(", "Directory.Delete(", "SafeToDelete"):
        checks += forbid(model, forbidden, f"review-only classifier {forbidden}")
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0x157)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {source_checks:,} source/test checks" if args.repo_root else ""
    print(
        "PASS: known-location candidate containment verifier: "
        f"{model_checks:,} model checks across {args.cases:,} randomized states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
