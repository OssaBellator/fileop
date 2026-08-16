#!/usr/bin/env python3
"""Portable checks for Move's stronger Windows volume-identity/product boundary."""
from __future__ import annotations

import argparse
import random
import sys
from enum import Enum, auto
from pathlib import Path


class Relationship(Enum):
    SAME = auto()
    DIFFERENT = auto()
    UNAVAILABLE = auto()


class Decision(Enum):
    PASSTHROUGH = auto()
    READY = auto()
    BLOCKED = auto()


def classify(
    *,
    is_move: bool,
    mutation_ready: bool,
    source_identity: tuple[int, int] | None,
    destination_identity: tuple[int, int] | None,
    relationship: Relationship,
) -> tuple[Decision, bool]:
    if not is_move or not mutation_ready:
        return Decision.PASSTHROUGH, False
    if source_identity is None or destination_identity is None:
        return Decision.BLOCKED, False
    if source_identity[0] != destination_identity[0]:
        return Decision.BLOCKED, False
    if relationship is Relationship.SAME:
        return Decision.READY, True
    return Decision.BLOCKED, True


def run_model(cases: int) -> int:
    checks = 0

    assert classify(
        is_move=True,
        mutation_ready=True,
        source_identity=(11, 10),
        destination_identity=(11, 20),
        relationship=Relationship.SAME,
    ) == (Decision.READY, True)
    checks += 1
    assert classify(
        is_move=True,
        mutation_ready=True,
        source_identity=(11, 10),
        destination_identity=(11, 20),
        relationship=Relationship.DIFFERENT,
    ) == (Decision.BLOCKED, True)
    checks += 1
    assert classify(
        is_move=True,
        mutation_ready=True,
        source_identity=(11, 10),
        destination_identity=(11, 20),
        relationship=Relationship.UNAVAILABLE,
    ) == (Decision.BLOCKED, True)
    checks += 1
    assert classify(
        is_move=True,
        mutation_ready=True,
        source_identity=(11, 10),
        destination_identity=(22, 20),
        relationship=Relationship.UNAVAILABLE,
    ) == (Decision.BLOCKED, False)
    checks += 1

    rng = random.Random(0x1922026)
    relationships = list(Relationship)
    for _ in range(cases):
        is_move = rng.random() < 0.85
        mutation_ready = rng.random() < 0.8
        source_identity = None if rng.random() < 0.04 else (rng.randrange(1, 8), rng.randrange(1, 100000))
        destination_identity = None if rng.random() < 0.04 else (rng.randrange(1, 8), rng.randrange(1, 100000))
        relationship = rng.choice(relationships)

        decision, queried = classify(
            is_move=is_move,
            mutation_ready=mutation_ready,
            source_identity=source_identity,
            destination_identity=destination_identity,
            relationship=relationship,
        )

        if not is_move or not mutation_ready:
            assert decision is Decision.PASSTHROUGH
            assert not queried
        elif source_identity is None or destination_identity is None:
            assert decision is Decision.BLOCKED
            assert not queried
        elif source_identity[0] != destination_identity[0]:
            assert decision is Decision.BLOCKED
            assert not queried
        else:
            assert queried
            assert decision is (
                Decision.READY if relationship is Relationship.SAME else Decision.BLOCKED
            )
        checks += 2

    return checks


def read(root: Path, relative: str) -> str:
    path = root / relative
    if not path.is_file():
        raise FileNotFoundError(str(path))
    return path.read_text(encoding="utf-8")


def require(text: str, *needles: str) -> int:
    for needle in needles:
        assert needle in text, needle
    return len(needles)


def forbid(text: str, *needles: str) -> int:
    for needle in needles:
        assert needle not in text, needle
    return len(needles)


