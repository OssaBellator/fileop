#!/usr/bin/env python3
"""Verify per-location indexed source identity for known-location review."""
from __future__ import annotations

import argparse
import ntpath
import random
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class Volume:
    root: str
    identity: int


@dataclass(frozen=True)
class Location:
    available: bool
    root: str
    source_identity: int | None


def canon_root(path: str) -> str:
    value = ntpath.normpath(path)
    drive, tail = ntpath.splitdrive(value)
    if not drive or tail not in ("", "\\"):
        raise ValueError(path)
    return ntpath.normcase(drive + "\\")


def path_root(path: str) -> str:
    value = ntpath.normpath(path)
    drive, tail = ntpath.splitdrive(value)
    if not drive or not tail.startswith("\\"):
        raise ValueError(path)
    return ntpath.normcase(drive + "\\")


def select_unique(volumes: tuple[Volume, ...], root: str) -> tuple[Volume | None, bool]:
    expected = canon_root(root)
    matches = [volume for volume in volumes if canon_root(volume.root) == expected]
    if len(matches) > 1:
        return None, True
    return (matches[0] if matches else None), False


def matches_source(volumes: tuple[Volume, ...], root: str, identity: int) -> bool:
    descriptor, ambiguous = select_unique(volumes, root)
    return not ambiguous and descriptor is not None and descriptor.identity == identity


def review_sources_current(
    *,
    primary_root: str,
    primary_identity: int,
    selected_primary_root: str | None,
    selected_primary_identity: int | None,
    locations: tuple[Location, ...],
    catalog: tuple[Volume, ...],
) -> bool:
    if (
        selected_primary_root is None
        or selected_primary_identity != primary_identity
        or canon_root(selected_primary_root) != canon_root(primary_root)
        or not matches_source(catalog, primary_root, primary_identity)
    ):
        return False

    for location in locations:
        if not location.available:
            continue
        if location.source_identity is None or not location.root:
            return False
        try:
            source_root = path_root(location.root)
        except ValueError:
            return False
        if not matches_source(catalog, source_root, location.source_identity):
            return False
    return True


def run_model(cases: int, seed: int) -> int:
    checks = 0
    base_catalog = (Volume("C:\\", 1), Volume("D:\\", 2))
    available_secondary = Location(True, r"D:\Users\U\Downloads", 2)
    available_primary = Location(True, r"C:\Users\U\Temp", 1)
    unavailable = Location(False, r"E:\Missing", None)

    assert review_sources_current(
        primary_root="C:\\",
        primary_identity=1,
        selected_primary_root="c:\\",
        selected_primary_identity=1,
        locations=(available_secondary, available_primary, unavailable),
        catalog=base_catalog,
    )
    assert not review_sources_current(
        primary_root="C:\\",
        primary_identity=1,
        selected_primary_root="C:\\",
        selected_primary_identity=1,
        locations=(available_secondary,),
        catalog=(Volume("C:\\", 1), Volume("D:\\", 99)),
    )
    assert not review_sources_current(
        primary_root="C:\\",
        primary_identity=1,
        selected_primary_root="C:\\",
        selected_primary_identity=1,
        locations=(available_secondary,),
        catalog=(Volume("C:\\", 1), Volume("D:\\", 2), Volume("D:\\", 3)),
    )
    assert not review_sources_current(
        primary_root="C:\\",
        primary_identity=1,
        selected_primary_root="C:\\",
        selected_primary_identity=1,
        locations=(Location(True, available_secondary.root, None),),
        catalog=base_catalog,
    )
    assert review_sources_current(
        primary_root="C:\\",
        primary_identity=1,
        selected_primary_root="C:\\",
        selected_primary_identity=1,
        locations=(unavailable,),
        catalog=(Volume("C:\\", 1),),
    )
    checks += 5

    rng = random.Random(seed)
    drives = "CDEFGH"
    for index in range(cases):
        primary_drive = rng.choice(drives)
        primary_root = f"{primary_drive}:\\"
        primary_identity = 10_000_000 + index
        selected_primary_matches = rng.random() < 0.98
        selected_primary_root_matches = rng.random() < 0.99
        selected_primary_identity = (
            primary_identity if selected_primary_matches else primary_identity + 1
        )
        selected_primary_root = (
            primary_root if selected_primary_root_matches else f"Z:\\"
        )

        catalog: list[Volume] = []
        primary_present = rng.random() < 0.99
        primary_identity_matches = rng.random() < 0.98
        primary_ambiguous = rng.random() < 0.01
        if primary_present:
            catalog_primary_identity = (
                primary_identity if primary_identity_matches else primary_identity + 2
            )
            catalog.append(Volume(primary_root, catalog_primary_identity))
            if primary_ambiguous:
                catalog.append(Volume(primary_root, catalog_primary_identity + 1_000_000))

        location_drive = rng.choice(drives)
        location_root = f"{location_drive}:\\"
        location_identity = 20_000_000 + index
        location_available = rng.random() < 0.90
        location_has_identity = rng.random() < 0.99
        location_present = rng.random() < 0.98
        location_identity_matches = rng.random() < 0.96
        location_ambiguous = rng.random() < 0.01
        location_path = f"{location_root}Users\\U{index % 97}\\Review"
        location = Location(
            location_available,
            location_path,
            location_identity if location_has_identity else None,
        )

        if location_available and canon_root(location_root) != canon_root(primary_root):
            if location_present:
                catalog_location_identity = (
                    location_identity if location_identity_matches else location_identity + 3
                )
                catalog.append(Volume(location_root, catalog_location_identity))
                if location_ambiguous:
                    catalog.append(Volume(location_root, catalog_location_identity + 1_000_000))
        elif location_available and canon_root(location_root) == canon_root(primary_root):
            location = Location(
                True,
                location_path,
                primary_identity if location_has_identity else None,
            )
            location_present = primary_present
            location_identity_matches = primary_identity_matches
            location_ambiguous = primary_ambiguous

        result = review_sources_current(
            primary_root=primary_root,
            primary_identity=primary_identity,
            selected_primary_root=selected_primary_root,
            selected_primary_identity=selected_primary_identity,
            locations=(location,),
            catalog=tuple(catalog),
        )
        expected = (
            selected_primary_matches
            and selected_primary_root_matches
            and primary_present
            and primary_identity_matches
            and not primary_ambiguous
            and (
                not location_available
                or (
                    location_has_identity
                    and location_present
                    and location_identity_matches
                    and not location_ambiguous
                )
            )
        )
        assert result == expected
        if location_available and not location_has_identity:
            assert not result
        if location_available and location_present and not location_identity_matches:
            assert not result
        if location_available and location_ambiguous:
            assert not result
        checks += 4
    return checks


