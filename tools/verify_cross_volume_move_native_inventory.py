#!/usr/bin/env python3
"""Source inventory checks for the explicit Windows cross-volume Move native gate."""
from __future__ import annotations

import argparse
import sys
from pathlib import Path


def require(text: str, *needles: str) -> int:
    for needle in needles:
        assert needle in text, needle
    return len(needles)


def forbid(text: str, *needles: str) -> int:
    for needle in needles:
        assert needle not in text, needle
    return len(needles)


def read(root: Path, relative: str) -> str:
    path = root / relative
    if not path.is_file():
        raise FileNotFoundError(str(path))
    return path.read_text(encoding="utf-8")


def check(root: Path) -> int:
    two_volume = read(root, "tests/FileOp.Windows.Tests/FileCrossVolumeMoveNativeTwoVolumeTests.cs")
    recovery = read(root, "tests/FileOp.Windows.Tests/FileCrossVolumeMoveNativeRecoveryTests.cs")
    share = read(root, "tests/FileOp.Windows.Tests/FileCrossVolumeMoveFidelityShareCompatibilityTests.cs")
    hard_link_path = read(root, "tests/FileOp.Windows.Tests/FileCrossVolumeMoveHardLinkPathBindingTests.cs")
    security = read(root, "tests/FileOp.Windows.Tests/WindowsFileCopyMutationSecurityPolicyTests.cs")
    validator = read(root, "src/FileOp.Windows/Operations/WindowsMoveOperationExecutionValidator.cs")
    native_runner = read(root, "tools/test-cross-volume-move-native.ps1")
    security_runner = read(root, "tools/test-cross-volume-move-security.ps1")
    native_doc = read(root, "docs/cross-volume-move-native-validation.md")

    checks = 0
    checks += require(
        two_volume,
        '[TestCategory("CrossVolumeMoveNative")]',
        "RealCompositeMoveCopiesThenDeletesExactSourceEntry",
        "SourceNamedStreamRefusesDestructiveCompletionAndRetainsBothFiles",
        "SourceExtendedAttributeRefusesDestructiveCompletionAndRetainsBothFiles",
        "CancellationRequestedByRealCopySettlesAtDestinationCommitted",
        "MultipleHardLinksMoveOnlySelectedSourceDirectoryEntry",
        "new WindowsFileOperationExecutionValidator()",
        "new WindowsFileCopyMutationPrimitive()",
        "new WindowsFidelityVerifiedFileCrossVolumeMoveSourceDeletePrimitive()",
        "NtSetEaFile(",
        "CreateHardLinkW(",
    )
    checks += forbid(two_volume, "new WindowsMoveOperationExecutionValidator()")

    checks += require(
        recovery,
        '[TestCategory("CrossVolumeMoveNative")]',
        "PostBarrierFidelityRefusalWithRealLeaseRequiresRecoveryAndRetainsBothFiles",
        "new WindowsFileCrossVolumeMoveSourceDeletePrimitive()",
        "new WindowsFileCopyMutationPrimitive()",
        "FileCrossVolumeMoveEntryState.RecoveryRequired",
        "FileCrossVolumeMoveTerminalState.RecoveryRequired",
        "Assert.IsFalse(\n            persisted.HasRetainedSourceDuplicates",
        "SourceDeleteStartedAtUtc",
    )
    checks += forbid(recovery, "new WindowsMoveOperationExecutionValidator()")

    checks += require(
        share,
        "PreExistingMainStreamWriterPreventsDeleteCapabilityAcquisition",
        "WritableMainStreamMappingPreventsDeleteCapabilityAcquisitionEvenAfterFileHandleCloses",
        "DeleteCapabilityPreventsNewMainStreamWriterUntilReleased",
        "DeleteCapabilityDoesNotPretendShareModeFreezesAttributesOrEas",
        "DeletingOneHardLinkLeavesOtherSourceVolumeEntryValid",
        "CreateFileMappingW(",
        "ErrorSharingViolation",
    )

    checks += require(
        hard_link_path,
        "CanonicalResolverPreservesTheSpecificOpenedHardLinkName",
        "WindowsFileOperationCanonicalPathResolver",
        "selected.Identity.Value",
        "retained.Identity.Value",
        "selected.CanonicalPath",
        "retained.CanonicalPath",
    )

    checks += require(
        security,
        '[TestCategory("CrossVolumeMoveSecurityNative")]',
        "ReviewedCopyCreatesDestinationWithDefaultSecurityInsteadOfCloningSourceNullDacl",
        "new WindowsFileCopyMutationPrimitive()",
        "SetNamedSecurityInfoW(",
        "GetNamedSecurityInfoW(",
    )
    checks += forbid(
        security,
        "SaclSecurityInformation",
        "AccessSystemSecurity",
        "BackupSecurityInformation",
    )

    checks += require(
        validator,
        "if (isCrossVolume)",
        "return Block(validation, CrossVolumeMoveDisabledSummary);",
        "No durable history, destination Copy, or source-delete mutation",
    )

    checks += require(
        native_runner,
        "verify_cross_volume_move_native_inventory.py",
        "FILEOP_CROSS_VOLUME_MOVE_SOURCE_ROOT",
        "FILEOP_CROSS_VOLUME_MOVE_DESTINATION_ROOT",
        'TestCategory=CrossVolumeMoveNative',
    )
    checks += require(
        security_runner,
        "must run from an ordinary unelevated token",
        'TestCategory=CrossVolumeMoveSecurityNative',
    )
    checks += require(
        native_doc,
        "Post-barrier fidelity refusal with the real Windows lease",
        "FileCrossVolumeMoveHardLinkPathBindingTests.CanonicalResolverPreservesTheSpecificOpenedHardLinkName",
        "tools/test-cross-volume-move-security.ps1",
        "tools/test-cross-volume-move-native.ps1",
    )

    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    args = parser.parse_args()
    count = check(args.repo_root.resolve())
    print(f"PASS cross-volume Move native matrix inventory: {count} checks")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
