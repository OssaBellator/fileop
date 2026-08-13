#!/usr/bin/env python3
"""Verify indexed cross-volume known-location review without .NET or GitHub Actions."""
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
    checkpoint: bool


def canon_root(path: str) -> str:
    value = ntpath.normpath(path)
    drive, tail = ntpath.splitdrive(value)
    if not drive or tail not in ("", "\\"):
        raise ValueError(path)
    return ntpath.normcase(drive + "\\")


def same_root(left: str, right: str) -> bool:
    try:
        return canon_root(left) == canon_root(right)
    except ValueError:
        return False


def select_volume(volumes: tuple[Volume, ...], root: str) -> Volume | None:
    return next((volume for volume in volumes if same_root(volume.root, root)), None)


def cross_volume_outcome(
    *,
    location_root: str,
    active_root: str,
    before: tuple[Volume, ...],
    catchup_current: bool,
    after: tuple[Volume, ...],
    primary_stable: bool,
) -> str:
    if same_root(location_root, active_root):
        return "primary"
    target = select_volume(before, location_root)
    if target is None:
        return "outside-active-no-index"
    if not target.checkpoint:
        return "unavailable-no-checkpoint"
    if not catchup_current:
        return "unavailable-not-current"
    current = select_volume(after, location_root)
    if (
        current is None
        or current.identity != target.identity
        or not current.checkpoint
        or not same_root(current.root, target.root)
    ):
        return "unavailable-source-changed"
    if not primary_stable:
        return "unavailable-primary-changed"
    return "available-cross-volume"


