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
    history = read(root, "src/FileOp.Core/Operations/FileCrossVolumeMoveActionHistory.cs")
    history_invariants = read(root, "tests/FileOp.Windows.Tests/FileCrossVolumeMoveActionHistoryInvariantTests.cs")
    history_store_roundtrip = read(root, "tests/FileOp.Windows.Tests/FileCrossVolumeMoveRecoveryEvidenceStoreTests.cs")
    history_corruption = read(root, "tests/FileOp.Windows.Tests/FileCrossVolumeMovePersistedRecoveryCorruptionTests.cs")
    fidelity = read(root, "src/FileOp.Core/Operations/FileCrossVolumeMoveFidelity.cs")
    fidelity_tests = read(root, "tests/FileOp.Windows.Tests/FileCrossVolumeMoveFidelityTests.cs")
    metadata_contract = read(root, "tests/FileOp.Windows.Tests/FileCrossVolumeMoveBasicMetadataContractTests.cs")
    two_volume = read(root, "tests/FileOp.Windows.Tests/FileCrossVolumeMoveNativeTwoVolumeTests.cs")
    recovery = read(root, "tests/FileOp.Windows.Tests/FileCrossVolumeMoveNativeRecoveryTests.cs")
    read_only = read(root, "tests/FileOp.Windows.Tests/FileCrossVolumeMoveNativeReadOnlyTests.cs")
    share = read(root, "tests/FileOp.Windows.Tests/FileCrossVolumeMoveFidelityShareCompatibilityTests.cs")
    hard_link_path = read(root, "tests/FileOp.Windows.Tests/FileCrossVolumeMoveHardLinkPathBindingTests.cs")
    mutation_proof = read(root, "tests/FileOp.Windows.Tests/FileCrossVolumeMoveMutationProofTests.cs")
    security = read(root, "tests/FileOp.Windows.Tests/WindowsFileCopyMutationSecurityPolicyTests.cs")
    copy_primitive = read(root, "src/FileOp.Windows/Operations/WindowsFileCopyMutationPrimitive.cs")
    raw_delete = read(root, "src/FileOp.Windows/Operations/WindowsFileCrossVolumeMoveSourceDeletePrimitive.cs")
    wrapper = read(root, "src/FileOp.Windows/Operations/WindowsFidelityVerifiedFileCrossVolumeMoveSourceDeletePrimitive.cs")
    validator = read(root, "src/FileOp.Windows/Operations/WindowsMoveOperationExecutionValidator.cs")
    relationship = read(root, "src/FileOp.Windows/Operations/WindowsFileOperationVolumeRelationship.cs")
    validator_tests = read(root, "tests/FileOp.Windows.Tests/WindowsMoveOperationExecutionValidatorTests.cs")
    relationship_tests = read(root, "tests/FileOp.Windows.Tests/WindowsFileOperationVolumeRelationshipTests.cs")
    cross_store = read(root, "src/FileOp.Core/Operations/SqliteFileCrossVolumeMoveActionHistoryStore.cs")
    native_runner = read(root, "tools/test-cross-volume-move-native.ps1")
    security_runner = read(root, "tools/test-cross-volume-move-security.ps1")
    volume_identity_verifier = read(root, "tools/verify_move_volume_identity.py")
    native_doc = read(root, "docs/cross-volume-move-native-validation.md")

    checks = 0
    checks += require(
        history,
        "public bool DestinationIsDurablyCommitted",
        "public bool HasDestinationRecoveryEvidence",
        "public bool SourceDeleteBarrierMayBeUnresolved",
        "State == FileCrossVolumeMoveEntryState.RecoveryRequired &&",
        "SourceDeleteStartedAtUtc.HasValue",
        "recovery observation",
        "grants no cleanup, replay, or source-delete authority",
    )
    checks += require(
        history_invariants,
        "CopyBarrierRecoveryMayCaptureObservedDestinationWithoutProvingSafeCommit",
        "SourceDeleteRecoveryStillProvesEarlierSafeDestinationCommit",
        "LiveSourceDeleteBarrierIsReportedUnresolvedUntilMoved",
        "Assert.IsTrue(history.Entries[0].HasDestinationRecoveryEvidence)",
        "Assert.IsFalse(history.Entries[0].DestinationIsDurablyCommitted)",
        "Assert.IsTrue(history.Entries[0].DestinationIsDurablyCommitted)",
        "Assert.IsTrue(history.Entries[0].SourceDeleteBarrierMayBeUnresolved)",
    )
    checks += require(
        history_store_roundtrip,
        "CopyBarrierRecoveryDestinationObservationDoesNotBecomeSafeCommit",
        "SourceDeleteRecoveryStillProvesEarlierSafeDestinationCommit",
        "using var reopened = new SqliteFileCrossVolumeMoveActionHistoryStore",
        "Assert.IsFalse(persisted.Entries[0].DestinationIsDurablyCommitted)",
        "Assert.IsTrue(persisted.Entries[0].DestinationIsDurablyCommitted)",
        "Assert.IsFalse(persisted.Entries[0].SourceDeleteBarrierMayBeUnresolved)",
        "Assert.IsTrue(persisted.Entries[0].SourceDeleteBarrierMayBeUnresolved)",
    )
    checks += require(
        history_corruption,
        "LoaderRejectsSourceDeleteRecoveryWithoutCommittedDestinationEvidence",
        "LoaderRejectsRecoveryDestinationEvidenceOnWrongRootVolume",
        "SET state = 7",
        "SET destination_volume_serial = 999",
        "ThrowsExactlyAsync<ArgumentException>",
    )

    checks += require(
        metadata_contract,
        "StableCopiedAttributeMaskMatchesReviewedCopyWriterContract",
        "CopyMergeReplacesOnlyStableCopiedBitsAndRetainsDestinationOwnedBits",
        "RecoveryComparerTreatsCreationLastWriteAndStableAttributesAsAuthoritative",
        "DestinationOwnedTemporaryOfflineBitsDoNotChangeStableMetadataComparison",
        "NormalAttributeDoesNotSurviveWhenCopyMergeProducesOtherAttributes",
        "WindowsFileCopyBasicMetadata.MergeDestinationAttributes(",
        "FileBasicMetadataEvidence.StableCopiedAttributesMask",
        "SameStableMetadata",
    )

    # #185 currently refuses ReadOnly before SourceDeleteStarted. The raw disposition does
    # not opt into FILE_DISPOSITION_IGNORE_READONLY_ATTRIBUTE; #190 owns that later support.
    checks += require(
        fidelity,
        "FileAttributeReadOnly = 0x00000001u",
        "StableCopiedAttributesMask & ~FileAttributeReadOnly",
        "FILE_DISPOSITION_IGNORE_READONLY_ATTRIBUTE",
        "predictable delete refusal into recovery after SourceDeleteStarted",
    )
    checks += require(
        fidelity_tests,
        "ReadOnlyAttributeIsRefusedBeforeSourceDeleteBarrier",
        "FileCrossVolumeMoveFidelityBlocker.SourceUnsupportedAttributes",
        "FileCrossVolumeMoveFidelityBlocker.DestinationUnsupportedAttributes",
        "0x21u",
    )
    checks += require(
        read_only,
        '[TestCategory("CrossVolumeMoveNative")]',
        "ReadOnlySourceIsSafelyRetainedBeforeSourceDeleteBarrier",
        "CrossVolumeMoveSourceDeletePreparationFailed",
        "SourceUnsupportedAttributes",
        "DestinationUnsupportedAttributes",
        "Assert.IsNull(persisted.Entries[0].SourceDeleteStartedAtUtc)",
        "Assert.IsTrue(persisted.HasRetainedSourceDuplicates)",
        "Assert.IsFalse(persisted.RequiresRecovery)",
        "FileAttributes.ReadOnly",
    )

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
    checks += forbid(read_only, "new WindowsMoveOperationExecutionValidator()")

    checks += require(
        recovery,
        '[TestCategory("CrossVolumeMoveNative")]',
        "PostBarrierFidelityRefusalWithRealLeaseRequiresRecoveryAndRetainsBothFiles",
        "PostBarrierSourceNamedStreamIsDetectedByRealSecondProofAndRequiresRecovery",
        "PostBarrierSourceExtendedAttributeIsDetectedByRealSecondProofAndRequiresRecovery",
        "PostBarrierMainStreamWriterIsRejectedByLiveLeaseAndMoveCanComplete",
        "ChallengeMainStreamWriterOnSecondProofVerifier",
        "WriterWasRejected",
        "ErrorSharingViolation",
        "MutateOnSecondProofFidelityVerifier",
        "WindowsFileCrossVolumeMoveFidelityVerifier _inner = new()",
        "FileCrossVolumeMoveFidelityBlocker.SourceNamedDataStreams",
        "FileCrossVolumeMoveFidelityBlocker.SourceExtendedAttributes",
        "new WindowsFileCrossVolumeMoveSourceDeletePrimitive()",
        "new WindowsFileCopyMutationPrimitive()",
        "FileCrossVolumeMoveEntryState.RecoveryRequired",
        "FileCrossVolumeMoveTerminalState.RecoveryRequired",
        "Assert.IsFalse(\n            persisted.HasRetainedSourceDuplicates",
        "SourceDeleteStartedAtUtc",
        "NtSetEaFile(",
        '":fileop-post-barrier"',
    )
    checks += forbid(recovery, "new WindowsMoveOperationExecutionValidator()")

    checks += require(
        share,
        "PreExistingMainStreamWriterPreventsDeleteCapabilityAcquisition",
        "PreExistingDeleteCapableHandlePreventsDestructiveLeaseAcquisition",
        "WritableMainStreamMappingPreventsDeleteCapabilityAcquisitionEvenAfterFileHandleCloses",
        "DeleteCapabilityPreventsNewMainStreamWriterUntilReleased",
        "DeleteCapabilityPreventsNewDeleteCapableHandleUntilReleased",
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
        mutation_proof,
        "FidelityWrapperRejectsInnerSuccessWithoutDispositionProof",
        "NonReportingDeletePrimitive",
        "SourceDeleteMutationPerformed => false",
        "without reporting",
    )
    checks += require(
        wrapper,
        "await inner.MarkDeletePendingAsync(authorization, CancellationToken.None)",
        "if (!inner.SourceDeleteMutationPerformed)",
        "without reporting that the exact source disposition was performed",
    )

    # The raw source capability must use direct POSIX disposition on the exact opened link.
    # FILE_DISPOSITION_ON_CLOSE and IGNORE_READONLY are intentionally absent in #185.
    checks += require(
        raw_delete,
        "FileDispositionDelete",
        "FileDispositionPosixSemantics",
        "FileDispositionForceImageSectionCheck",
        "NtSetInformationFile(",
        "FileInformationClass.FileDispositionInformationEx",
        "Volatile.Write(ref _mutationPerformed, 1)",
    )
    checks += forbid(
        raw_delete,
        "FileDispositionOnClose",
        "FileDispositionIgnoreReadOnlyAttribute",
        "FileDispositionIgnoreReadonlyAttribute",
        "IgnoreReadOnlyAttribute",
    )

    # #186: the reviewed Copy creates with a default/null OBJECT_ATTRIBUTES security
    # descriptor. The ordinary-token native test then proves the source NULL DACL is not
    # cloned into the newly created destination object.
    checks += require(
        copy_primitive,
        "var attributes = new ObjectAttributes",
        "public IntPtr SecurityDescriptor;",
        "var status = NtCreateFile(",
    )
    checks += forbid(copy_primitive, "SecurityDescriptor =")
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
        "IFileOperationVolumeRelationshipProbe _volumeRelationshipProbe",
        "sourceIdentity.VolumeSerialNumber != destinationIdentity.VolumeSerialNumber",
        "_volumeRelationshipProbe.Query(",
        "sourceIdentity",
        "destinationIdentity",
        "if (isCrossVolume)",
        "return Block(validation, CrossVolumeMoveDisabledSummary);",
        "No durable history, destination Copy, or source-delete mutation",
    )
    checks += require(
        relationship,
        "FileIdentity expectedSourceIdentity",
        "FileIdentity expectedDestinationIdentity",
        "GetFileInformationByHandle(",
        "observedIdentity != expectedIdentity",
        "filesystem identity changed before volume relationship proof",
        "VolumeNameGuid",
        "handle-bound Windows volume GUID",
    )
    checks += require(
        validator_tests,
        "EqualVolumeSerialCollisionWithDifferentGuidIsBlockedAsCrossVolume",
        "EqualVolumeSerialWithoutStrongerGuidProofFailsClosedBeforeNamespaceProbe",
        "SourceIdentities",
        "DestinationIdentities",
    )
    checks += require(
        relationship_tests,
        "TwoDirectoriesOnSameTempVolumeResolveToSameHandleBoundGuid",
        "StaleExpectedRootIdentityMakesVolumeRelationshipUnavailable",
        "WindowsFileOperationCanonicalPathResolver",
        "filesystem identity changed before volume relationship proof",
    )
    checks += require(
        cross_store,
        "CHECK(source_root_volume_serial <> destination_root_volume_serial)",
    )
    checks += require(
        volume_identity_verifier,
        "PASS Move volume identity source contract",
        "StaleExpectedRootIdentityMakesVolumeRelationshipUnavailable",
        "CHECK(source_root_volume_serial <> destination_root_volume_serial)",
    )

    checks += require(
        native_runner,
        "verify_cross_volume_move_native_inventory.py",
        "verify_move_volume_identity.py",
        "must run from an ordinary unelevated token",
        "[System.Security.Principal.WindowsIdentity]::GetCurrent()",
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
        "post-barrier ADS",
        "post-barrier EA",
        "post-barrier main-stream writer",
        "read-only",
        "ordinary unelevated token",
        "schema-v1",
        "handle-bound Windows volume-GUID",
        "FileCrossVolumeMoveHardLinkPathBindingTests.CanonicalResolverPreservesTheSpecificOpenedHardLinkName",
        "WindowsFileOperationVolumeRelationshipTests.TwoDirectoriesOnSameTempVolumeResolveToSameHandleBoundGuid",
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