def require(text: str, needle: str, label: str) -> int:
    if needle not in text:
        raise AssertionError(f"missing {label}: {needle}")
    return 1


def check_repository(root: Path) -> int:
    files = {
        "model": root / "src/FileOp.Core/Storage/StorageKnownLocationReview.cs",
        "producer": root / "src/FileOp.App/DesktopSearchEngine.KnownLocationReview.cs",
        "cross": root / "src/FileOp.App/DesktopSearchEngine.KnownLocationReviewCrossVolume.cs",
        "source": root / "src/FileOp.App/DesktopSearchEngine.KnownLocationReviewSource.cs",
        "readiness": root / "src/FileOp.App/MainWindow.StorageCleanupReadiness.cs",
        "gate": root / "tools/test-local.ps1",
    }
    text = {name: path.read_text(encoding="utf-8") for name, path in files.items()}
    required = (
        ("model", "public ulong? SourceVolumeIdentity { get; init; }", "per-location source identity model"),
        ("producer", "SourceVolumeIdentity = activeVolumeIdentity", "same-volume source identity capture"),
        ("producer", "IsNativeReviewSourceCurrentAsync(\n                capturedVolumeIdentity,\n                capturedRoot,\n                locations)", "final all-location source revalidation"),
        ("cross", "SourceVolumeIdentity = target.VolumeIdentity", "cross-volume source identity capture"),
        ("source", "IReadOnlyList<StorageKnownLocationReview> expectedLocations", "location-aware freshness overload"),
        ("source", "location.SourceVolumeIdentity is not { } sourceVolumeIdentity", "location source identity requirement"),
        ("source", "Path.GetPathRoot(location.RootPath)", "location source-root derivation"),
        ("source", "MatchesExpectedSource(", "unique catalog source matching"),
        ("readiness", "[matchedLocation]", "readiness owning-location source binding"),
        ("gate", "verify_known_location_source_identity.py", "offline gate wiring"),
    )
    checks = 0
    for source, needle, label in required:
        checks += require(text[source], needle, label)
    if text["readiness"].count("IsNativeReviewSourceCurrentAsync(") != 2:
        raise AssertionError("readiness must revalidate the owning indexed source before and after its current-file read")
    checks += 1
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0x50A1CE)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")
    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {source_checks:,} source checks" if args.repo_root else ""
    print(
        f"PASS: known-location source identity verifier: {model_checks:,} model checks "
        f"across {args.cases:,} randomized states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