def run_model(cases: int, seed: int) -> int:
    checks = 0
    before = (Volume("C:\\", 1, True), Volume("D:\\", 2, True))
    assert cross_volume_outcome(
        location_root="D:\\",
        active_root="C:\\",
        before=before,
        catchup_current=True,
        after=before,
        primary_stable=True,
    ) == "available-cross-volume"
    assert cross_volume_outcome(
        location_root="E:\\",
        active_root="C:\\",
        before=before,
        catchup_current=True,
        after=before,
        primary_stable=True,
    ) == "outside-active-no-index"
    assert cross_volume_outcome(
        location_root="D:\\",
        active_root="C:\\",
        before=(Volume("D:\\", 2, False),),
        catchup_current=True,
        after=(Volume("D:\\", 2, False),),
        primary_stable=True,
    ) == "unavailable-no-checkpoint"
    assert cross_volume_outcome(
        location_root="D:\\",
        active_root="C:\\",
        before=before,
        catchup_current=False,
        after=before,
        primary_stable=True,
    ) == "unavailable-not-current"
    assert cross_volume_outcome(
        location_root="D:\\",
        active_root="C:\\",
        before=before,
        catchup_current=True,
        after=(Volume("C:\\", 1, True), Volume("D:\\", 99, True)),
        primary_stable=True,
    ) == "unavailable-source-changed"
    checks += 5

    rng = random.Random(seed)
    drives = "CDEFGH"
    for index in range(cases):
        active_drive = rng.choice(drives)
        location_drive = rng.choice(drives)
        active_root = f"{active_drive}:\\"
        location_root = f"{location_drive}:\\"
        target_present = rng.random() < 0.90
        checkpoint = rng.random() < 0.91
        identity = rng.randrange(1, 1_000_000)
        before_list = [Volume(active_root, 10_000_000 + index, True)]
        if target_present and not same_root(location_root, active_root):
            before_list.append(Volume(location_root, identity, checkpoint))
        before_state = tuple(before_list)
        catchup_current = rng.random() < 0.88
        source_stable = rng.random() < 0.94
        primary_stable = rng.random() < 0.96
        after_list = [Volume(active_root, 10_000_000 + index, True)]
        if target_present and not same_root(location_root, active_root):
            if source_stable:
                after_list.append(Volume(location_root, identity, checkpoint))
            else:
                after_list.append(Volume(location_root, identity + 1, True))
        outcome = cross_volume_outcome(
            location_root=location_root,
            active_root=active_root,
            before=before_state,
            catchup_current=catchup_current,
            after=tuple(after_list),
            primary_stable=primary_stable,
        )

        if same_root(location_root, active_root):
            assert outcome == "primary"
            checks += 1
            continue

        expected_available = (
            target_present
            and checkpoint
            and catchup_current
            and source_stable
            and primary_stable
        )
        assert (outcome == "available-cross-volume") == expected_available
        checks += 1
        if outcome == "available-cross-volume":
            target = select_volume(before_state, location_root)
            current = select_volume(tuple(after_list), location_root)
            assert target is not None and current is not None
            assert target.checkpoint and current.checkpoint
            assert target.identity == current.identity
            assert primary_stable
            checks += 4
        if not target_present:
            assert outcome == "outside-active-no-index"
            checks += 1
        elif not checkpoint:
            assert outcome == "unavailable-no-checkpoint"
            checks += 1
        elif not catchup_current:
            assert outcome == "unavailable-not-current"
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
    helper = (root / "src/FileOp.App/DesktopSearchEngine.KnownLocationReviewCrossVolume.cs").read_text(encoding="utf-8")
    producer = (root / "src/FileOp.App/DesktopSearchEngine.KnownLocationReview.cs").read_text(encoding="utf-8")
    xaml = (root / "src/FileOp.App/StorageKnownLocationReviewView.xaml").read_text(encoding="utf-8")
    view = (root / "src/FileOp.App/StorageKnownLocationReviewView.xaml.cs").read_text(encoding="utf-8")
    handoff = (root / "src/FileOp.App/MainWindow.FilesReviewHandoff.cs").read_text(encoding="utf-8")
    readiness = (root / "src/FileOp.App/MainWindow.StorageCleanupReadiness.cs").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    docs = (root / "docs/known-location-review.md").read_text(encoding="utf-8")
    checks = 0

    for text, needle, label in (
        (producer, "AnalyzeCrossVolumeKnownLocationAsync(", "cross-volume delegation"),
        (producer, "if (!IsReviewPathWithinRoot(fullPath, activeRoot))", "primary/cross-volume split"),
        (helper, "Path.GetPathRoot(fullPath)", "location volume-root derivation"),
        (helper, "GetVolumesAsync(_lifetimeCancellation.Token)", "indexed-volume discovery"),
        (helper, "FindIndexedVolumeByRoot(volumesBefore.Volumes, locationRoot)", "pre-capture root selection"),
        (helper, "if (!target.HasCheckpoint)", "existing-checkpoint requirement"),
        (helper, "new IndexingVolumeRequest(target.VolumeIdentity, target.RootPath)", "exact sync volume binding"),
        (helper, "CatchUpAsync(", "bounded existing catch-up path"),
        (helper, "InitialCatchUpBatchLimit", "bounded catch-up limit"),
        (helper, "if (!catchUp.IsCurrent)", "currentness requirement"),
        (helper, "new IndexingStorageOptimizationRequest(", "volume-bound analysis request"),
        (helper, "target.VolumeIdentity", "analysis volume identity"),
        (helper, "target.RootPath", "analysis volume root"),
        (helper, "fullPath", "analysis directory path"),
        (helper, "var volumesAfter = await session.Client", "post-capture volume discovery"),
        (helper, "current.VolumeIdentity != target.VolumeIdentity", "post-capture identity check"),
        (helper, "!current.HasCheckpoint", "post-capture checkpoint check"),
        (helper, "_primaryVolume is not { } currentPrimary", "primary source preservation"),
        (helper, "FileOp did not scan the path directly", "no-crawler failure disclosure"),
        (helper, "will not rebuild a secondary volume implicitly", "no implicit secondary rebuild"),
        (xaml, 'IsEnabled="{Binding CanReviewInFiles}"', "Files handoff UI gating"),
        (view, "bool CanReviewInFiles", "candidate handoff capability"),
        (view, "outside the active Files indexed volume", "cross-volume handoff explanation"),
        (handoff, "That review candidate is on another indexed volume", "programmatic Files refusal"),
        (handoff, "did not use direct filesystem enumeration as a fallback", "no Files fallback disclosure"),
        (readiness, "Cross-volume review candidates remain eligible for this read-only current-path check", "cross-volume readiness"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8"),
        (gate, "verify_cross_volume_known_location_review.py", "offline gate wiring"),
        (docs, "requires catch-up to reach `IsCurrent == true`", "freshness documentation"),
        (docs, "does **not** silently rebuild a secondary volume", "no-rebuild documentation"),
    ):
        checks += require(text, needle, label)

    if helper.count("GetVolumesAsync(_lifetimeCancellation.Token)") != 2:
        raise AssertionError("cross-volume review must read volume descriptors before and after analysis")
    checks += 1

    request_at = helper.index("new IndexingStorageOptimizationRequest(")
    catchup_at = helper.index("CatchUpAsync(")
    post_at = helper.index("var volumesAfter = await session.Client")
    classify_at = helper.index("StorageKnownLocationReviewClassifier.Classify(")
    if not catchup_at < request_at < post_at < classify_at:
        raise AssertionError("cross-volume freshness/analysis/source-validation order changed")
    checks += 1

    for text, label in ((helper, "cross-volume helper"), (producer, "known-location producer")):
        for needle in (
            "FileSystemCrawler",
            "Directory.Enumerate",
            "Directory.GetFiles",
            "File.Open",
            "RebuildVolumeAsync",
            "RebuildVolume",
            "File.Delete(",
            "Directory.Delete(",
        ):
            checks += forbid(text, needle, f"{label} {needle}")

    checks += forbid(protocol, "CrossVolumeKnownLocation", "new protocol operation")
    checks += forbid(protocol, "KnownLocationReview", "known-location protocol surface")
    checks += forbid(
        readiness,
        "IsPathWithinRoot(matchedLocation.RootPath, activeRoot)",
        "primary-volume-only readiness binding",
    )
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0xC2055)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model = run_model(args.cases, args.seed)
    source = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {source:,} source checks" if args.repo_root else ""
    print(
        f"PASS: cross-volume known-location review verified with {model:,} model assertions "
        f"across {args.cases:,} randomized states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
