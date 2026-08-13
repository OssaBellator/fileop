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
    state: str = "Idle"


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


def same_path(left: str, right: str) -> bool:
    try:
        return ntpath.normcase(ntpath.normpath(left)) == ntpath.normcase(ntpath.normpath(right))
    except ValueError:
        return False


def select_unique(volumes: tuple[Volume, ...], root: str) -> tuple[Volume | None, bool]:
    matches = [volume for volume in volumes if same_root(volume.root, root)]
    if len(matches) > 1:
        return None, True
    return (matches[0] if matches else None), False


def snapshot_required(volume: Volume) -> bool:
    return volume.state.lower() == "snapshotrequired"


def cross_volume_outcome(
    *,
    location_root: str,
    active_root: str,
    selected_primary_identity: int,
    before: tuple[Volume, ...],
    catchup_current: bool,
    after: tuple[Volume, ...],
) -> str:
    if same_root(location_root, active_root):
        return "primary"

    primary_before, primary_before_ambiguous = select_unique(before, active_root)
    if primary_before_ambiguous:
        return "unavailable-primary-ambiguous-before"
    if primary_before is None or primary_before.identity != selected_primary_identity:
        return "unavailable-primary-before"

    target, target_ambiguous = select_unique(before, location_root)
    if target_ambiguous:
        return "unavailable-target-ambiguous-before"
    if target is None:
        return "outside-active-no-index"
    if not target.checkpoint:
        return "unavailable-no-checkpoint" if snapshot_required(target) else "unavailable-busy-before"
    if not catchup_current:
        return "unavailable-not-current"

    current, current_ambiguous = select_unique(after, location_root)
    if current_ambiguous:
        return "unavailable-source-ambiguous-after"
    if current is None or current.identity != target.identity or not same_root(current.root, target.root):
        return "unavailable-source-changed"
    if not current.checkpoint:
        return "unavailable-checkpoint-lost" if snapshot_required(current) else "unavailable-busy-after"

    primary_after, primary_after_ambiguous = select_unique(after, active_root)
    if primary_after_ambiguous:
        return "unavailable-primary-ambiguous-after"
    if (
        primary_after is None
        or primary_after.identity != primary_before.identity
        or not same_root(primary_after.root, primary_before.root)
    ):
        return "unavailable-primary-changed"

    return "available-cross-volume"


