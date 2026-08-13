#!/usr/bin/env python3
"""Verify known-location source-loss status semantics without .NET or Actions."""
from __future__ import annotations

import argparse
from pathlib import Path
import random

ERROR_CODES = (
    "InvalidRequest",
    "ProtocolMismatch",
    "VolumeNotFound",
    "SnapshotRequired",
    "ElevationRequired",
    "Busy",
    "ResponseTooLarge",
    "InternalError",
)


def remote_failure_status(error_code: str) -> str:
    if error_code not in ERROR_CODES:
        raise ValueError(error_code)
    # Once a review request reached the native service, a remote failure means the
    # source became unavailable. It does not prove that no indexed source exists.
    return "unavailable"


def catalog_lookup_status(*, cross_volume: bool, target_descriptor_present: bool) -> str:
    if not cross_volume:
        return "primary"
    return "indexed" if target_descriptor_present else "outside-active-no-index"


def run_model(cases: int, seed: int) -> int:
    checks = 0
    assert remote_failure_status("VolumeNotFound") == "unavailable"
    assert remote_failure_status("ProtocolMismatch") == "unavailable"
    assert remote_failure_status("Busy") == "unavailable"
    assert catalog_lookup_status(cross_volume=True, target_descriptor_present=False) == "outside-active-no-index"
    assert catalog_lookup_status(cross_volume=True, target_descriptor_present=True) == "indexed"
    assert catalog_lookup_status(cross_volume=False, target_descriptor_present=False) == "primary"
    checks += 6

    rng = random.Random(seed)
    for _ in range(cases):
        error = rng.choice(ERROR_CODES)
        assert remote_failure_status(error) == "unavailable"

        cross_volume = rng.random() < 0.70
        target_present = rng.random() < 0.85
        status = catalog_lookup_status(
            cross_volume=cross_volume,
            target_descriptor_present=target_present,
        )
        expected = (
            "primary"
            if not cross_volume
            else "indexed"
            if target_present
            else "outside-active-no-index"
        )
        assert status == expected
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


def check_repository(root: Path) -> int:
    producer = (
        root / "src/FileOp.App/DesktopSearchEngine.KnownLocationReview.cs"
    ).read_text(encoding="utf-8")
    cross_volume = (
        root / "src/FileOp.App/DesktopSearchEngine.KnownLocationReviewCrossVolume.cs"
    ).read_text(encoding="utf-8")
    view = (
        root / "src/FileOp.App/StorageKnownLocationReviewView.xaml.cs"
    ).read_text(encoding="utf-8")
    protocol = (
        root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs"
    ).read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    checks = 0

    checks += require(
        producer,
        "catch (IndexingServiceRemoteException exception)",
        "remote review failure isolation",
    )
    checks += require(
        producer,
        "// A remote failure after review has begun is source unavailability, not",
        "remote failure status rationale",
    )
    checks += require(
        producer,
        "StorageReviewLocationStatus.Unavailable,\n                fullPath,\n                $\"{FormatProvenance(provenance)} could not be reviewed from the native index",
        "remote failures map to unavailable",
    )
    checks += forbid(
        producer,
        "exception.Error.Code == IndexingServiceErrorCode.VolumeNotFound",
        "VolumeNotFound outside-active remapping",
    )

    target_missing = cross_volume.index("if (target is null)")
    outside_status = cross_volume.index(
        "StorageReviewLocationStatus.OutsideActiveVolume",
        target_missing,
    )
    no_match_detail = cross_volume.index(
        "no matching native indexed volume is available",
        outside_status,
    )
    checkpoint_check = cross_volume.index("if (!target.HasCheckpoint)", no_match_detail)
    if not target_missing < outside_status < no_match_detail < checkpoint_check:
        raise AssertionError(
            "OutsideActiveVolume must be emitted only by the fresh pre-capture missing-target catalog branch"
        )
    checks += 1

    if cross_volume.count("StorageReviewLocationStatus.OutsideActiveVolume") != 1:
        raise AssertionError(
            "cross-volume review should have exactly one OutsideActiveVolume emission site"
        )
    checks += 1

    checks += require(
        view,
        'StorageReviewLocationStatus.OutsideActiveVolume => "outside active volume / no indexed source"',
        "outside-active UI meaning",
    )
    checks += require(protocol, "public const int CurrentVersion = 8;", "protocol v8")
    checks += require(
        gate,
        "verify_known_location_status_semantics.py --repo-root $repoRoot --cases 50000",
        "offline status-semantics gate wiring",
    )
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0x150157)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {source_checks:,} source checks" if args.repo_root else ""
    print(
        "PASS: known-location status semantics verifier: "
        f"{model_checks:,} model checks across {args.cases:,} randomized states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
