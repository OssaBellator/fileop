#!/usr/bin/env python3
"""Verify malformed known-location/catalog path evidence fails closed."""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import ntpath
from pathlib import Path
import random


@dataclass(frozen=True)
class Volume:
    root: str | None
    identity: int


def try_root(path: str | None) -> str | None:
    if path is None or not path.strip() or "\x00" in path:
        return None
    value = ntpath.normpath(path.replace("/", "\\"))
    drive, tail = ntpath.splitdrive(value)
    if not drive or tail not in ("", "\\"):
        return None
    if drive.startswith("\\\\"):
        return ntpath.normcase(drive.rstrip("\\") + "\\")
    if len(drive) != 2 or drive[1] != ":":
        return None
    return ntpath.normcase(drive + "\\")


def try_path(path: str | None) -> str | None:
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


def select_unique(
    volumes: tuple[Volume, ...],
    expected_root: str | None,
) -> tuple[Volume | None, bool, bool]:
    normalized_expected = try_root(expected_root)
    if normalized_expected is None:
        return None, False, True

    match: Volume | None = None
    for volume in volumes:
        normalized_volume = try_root(volume.root)
        if normalized_volume is None:
            return None, False, True
        if normalized_volume != normalized_expected:
            continue
        if match is not None:
            return None, True, False
        match = volume
    return match, False, False


def safe_path_equal(left: str | None, right: str | None) -> bool:
    normalized_left = try_path(left)
    normalized_right = try_path(right)
    return (
        normalized_left is not None
        and normalized_right is not None
        and normalized_left == normalized_right
    )