def run_model(cases: int, seed: int) -> int:
    checks = 0
    before = (Volume("C:\\", 1, True), Volume("D:\\", 2, True))
    assert same_path("D:\\Temp\\", "D:\\Temp")
    assert same_path("d:\\users\\u\\downloads", "D:\\Users\\U\\Downloads\\")
    assert not same_path("D:\\Temp", "D:\\Temp2")
    checks += 3

    fixed = (
        (
            dict(location_root="D:\\", active_root="C:\\", selected_primary_identity=1,
                 before=before, catchup_current=True, after=before),
            "available-cross-volume",
        ),
        (
            dict(location_root="E:\\", active_root="C:\\", selected_primary_identity=1,
                 before=before, catchup_current=True, after=before),
            "outside-active-no-index",
        ),
        (
            dict(location_root="D:\\", active_root="C:\\", selected_primary_identity=1,
                 before=(Volume("C:\\", 1, True), Volume("C:\\", 9, True), Volume("D:\\", 2, True)),
                 catchup_current=True, after=before),
            "unavailable-primary-ambiguous-before",
        ),
        (
            dict(location_root="D:\\", active_root="C:\\", selected_primary_identity=1,
                 before=(Volume("C:\\", 1, True), Volume("D:\\", 2, True), Volume("D:\\", 3, True)),
                 catchup_current=True, after=before),
            "unavailable-target-ambiguous-before",
        ),
        (
            dict(location_root="D:\\", active_root="C:\\", selected_primary_identity=1,
                 before=before, catchup_current=True,
                 after=(Volume("C:\\", 1, True), Volume("D:\\", 2, True), Volume("D:\\", 3, True))),
            "unavailable-source-ambiguous-after",
        ),
        (
            dict(location_root="D:\\", active_root="C:\\", selected_primary_identity=1,
                 before=before, catchup_current=True,
                 after=(Volume("C:\\", 1, True), Volume("C:\\", 9, True), Volume("D:\\", 2, True))),
            "unavailable-primary-ambiguous-after",
        ),
        (
            dict(location_root="D:\\", active_root="C:\\", selected_primary_identity=1,
                 before=(Volume("C:\\", 1, True), Volume("D:\\", 2, False, "SnapshotRequired")),
                 catchup_current=True,
                 after=(Volume("C:\\", 1, True), Volume("D:\\", 2, False, "SnapshotRequired"))),
            "unavailable-no-checkpoint",
        ),
        (
            dict(location_root="D:\\", active_root="C:\\", selected_primary_identity=1,
                 before=(Volume("C:\\", 1, True), Volume("D:\\", 2, False, "Idle")),
                 catchup_current=True,
                 after=(Volume("C:\\", 1, True), Volume("D:\\", 2, False, "Idle"))),
            "unavailable-busy-before",
        ),
        (
            dict(location_root="D:\\", active_root="C:\\", selected_primary_identity=1,
                 before=before, catchup_current=False, after=before),
            "unavailable-not-current",
        ),
        (
            dict(location_root="D:\\", active_root="C:\\", selected_primary_identity=1,
                 before=before, catchup_current=True,
                 after=(Volume("C:\\", 1, True), Volume("D:\\", 99, True))),
            "unavailable-source-changed",
        ),
        (
            dict(location_root="D:\\", active_root="C:\\", selected_primary_identity=99,
                 before=before, catchup_current=True, after=before),
            "unavailable-primary-before",
        ),
        (
            dict(location_root="D:\\", active_root="C:\\", selected_primary_identity=1,
                 before=before, catchup_current=True,
                 after=(Volume("C:\\", 99, True), Volume("D:\\", 2, True))),
            "unavailable-primary-changed",
        ),
    )
    for arguments, expected in fixed:
        assert cross_volume_outcome(**arguments) == expected
        checks += 1

    rng = random.Random(seed)
    drives = "CDEFGH"
    busy_descriptor_states = ("Idle", "Busy", "Rebuilding", "Syncing")
    for index in range(cases):
        active_drive = rng.choice(drives)
        location_drive = rng.choice(drives)
        active_root = f"{active_drive}:\\"
        location_root = f"{location_drive}:\\"
        normalized_candidate = f"{location_root}Users\\U{index % 97}\\Temp"
        equivalent_candidate = (
            normalized_candidate + "\\" if index % 2 == 0 else normalized_candidate.swapcase()
        )
        assert same_path(normalized_candidate, equivalent_candidate)
        checks += 1

        target_present = rng.random() < 0.90
        checkpoint = rng.random() < 0.91
        target_busy = rng.random() < 0.05
        identity = rng.randrange(1, 1_000_000)
        selected_primary_identity = 10_000_000 + index
        primary_before_matches = rng.random() < 0.98
        primary_before_identity = (
            selected_primary_identity if primary_before_matches else selected_primary_identity + 1
        )
        primary_before_ambiguous = rng.random() < 0.01
        target_before_ambiguous = rng.random() < 0.01

        before_list = [Volume(active_root, primary_before_identity, True)]
        if primary_before_ambiguous:
            before_list.append(Volume(active_root, primary_before_identity + 1_000_000, True))
        if target_present and not same_root(location_root, active_root):
            before_list.append(Volume(
                location_root,
                identity,
                checkpoint and not target_busy,
                rng.choice(busy_descriptor_states)
                if target_busy else ("Idle" if checkpoint else "SnapshotRequired"),
            ))
            if target_before_ambiguous:
                before_list.append(Volume(location_root, identity + 1_000_000, True))
        before_state = tuple(before_list)

        catchup_current = rng.random() < 0.88
        source_stable = rng.random() < 0.94
        after_busy = rng.random() < 0.04
        checkpoint_lost = rng.random() < 0.015
        primary_stable = rng.random() < 0.96
        target_after_ambiguous = rng.random() < 0.01
        primary_after_ambiguous = rng.random() < 0.01
        primary_after_identity = (
            primary_before_identity if primary_stable else primary_before_identity + 1
        )

        after_list = [Volume(active_root, primary_after_identity, True)]
        if primary_after_ambiguous:
            after_list.append(Volume(active_root, primary_after_identity + 1_000_000, True))
        if target_present and not same_root(location_root, active_root):
            after_identity = identity if source_stable else identity + 1
            after_checkpoint = checkpoint and not after_busy and not checkpoint_lost
            after_list.append(Volume(
                location_root,
                after_identity,
                after_checkpoint,
                rng.choice(busy_descriptor_states)
                if after_busy else ("Idle" if after_checkpoint else "SnapshotRequired"),
            ))
            if target_after_ambiguous:
                after_list.append(Volume(location_root, after_identity + 1_000_000, True))

        outcome = cross_volume_outcome(
            location_root=location_root,
            active_root=active_root,
            selected_primary_identity=selected_primary_identity,
            before=before_state,
            catchup_current=catchup_current,
            after=tuple(after_list),
        )

        if same_root(location_root, active_root):
            assert outcome == "primary"
            checks += 1
            continue

        expected_available = (
            not primary_before_ambiguous
            and primary_before_matches
            and target_present
            and not target_before_ambiguous
            and not target_busy
            and checkpoint
            and catchup_current
            and not target_after_ambiguous
            and source_stable
            and not after_busy
            and not checkpoint_lost
            and not primary_after_ambiguous
            and primary_stable
        )
        assert (outcome == "available-cross-volume") == expected_available
        checks += 1

        if primary_before_ambiguous:
            assert outcome == "unavailable-primary-ambiguous-before"
        elif not primary_before_matches:
            assert outcome == "unavailable-primary-before"
        elif target_present and target_before_ambiguous:
            assert outcome == "unavailable-target-ambiguous-before"
        elif not target_present:
            assert outcome == "outside-active-no-index"
        elif target_busy:
            assert outcome == "unavailable-busy-before"
        elif not checkpoint:
            assert outcome == "unavailable-no-checkpoint"
        elif not catchup_current:
            assert outcome == "unavailable-not-current"
        elif target_after_ambiguous:
            assert outcome == "unavailable-source-ambiguous-after"
        elif not source_stable:
            assert outcome == "unavailable-source-changed"
        elif after_busy:
            assert outcome == "unavailable-busy-after"
        elif checkpoint_lost:
            assert outcome == "unavailable-checkpoint-lost"
        elif primary_after_ambiguous:
            assert outcome == "unavailable-primary-ambiguous-after"
        elif not primary_stable:
            assert outcome == "unavailable-primary-changed"
        checks += 1

        if outcome == "available-cross-volume":
            target, target_ambiguous = select_unique(before_state, location_root)
            current, current_ambiguous = select_unique(tuple(after_list), location_root)
            primary_before, primary_before_is_ambiguous = select_unique(before_state, active_root)
            primary_after, primary_after_is_ambiguous = select_unique(tuple(after_list), active_root)
            assert not any((
                target_ambiguous,
                current_ambiguous,
                primary_before_is_ambiguous,
                primary_after_is_ambiguous,
            ))
            assert all(value is not None for value in (
                target,
                current,
                primary_before,
                primary_after,
            ))
            assert target is not None and current is not None
            assert primary_before is not None and primary_after is not None
            assert target.checkpoint and current.checkpoint
            assert target.identity == current.identity
            assert primary_before.identity == selected_primary_identity == primary_after.identity
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
    paths = {
        "helper": "src/FileOp.App/DesktopSearchEngine.KnownLocationReviewCrossVolume.cs",
        "producer": "src/FileOp.App/DesktopSearchEngine.KnownLocationReview.cs",
        "review_model": "src/FileOp.Core/Storage/StorageKnownLocationReview.cs",
        "optimizer": "src/FileOp.Core/Storage/SqliteStorageOptimizationAnalytics.cs",
        "xaml": "src/FileOp.App/StorageKnownLocationReviewView.xaml",
        "view": "src/FileOp.App/StorageKnownLocationReviewView.xaml.cs",
        "handoff": "src/FileOp.App/MainWindow.FilesReviewHandoff.cs",
        "readiness": "src/FileOp.App/MainWindow.StorageCleanupReadiness.cs",
        "discovery": "src/FileOp.Windows/Ntfs/NtfsVolumeDiscovery.cs",
        "backend": "src/FileOp.Windows/IndexingService/NtfsIndexingServiceBackend.cs",
        "protocol": "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs",
        "gate": "tools/test-local.ps1",
        "docs": "docs/known-location-review.md",
    }
    text = {name: (root / path).read_text(encoding="utf-8") for name, path in paths.items()}
    checks = 0

    required = (
        ("producer", "AnalyzeCrossVolumeKnownLocationAsync(", "cross-volume delegation"),
        ("producer", "var capturedVolumeIdentity = capturedPrimary.VolumeIdentity;", "snapshot primary identity capture"),
        ("producer", "currentPrimary.VolumeIdentity != capturedVolumeIdentity", "snapshot primary identity revalidation"),
        ("producer", "ActiveVolumeIdentity = capturedVolumeIdentity", "snapshot identity persistence"),
        ("review_model", "public ulong? ActiveVolumeIdentity { get; init; }", "snapshot identity model"),
        ("helper", "Path.GetPathRoot(fullPath)", "location volume-root derivation"),
        ("helper", "FindUniqueIndexedVolumeByRoot(", "unique descriptor selector"),
        ("helper", "out var primaryBeforeAmbiguous", "pre-capture primary ambiguity check"),
        ("helper", "out var targetAmbiguous", "pre-capture secondary ambiguity check"),
        ("helper", "out var currentAmbiguous", "post-capture secondary ambiguity check"),
        ("helper", "out var primaryAfterAmbiguous", "post-capture primary ambiguity check"),
        ("helper", "ReviewPathsEqual(response.Analysis.RootPath, fullPath)", "normalized analysis-root validation"),
        ("helper", "primaryBefore.VolumeIdentity != primaryVolume.VolumeIdentity", "selected primary identity validation"),
        ("helper", "if (!target.HasCheckpoint)", "existing-checkpoint requirement"),
        ("helper", "!IsSnapshotRequiredState(target.State)", "temporary descriptor classification"),
        ("helper", "new IndexingVolumeRequest(target.VolumeIdentity, target.RootPath)", "exact sync volume binding"),
        ("helper", "CatchUpAsync(", "bounded catch-up"),
        ("helper", "InitialCatchUpBatchLimit", "bounded catch-up limit"),
        ("helper", "if (!catchUp.IsCurrent)", "currentness requirement"),
        ("helper", "new IndexingStorageOptimizationRequest(", "volume-bound analysis"),
        ("helper", "current.VolumeIdentity != target.VolumeIdentity", "post-capture secondary identity check"),
        ("helper", "if (!current.HasCheckpoint)", "post-capture checkpoint check"),
        ("helper", "primaryAfter.VolumeIdentity != primaryBefore.VolumeIdentity", "post-capture primary identity check"),
        ("helper", "currentPrimary.VolumeIdentity != primaryBefore.VolumeIdentity", "selected primary preservation"),
        ("helper", "will not rebuild a secondary volume implicitly", "no implicit rebuild"),
        ("optimizer", "trimmed = trimmed.TrimEnd('\\\\', '/')", "optimizer path normalization"),
        ("discovery", "DriveInfo.GetDrives()", "native fixed-drive discovery"),
        ("discovery", "rootPath[1] != ':'", "drive-letter root contract"),
        ("backend", "CreateBusyDescriptor", "busy descriptor source"),
        ("backend", "HasCheckpoint: false", "busy descriptor checkpoint suppression"),
        ("backend", "context.State == VolumeState.Idle && !hasCheckpoint", "snapshot-required state derivation"),
        ("xaml", 'IsEnabled="{Binding CanReviewInFiles}"', "Files handoff UI gating"),
        ("view", "bool CanReviewInFiles", "candidate handoff capability"),
        ("handoff", "review.ActiveVolumeIdentity != volumeIdentity", "Files handoff identity binding"),
        ("handoff", "That review candidate is on another indexed volume", "programmatic Files refusal"),
        ("readiness", "review.ActiveVolumeIdentity != activeVolumeIdentity", "readiness identity binding"),
        ("readiness", "!PathsEqual(location.RootPath, requestedReviewRoot)", "exact review-root readiness binding"),
        ("readiness", "IsPathWithinRoot(matchedCandidate.Path, matchedLocation.RootPath)", "candidate owning-root containment"),
        ("readiness", "matchCount != 1", "unique readiness binding"),
        ("protocol", "public const int CurrentVersion = 8;", "protocol v8"),
        ("gate", "verify_cross_volume_known_location_review.py", "offline gate wiring"),
        ("docs", "requires catch-up to reach `IsCurrent == true`", "freshness documentation"),
        ("docs", "does **not** silently rebuild a secondary volume", "no-rebuild documentation"),
    )
    for source, needle, label in required:
        checks += require(text[source], needle, label)

    helper = text["helper"]
    if helper.count("GetVolumesAsync(_lifetimeCancellation.Token)") != 2:
        raise AssertionError("cross-volume review must read volume descriptors before and after analysis")
    if "FirstOrDefault(volume =>" in helper:
        raise AssertionError("cross-volume volume selection must not fall back to first root match")
    checks += 2

    pre_at = helper.index("var volumesBefore = await session.Client")
    primary_before_at = helper.index("var primaryBefore = FindUniqueIndexedVolumeByRoot")
    target_at = helper.index("var target = FindUniqueIndexedVolumeByRoot")
    target_checkpoint_at = helper.index("if (!target.HasCheckpoint)")
    catchup_at = helper.index("CatchUpAsync(")
    request_at = helper.index("new IndexingStorageOptimizationRequest(")
    analysis_root_at = helper.index("ReviewPathsEqual(response.Analysis.RootPath, fullPath)")
    post_at = helper.index("var volumesAfter = await session.Client")
    current_at = helper.index("var current = FindUniqueIndexedVolumeByRoot")
    current_checkpoint_at = helper.index("if (!current.HasCheckpoint)")
    primary_after_at = helper.index("var primaryAfter = FindUniqueIndexedVolumeByRoot")
    current_primary_identity_at = helper.index("currentPrimary.VolumeIdentity != primaryBefore.VolumeIdentity")
    classify_at = helper.index("StorageKnownLocationReviewClassifier.Classify(")
    if not (
        pre_at < primary_before_at < target_at < target_checkpoint_at
        < catchup_at < request_at < analysis_root_at < post_at
    ):
        raise AssertionError("pre-capture unique binding/catch-up/analysis ordering changed")
    if not (
        post_at < current_at < current_checkpoint_at < primary_after_at
        < current_primary_identity_at < classify_at
    ):
        raise AssertionError("post-capture unique binding/identity/checkpoint ordering changed")
    checks += 2

    for source, label in (("helper", "cross-volume helper"), ("producer", "known-location producer")):
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
            checks += forbid(text[source], needle, f"{label} {needle}")

    checks += forbid(text["protocol"], "CrossVolumeKnownLocation", "new protocol operation")
    checks += forbid(text["protocol"], "KnownLocationReview", "known-location protocol surface")
    checks += forbid(
        text["readiness"],
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