def check_repository(root: Path) -> int:
    relationship = read(root, "src/FileOp.Windows/Operations/WindowsFileOperationVolumeRelationship.cs")
    validator = read(root, "src/FileOp.Windows/Operations/WindowsMoveOperationExecutionValidator.cs")
    wrapper = read(root, "src/FileOp.Windows/Operations/WindowsNtfsMoveOperationExecutionValidator.cs")
    aliases = read(root, "src/FileOp.App/MutationExecutionValidatorAliases.cs")
    tests = read(root, "tests/FileOp.Windows.Tests/WindowsMoveOperationExecutionValidatorTests.cs")
    real_probe_tests = read(root, "tests/FileOp.Windows.Tests/WindowsFileOperationVolumeRelationshipTests.cs")
    strategy = read(root, "src/FileOp.Core/Operations/FileMoveExecutionStrategy.cs")
    store = read(root, "src/FileOp.Core/Operations/SqliteFileCrossVolumeMoveActionHistoryStore.cs")

    checks = 0
    checks += require(
        relationship,
        "FileOperationVolumeRelationshipState",
        "SameVolume",
        "DifferentVolume",
        "Unavailable",
        "WindowsFileOperationVolumeRelationshipProbe",
        "FileIdentity expectedSourceIdentity",
        "FileIdentity expectedDestinationIdentity",
        "CreateFileW(",
        "FileFlagOpenReparsePoint",
        "GetFileInformationByHandle(",
        "observedIdentity != expectedIdentity",
        "filesystem identity changed before volume relationship proof",
        "GetFinalPathNameByHandleW(",
        "VolumeNameGuid",
        "ExtractVolumeGuidName(",
        "handle-bound Windows volume GUID",
    )
    checks += forbid(
        relationship,
        "File.Move(",
        "File.Copy(",
        "File.Delete(",
        "GetVolumeNameForVolumeMountPointW(",
    )

    checks += require(
        validator,
        "IFileOperationVolumeRelationshipProbe _volumeRelationshipProbe",
        "new WindowsFileOperationVolumeRelationshipProbe()",
        "validation.SourceDirectory.Identity is not FileIdentity sourceIdentity",
        "validation.DestinationDirectory.Identity is not FileIdentity destinationIdentity",
        "sourceIdentity.VolumeSerialNumber != destinationIdentity.VolumeSerialNumber",
        "_volumeRelationshipProbe.Query(",
        "validation.SourceDirectory.CanonicalPath",
        "sourceIdentity",
        "validation.DestinationDirectory.CanonicalPath",
        "destinationIdentity",
        "case FileOperationVolumeRelationshipState.SameVolume:",
        "case FileOperationVolumeRelationshipState.DifferentVolume:",
        "equal volume-serial evidence",
        "return Block(validation, CrossVolumeMoveDisabledSummary);",
        "RequireSupportedMutationRoots(validation, cancellationToken)",
    )
    assert validator.index("sourceIdentity.VolumeSerialNumber != destinationIdentity.VolumeSerialNumber") < validator.index("_volumeRelationshipProbe.Query(")
    assert validator.index("_volumeRelationshipProbe.Query(") < validator.index("RequireSupportedMutationRoots(validation, cancellationToken)")
    checks += 2

    checks += require(
        tests,
        "SupportedSameVolumeNamespacesReturnOriginalReadyMoveValidation",
        "MutationReadyMoveWithoutRootIdentityFailsClosedBeforeNamespaceProbe",
        "CrossVolumeMoveIsProductBlockedBeforeNamespaceProbeOrMutationHistory",
        "EqualVolumeSerialCollisionWithDifferentGuidIsBlockedAsCrossVolume",
        "EqualVolumeSerialWithoutStrongerGuidProofFailsClosedBeforeNamespaceProbe",
        "Assert.AreEqual(0, volumeProbe.QueryCalls)",
        "Assert.AreEqual(0, probe.QueryCalls)",
        "FileOperationVolumeRelationshipState.DifferentVolume",
        "FileOperationVolumeRelationshipState.Unavailable",
    )

    checks += require(
        real_probe_tests,
        "TwoDirectoriesOnSameTempVolumeResolveToSameHandleBoundGuid",
        "StaleExpectedRootIdentityMakesVolumeRelationshipUnavailable",
        "WindowsFileOperationCanonicalPathResolver",
        "new WindowsFileOperationVolumeRelationshipProbe()",
        "FileOperationVolumeRelationshipState.SameVolume",
        "FileOperationVolumeRelationshipState.Unavailable",
        "filesystem identity changed before volume relationship proof",
        "SourceVolumeGuidName",
        "DestinationVolumeGuidName",
        "StringComparison.OrdinalIgnoreCase",
    )

    # Product App composition must pass through the strengthened raw Move validator before
    # the NTFS mutation guard and before Core's still-serial-based strategy classifier can
    # authorize same-volume rename.
    checks += require(
        wrapper,
        "moveValidator ?? new WindowsMoveOperationExecutionValidator()",
        "WindowsNtfsMutationExecutionValidator",
    )
    checks += require(
        aliases,
        "global using WindowsMoveOperationExecutionValidator =",
        "FileOp.Windows.Operations.WindowsNtfsMoveOperationExecutionValidator;",
    )
    checks += require(
        strategy,
        "sourceDirectoryIdentity.VolumeSerialNumber ==",
        "destinationDirectoryIdentity.VolumeSerialNumber",
        "FileMoveExecutionStrategy.SameVolumeRenameRequired",
    )

    # The dormant composite journal is schema-v1 and still keys root identity by the
    # 32-bit serial. Equal-serial/different-GUID roots therefore remain product-blocked
    # until a later reviewed schema/identity upgrade explicitly supports them.
    checks += require(
        store,
        "CHECK(source_root_volume_serial <> destination_root_volume_serial)",
    )

    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--cases", type=int, default=50000)
    parser.add_argument("--model-only", action="store_true")
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases)
    print(f"PASS Move volume-identity model: {model_checks} checks across {args.cases} randomized cases")
    if not args.model_only:
        source_checks = check_repository(args.repo_root.resolve())
        print(f"PASS Move volume-identity source contract: {source_checks} checks")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
