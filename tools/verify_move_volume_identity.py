#!/usr/bin/env python3
"""Source checks for Move volume-relationship classification and serial-collision safety."""
from __future__ import annotations

import argparse
import sys
from pathlib import Path


def read(root: Path, relative: str) -> str:
    path = root / relative
    if not path.is_file():
        raise FileNotFoundError(str(path))
    return path.read_text(encoding="utf-8")


def require(text: str, *needles: str) -> int:
    for needle in needles:
        assert needle in text, needle
    return len(needles)


def check(root: Path) -> int:
    relationship = read(root, "src/FileOp.Windows/Operations/WindowsFileOperationVolumeRelationship.cs")
    validator = read(root, "src/FileOp.Windows/Operations/WindowsMoveOperationExecutionValidator.cs")
    tests = read(root, "tests/FileOp.Windows.Tests/WindowsMoveOperationExecutionValidatorTests.cs")
    real_probe_tests = read(root, "tests/FileOp.Windows.Tests/WindowsFileOperationVolumeRelationshipTests.cs")
    store = read(root, "src/FileOp.Core/Operations/SqliteFileCrossVolumeMoveActionHistoryStore.cs")

    checks = 0
    checks += require(
        relationship,
        "FileOperationVolumeRelationshipState",
        "SameVolume",
        "DifferentVolume",
        "Unavailable",
        "WindowsFileOperationVolumeRelationshipProbe",
        "GetFinalPathNameByHandleW(",
        "VolumeNameGuid",
        "ExtractVolumeGuidName(",
        "handle-bound Windows volume GUID",
    )

    checks += require(
        validator,
        "IFileOperationVolumeRelationshipProbe _volumeRelationshipProbe",
        "new WindowsFileOperationVolumeRelationshipProbe()",
        "sourceIdentity.VolumeSerialNumber != destinationIdentity.VolumeSerialNumber",
        "_volumeRelationshipProbe.Query(",
        "case FileOperationVolumeRelationshipState.SameVolume:",
        "case FileOperationVolumeRelationshipState.DifferentVolume:",
        "equal volume-serial evidence",
        "return Block(validation, CrossVolumeMoveDisabledSummary);",
    )
    assert validator.index("sourceIdentity.VolumeSerialNumber != destinationIdentity.VolumeSerialNumber") < validator.index("_volumeRelationshipProbe.Query(")
    checks += 1

    checks += require(
        tests,
        "EqualVolumeSerialCollisionWithDifferentGuidIsBlockedAsCrossVolume",
        "EqualVolumeSerialWithoutStrongerGuidProofFailsClosedBeforeNamespaceProbe",
        "CrossVolumeMoveIsProductBlockedBeforeNamespaceProbeOrMutationHistory",
        "SupportedSameVolumeNamespacesReturnOriginalReadyMoveValidation",
        "FileOperationVolumeRelationshipState.DifferentVolume",
        "FileOperationVolumeRelationshipState.Unavailable",
        "Assert.AreEqual(0, probe.QueryCalls)",
    )

    checks += require(
        real_probe_tests,
        "TwoDirectoriesOnSameTempVolumeResolveToSameHandleBoundGuid",
        "new WindowsFileOperationVolumeRelationshipProbe()",
        "FileOperationVolumeRelationshipState.SameVolume",
        "SourceVolumeGuidName",
        "DestinationVolumeGuidName",
        "StringComparison.OrdinalIgnoreCase",
    )

    # The dormant composite journal is schema-v1 and still keys root identity by the
    # 32-bit serial. Equal-serial/different-GUID roots must therefore remain blocked until
    # a later reviewed schema/identity upgrade explicitly supports them.
    checks += require(
        store,
        "CHECK(source_root_volume_serial <> destination_root_volume_serial)",
    )

    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    args = parser.parse_args()
    checks = check(args.repo_root.resolve())
    print(f"PASS Move volume identity source contract: {checks} checks")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