def run_model(cases: int) -> int:
    checks = 0
    valid = (Volume("C:\\", 1), Volume("D:\\", 2))
    descriptor, ambiguous, invalid = select_unique(valid, "D:\\")
    assert descriptor is not None and descriptor.identity == 2 and not ambiguous and not invalid
    checks += 1

    descriptor, ambiguous, invalid = select_unique(
        valid + (Volume("D:\\", 3),),
        "D:\\",
    )
    assert descriptor is None and ambiguous and not invalid
    checks += 1

    descriptor, ambiguous, invalid = select_unique(
        (Volume("C:\\", 1), Volume("bad\x00root", 9)),
        "C:\\",
    )
    assert descriptor is None and not ambiguous and invalid
    checks += 1

    descriptor, ambiguous, invalid = select_unique(
        (Volume("C:\\", 1), Volume("D:\\folder", 9)),
        "C:\\",
    )
    assert descriptor is None and not ambiguous and invalid
    checks += 1

    assert select_unique((Volume("C:\\", 1),), "bad\x00root")[2]
    checks += 1
    unc_root = r"\\server\share\\"
    assert try_root(unc_root) == ntpath.normcase(ntpath.normpath(unc_root))
    checks += 1
    assert try_path("D:\\Users\\U\\Downloads") is not None
    checks += 1
    assert try_path("D:\\Users\\U\\Down\x00loads") is None
    checks += 1
    assert try_path("D:Temp") is None
    checks += 1
    assert try_path("\\Temp") is None
    checks += 1
    assert not safe_path_equal("D:\\Temp\x00", "D:\\Temp")
    checks += 1

    rng = random.Random(0xC4710)
    for index in range(cases):
        target_root = "D:\\"
        malformed = rng.random() < 0.10
        duplicate = rng.random() < 0.10
        present = rng.random() < 0.90
        volumes = [Volume("C:\\", 1)]
        if present:
            volumes.append(Volume(target_root, 2))
        if malformed:
            malformed_root = "bad\x00root" if index % 2 == 0 else "E:\\not-a-root"
            volumes.append(Volume(malformed_root, 4))
        if duplicate and present:
            volumes.append(Volume(target_root, 3))

        descriptor, ambiguous, invalid = select_unique(tuple(volumes), target_root)
        if malformed:
            assert invalid
        else:
            assert not invalid
            assert ambiguous == (duplicate and present)
            assert (descriptor is not None) == (present and not duplicate)
        checks += 3

        candidate = f"D:\\Temp\\U{index % 97}"
        returned_root = candidate if rng.random() < 0.95 else candidate + "\x00"
        assert safe_path_equal(returned_root, candidate) == ("\x00" not in returned_root)
        checks += 1

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
    producer = (root / "src/FileOp.App/DesktopSearchEngine.KnownLocationReview.cs").read_text(
        encoding="utf-8"
    )
    helper = (root / "src/FileOp.App/DesktopSearchEngine.KnownLocationReviewCrossVolume.cs").read_text(
        encoding="utf-8"
    )
    source = (root / "src/FileOp.App/DesktopSearchEngine.KnownLocationReviewSource.cs").read_text(
        encoding="utf-8"
    )
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(
        encoding="utf-8"
    )
    checks = 0

    required_producer = (
        ("TryNormalizeReviewRoot(capturedPrimary.RootPath, out var capturedRoot)", "active primary root validation"),
        ("exception is ArgumentException or\n            COMException or\n            InvalidDataException or\n            IOException or\n            NotSupportedException", "Downloads resolver path exception isolation"),
        ("if (!Path.IsPathFullyQualified(locationPath))", "local path fully-qualified requirement"),
        ("fullPath = Path.GetFullPath(locationPath);", "per-location path normalization"),
        ("resolved to an invalid filesystem path", "local malformed-path refusal"),
        ("exception is ArgumentException or NotSupportedException or PathTooLongException", "local path exception filter"),
        ("!ReviewRootsEqual(currentRoot, capturedRoot)", "final public root safe comparison"),
        ("!ReviewRootsEqual(currentPrimary.RootPath, capturedRoot)", "final selected root safe comparison"),
        ("exception is ArgumentException or\n            IOException or\n            NotSupportedException or\n            PathTooLongException", "Temp PathTooLong isolation"),
    )
    for needle, label in required_producer:
        checks += require(producer, needle, label)

    required_helper = (
        ("out bool invalidCatalog", "selector malformed-catalog status"),
        ("TryNormalizeReviewRoot(rootPath", "expected-root safe normalization"),
        ("TryNormalizeReviewRoot(volume.RootPath", "descriptor safe normalization"),
        ("var filesystemRoot = Path.GetPathRoot(fullPath);", "filesystem-root derivation"),
        ("var normalizedFilesystemRoot = NormalizeRoot(filesystemRoot);", "filesystem-root normalization"),
        ("normalizedRoot,\n                    normalizedFilesystemRoot", "root-only semantic check"),
        ("out var primaryBeforeInvalidCatalog", "primary-before malformed check"),
        ("out var targetInvalidCatalog", "target-before malformed check"),
        ("out var currentInvalidCatalog", "secondary-after malformed check"),
        ("out var primaryAfterInvalidCatalog", "primary-after malformed check"),
        ("ReviewRootsEqual(primaryVolume.RootPath, activeRoot)", "selected-primary safe root comparison"),
        ("ReviewRootsEqual(current.RootPath, target.RootPath)", "secondary safe root comparison"),
        ("ReviewPathsEqual(response.Analysis.RootPath, fullPath)", "analysis-root safe comparison"),
        ("if (!Path.IsPathFullyQualified(path))", "analysis path fully-qualified requirement"),
        ("TryNormalizeReviewPath(left", "nonthrowing analysis path normalizer"),
        ("exception is ArgumentException or NotSupportedException or PathTooLongException", "path exception fail-closed filter"),
        ("unexpected or malformed root", "malformed analysis-root refusal wording"),
    )
    for needle, label in required_helper:
        checks += require(helper, needle, label)

    required_source = (
        ("TryNormalizeReviewRoot(expectedRoot, out _)", "expected freshness root validation"),
        ("ReviewRootsEqual(selectedPrimary.RootPath, expectedRoot)", "selected source safe comparison"),
        ("ReviewRootsEqual(currentPrimary.RootPath, expectedRoot)", "current source safe comparison"),
        ("out var invalidCatalog", "freshness malformed-catalog status"),
        ("return !invalidCatalog &&", "freshness malformed-catalog refusal"),
        ("ReviewRootsEqual(currentDescriptor.RootPath, expectedRoot)", "fresh descriptor safe comparison"),
    )
    for needle, label in required_source:
        checks += require(source, needle, label)

    checks += forbid(
        source,
        "NormalizeRoot(currentDescriptor.RootPath)",
        "throwing final descriptor root normalization",
    )
    checks += require(
        gate,
        "verify_known_location_malformed_catalog.py --repo-root $repoRoot --cases 50000",
        "offline malformed-catalog verifier wiring",
    )
    checks += require(protocol, "public const int CurrentVersion = 8;", "protocol v8")
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
    suffix = f" and {repo_checks:,} source checks" if args.repo_root else ""
    print(
        "PASS: malformed known-location path/catalog handling verified with "
        f"{model_checks:,} model checks across {args.cases:,} randomized states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
