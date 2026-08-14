#!/usr/bin/env python3
"""Zero-Actions model/source checks for durable cross-volume regular-file Move."""
from __future__ import annotations

import argparse
import random
import sys
from pathlib import Path

SAFE_STATES = {"Pending", "DestinationCommitted", "Moved", "Skipped"}
RECOVERY_STATES = {"CopyMutationStarted", "SourceDeleteStarted", "RecoveryRequired"}


def model_entry(
    *,
    cancel_at: str | None,
    copy_ok: bool,
    delete_prepare_ok: bool,
    pre_fidelity_ok: bool,
    post_fidelity_ok: bool,
    delete_ok: bool,
) -> tuple[str, list[str]]:
    trace = ["fresh-validate"]
    if cancel_at == "before-copy":
        return "Pending", trace + ["cancel-safe"]
    trace += ["copy-barrier", "copy"]
    if not copy_ok:
        return "RecoveryRequired", trace + ["recovery"]
    trace += ["destination-commit"]
    if cancel_at == "after-destination-commit":
        return "DestinationCommitted", trace + ["cancel-safe-source-retained"]
    trace += ["acquire-source-delete-lease"]
    if not delete_prepare_ok:
        return "DestinationCommitted", trace + ["safe-failure-source-retained"]
    trace += ["pre-barrier-fidelity-proof"]
    if not pre_fidelity_ok:
        return "DestinationCommitted", trace + ["safe-fidelity-refusal-source-retained"]
    if cancel_at == "before-delete-barrier":
        return "DestinationCommitted", trace + ["cancel-safe-source-retained"]
    trace += ["source-delete-barrier", "mint-delete-authorization", "post-barrier-fidelity-recheck"]
    if not post_fidelity_ok:
        return "RecoveryRequired", trace + ["recovery-no-delete"]
    trace += ["same-handle-delete", "release-delete-lease"]
    if not delete_ok:
        return "RecoveryRequired", trace + ["recovery"]
    return "Moved", trace + ["source-delete-commit"]


def check_properties(cases: int) -> int:
    state, trace = model_entry(
        cancel_at="after-destination-commit", copy_ok=True, delete_prepare_ok=True,
        pre_fidelity_ok=True, post_fidelity_ok=True, delete_ok=True)
    assert state == "DestinationCommitted"
    assert "source-delete-barrier" not in trace
    assert trace.index("copy-barrier") < trace.index("copy") < trace.index("destination-commit")

    state, trace = model_entry(
        cancel_at=None, copy_ok=True, delete_prepare_ok=True,
        pre_fidelity_ok=True, post_fidelity_ok=True, delete_ok=True)
    assert state == "Moved"
    ordered = [
        "destination-commit", "acquire-source-delete-lease", "pre-barrier-fidelity-proof",
        "source-delete-barrier", "mint-delete-authorization", "post-barrier-fidelity-recheck",
        "same-handle-delete", "release-delete-lease", "source-delete-commit",
    ]
    assert [trace.index(item) for item in ordered] == sorted(trace.index(item) for item in ordered)

    state, trace = model_entry(
        cancel_at=None, copy_ok=True, delete_prepare_ok=True,
        pre_fidelity_ok=False, post_fidelity_ok=True, delete_ok=True)
    assert state == "DestinationCommitted"
    assert "source-delete-barrier" not in trace and "same-handle-delete" not in trace

    state, trace = model_entry(
        cancel_at=None, copy_ok=True, delete_prepare_ok=True,
        pre_fidelity_ok=True, post_fidelity_ok=False, delete_ok=True)
    assert state == "RecoveryRequired"
    assert "source-delete-barrier" in trace and "same-handle-delete" not in trace

    rng = random.Random(20260815)
    checks = 22
    cancel_points = [None, "before-copy", "after-destination-commit", "before-delete-barrier"]
    for _ in range(cases):
        state, trace = model_entry(
            cancel_at=rng.choice(cancel_points),
            copy_ok=bool(rng.getrandbits(1)),
            delete_prepare_ok=bool(rng.getrandbits(1)),
            pre_fidelity_ok=bool(rng.getrandbits(1)),
            post_fidelity_ok=bool(rng.getrandbits(1)),
            delete_ok=bool(rng.getrandbits(1)),
        )
        if "same-handle-delete" in trace:
            assert "source-delete-barrier" in trace
            assert trace.index("destination-commit") < trace.index("source-delete-barrier") < trace.index("same-handle-delete")
            assert "pre-barrier-fidelity-proof" in trace and "post-barrier-fidelity-recheck" in trace
            checks += 5
        if "cancel-safe-source-retained" in trace:
            assert state == "DestinationCommitted" and "same-handle-delete" not in trace
            checks += 2
        if "safe-fidelity-refusal-source-retained" in trace:
            assert state == "DestinationCommitted"
            assert "source-delete-barrier" not in trace and "same-handle-delete" not in trace
            checks += 3
        if "recovery-no-delete" in trace:
            assert state == "RecoveryRequired"
            assert "source-delete-barrier" in trace and "same-handle-delete" not in trace
            checks += 3
        if state in RECOVERY_STATES:
            assert "recovery" in trace or "recovery-no-delete" in trace
            checks += 1
        if state == "Moved":
            assert trace[-1] == "source-delete-commit"
            checks += 1
        assert state in SAFE_STATES | RECOVERY_STATES
        checks += 1
    return checks


def must_contain(text: str, *needles: str) -> int:
    for needle in needles:
        assert needle in text, needle
    return len(needles)


def must_not_contain(text: str, *needles: str) -> int:
    for needle in needles:
        assert needle not in text, needle
    return len(needles)


def check_repository(root: Path) -> int:
    files = {
        "history": "src/FileOp.Core/Operations/FileCrossVolumeMoveActionHistory.cs",
        "store": "src/FileOp.Core/Operations/SqliteFileCrossVolumeMoveActionHistoryStore.cs",
        "contract": "src/FileOp.Core/Operations/FileCrossVolumeMoveSourceDelete.cs",
        "executor": "src/FileOp.Core/Operations/FileCrossVolumeMoveOperationExecutor.cs",
        "fidelity": "src/FileOp.Core/Operations/FileCrossVolumeMoveFidelity.cs",
        "security": "src/FileOp.Core/Operations/FileCrossVolumeMoveSecurityPolicy.cs",
        "preservation": "src/FileOp.Core/Operations/FileCrossVolumeMovePreservationPolicy.cs",
        "raw": "src/FileOp.Windows/Operations/WindowsFileCrossVolumeMoveSourceDeletePrimitive.cs",
        "wrapper": "src/FileOp.Windows/Operations/WindowsFidelityVerifiedFileCrossVolumeMoveSourceDeletePrimitive.cs",
        "native_fidelity": "src/FileOp.Windows/Operations/WindowsFileCrossVolumeMoveFidelityVerifier.cs",
        "production_validator": "src/FileOp.Windows/Operations/WindowsMoveOperationExecutionValidator.cs",
        "production_validator_tests": "tests/FileOp.Windows.Tests/WindowsMoveOperationExecutionValidatorTests.cs",
        "fidelity_tests": "tests/FileOp.Windows.Tests/FileCrossVolumeMoveFidelityTests.cs",
        "share_tests": "tests/FileOp.Windows.Tests/FileCrossVolumeMoveFidelityShareCompatibilityTests.cs",
        "lease_tests": "tests/FileOp.Windows.Tests/FileCrossVolumeMoveFidelityLeaseTests.cs",
        "history_tests": "tests/FileOp.Windows.Tests/FileCrossVolumeMoveActionHistoryInvariantTests.cs",
        "corruption_tests": "tests/FileOp.Windows.Tests/FileCrossVolumeMovePersistedCorruptionTests.cs",
        "ui": "src/FileOp.App/FilesView.Move.cs",
        "xaml": "src/FileOp.App/FilesView.xaml",
        "readme": "README.md",
        "files_doc": "docs/files-browser.md",
        "cross_doc": "docs/file-cross-volume-move.md",
        "gate": "tools/test-local.ps1",
    }
    source: dict[str, str] = {}
    for key, relative in files.items():
        path = root / relative
        if not path.is_file():
            raise FileNotFoundError(str(path))
        source[key] = path.read_text(encoding="utf-8")

    checks = 0
    checks += must_contain(
        source["history"], "CopyMutationStarted", "DestinationCommitted",
        "SourceDeleteStarted", "RecoveryRequired",
        "A cross-volume Move failure after the source-delete barrier must be RecoveryRequired, not Failed.")
    checks += must_contain(
        source["store"], "file_cross_volume_move_schema", "file_cross_volume_move_actions",
        "file_cross_volume_move_entries", "CHECK(source_root_volume_serial <> destination_root_volume_serial)",
        "MarkCopyMutationStartedAsync(", "CommitDestinationAsync(",
        "MarkSourceDeleteStartedAsync(", "CommitSourceDeletedAsync(")
    checks += must_not_contain(source["store"], "UPDATE file_operation_actions", "UPDATE file_operation_action_entries")

    executor = source["executor"]
    ordered_calls = [
        ".MarkCopyMutationStartedAsync(", ".CopyNewFileAsync(", ".CommitDestinationAsync(",
        ".AcquireAsync(sourceDeleteRequest", ".MarkSourceDeleteStartedAsync(",
        "new FileCrossVolumeMoveSourceDeleteAuthorization(",
        ".MarkDeletePendingAsync(authorization, CancellationToken.None)", ".CommitSourceDeletedAsync(",
    ]
    assert [executor.index(item) for item in ordered_calls] == sorted(executor.index(item) for item in ordered_calls)
    checks += len(ordered_calls)
    checks += must_contain(
        executor, "FileMoveExecutionStrategy.CrossVolumeCopyDeleteRequired",
        "DestinationCommitted is deliberately a cancellation-safe checkpoint.",
        "No cancellation is passed beyond SourceDeleteStarted", "DeleteAccessCapabilityHeld",
        "CancellationToken.None", "MarkRecoveryRequiredAsync(")
    checks += must_not_contain(executor, "File.Delete(", "File.Move(", "overwrite: true")

    checks += must_contain(
        source["contract"], "SourceDeleteMutationAuthorized => false",
        "SourceDeleteBarrierSatisfied => true", "SourceDeleteMutationAuthorized => true",
        "IFileCrossVolumeMoveSourceDeleteLease")

    checks += must_contain(
        source["security"], "DestinationDefaultInherited", "PreservesSourceSecurityDescriptor = false",
        "destination-default security semantics", "rather than preserving the source security descriptor")
    checks += must_contain(
        source["preservation"],
        "MovesSelectedSourceDirectoryEntryOnly = true",
        "PreservesHardLinkTopology = false",
        "RequiresStableMainStreamThroughDelete = true",
        "RequiresNoSourceNamedDataStreamsAtProof = true",
        "RequiresNoSourceExtendedAttributesAtProof = true",
        "GuaranteesAtomicConcurrentMetadataMutationCapture = false",
        "hard-link topology is not recreated across volumes",
    )

    checks += must_contain(
        source["fidelity"], "SourceContentChanged", "DestinationContentChanged",
        "StableBasicMetadataMismatch", "SourceNamedDataStreams", "SourceExtendedAttributes",
        "CanDeleteSourceAfterDurableBarrier", "FileCrossVolumeMovePreservationPolicy",
        "hard-link topology is path-entry state")
    checks += must_not_contain(
        source["fidelity"], "SecurityDescriptorEvidenceIncomplete", "SecurityDescriptorMismatch",
        "SourceSecurityDescriptor", "DestinationSecurityDescriptor",
        "SourceHardLinks =", "DestinationHardLinks =",
        "blockers.Add(FileCrossVolumeMoveFidelityBlocker.DestinationNamedDataStreams)",
        "blockers.Add(FileCrossVolumeMoveFidelityBlocker.DestinationExtendedAttributes)")

    checks += must_contain(
        source["raw"], "Delete | FileReadAttributes | Synchronize", "FileShare.Read,",
        "NtCreateFile(", "NtSetInformationFile(", "FileDispositionInformationEx",
        "authorization.IsBoundTo(Evidence)")
    checks += must_contain(
        source["wrapper"], "IFileCrossVolumeMoveFidelityVerifier",
        "new WindowsFileCrossVolumeMoveSourceDeletePrimitive()",
        "new WindowsFileCrossVolumeMoveFidelityVerifier()", ".VerifyAsync(request, cancellationToken)",
        ".VerifyAsync(_request, CancellationToken.None)", "before the source-delete barrier",
        "after the durable source-delete barrier",
        "inner.MarkDeletePendingAsync(authorization, CancellationToken.None)")

    checks += must_contain(
        source["native_fidelity"], "FileShare.Read | FileShare.Delete",
        "NtQueryInformationFile(", "FileEaInformation",
        "WindowsFileNamedDataStreamTopologyDigest.Read(", "HashMainStream(")
    checks += must_not_contain(
        source["native_fidelity"], "GetSecurityInfo(", "BackupSecurityInformation",
        "AccessSystemSecurity", "GetSecurityDescriptorLength(", "LocalFree(", "GetKernelObjectSecurity(")

    validator = source["production_validator"]
    checks += must_contain(
        validator, "CrossVolumeMoveDisabledSummary", "MissingRootIdentitySummary",
        "TryClassifyVolumeRelationship(", "return Block(validation, MissingRootIdentitySummary);",
        "if (isCrossVolume)", "return Block(validation, CrossVolumeMoveDisabledSummary);",
        "final mutation-stability boundary is still under review", "Choose a destination on the same volume",
        "No durable history, destination Copy, or source-delete mutation")
    assert validator.index("TryClassifyVolumeRelationship(validation") < validator.index("RequireSupportedMutationRoots(validation")
    assert "throw new InvalidOperationException" not in validator
    checks += 2

    checks += must_contain(
        source["production_validator_tests"], "MutationReadyMoveWithoutRootIdentityFailsClosedBeforeNamespaceProbe",
        "CrossVolumeMoveIsProductBlockedBeforeNamespaceProbeOrMutationHistory",
        "Assert.AreEqual(0, probe.QueryCalls)", "SupportedSameVolumeNamespacesReturnOriginalReadyMoveValidation")
    checks += must_contain(
        source["fidelity_tests"], "EquivalentOrdinaryPinnedFilesMayReachLaterDeleteBarrier",
        "ContentDriftOnEitherPinnedObjectBlocksSourceDeletion",
        "SourceNamedStreamsAndExtendedAttributesStillBlockDestructiveCompletion",
        "HardLinkCountDoesNotBlockMovingTheSelectedDirectoryEntry",
        "DestinationOnlyStreamsOrEasDoNotRepresentLostSourceSemantics",
        "PreservationPolicyIsExplicitAboutSelectedEntryAndConcurrentMetadataAtomicity",
        "DestinationDefaultInherited", "PreservesSourceSecurityDescriptor")
    checks += must_contain(
        source["share_tests"],
        "FidelityReadReopenMustShareDeleteWhileDeleteCapabilityIsLive",
        "PreExistingMainStreamWriterPreventsDeleteCapabilityAcquisition",
        "WritableMainStreamMappingPreventsDeleteCapabilityAcquisitionEvenAfterFileHandleCloses",
        "DeleteCapabilityPreventsNewMainStreamWriterUntilReleased",
        "DeleteCapabilityDoesNotPretendShareModeFreezesAttributesOrEas",
        "DeletingOneHardLinkLeavesOtherSourceVolumeEntryValid",
        "FileWriteAttributes",
        "FileWriteEa",
        "CreateFileMappingW(",
        "CreateHardLinkW(",
        "ErrorSharingViolation",
    )
    checks += must_contain(
        source["lease_tests"], "PreBarrierFidelityRefusalRetainsSourceWithoutDeleteBarrier",
        "PostBarrierFidelityRefusalRequiresRecoveryWithoutInnerDeleteMutation",
        "TwoPositiveFidelityProofsPermitExactlyOneInnerDeleteMutation", "Assert.AreEqual(0, inner.MutationCount)")
    checks += must_contain(
        source["history_tests"], "FailedStateCannotHideUnresolvedCopyMutationBarrier",
        "SourceDeleteRecoveryRequiresEarlierCommittedDestinationChronology",
        "SafeFailureAfterDestinationCommitRetainsSourceDuplicateWithoutRecovery")
    checks += must_contain(
        source["corruption_tests"], "LoaderRejectsSafeFailureThatHidesUnresolvedCopyBarrier",
        "UPDATE file_cross_volume_move_entries", "ThrowsExactlyAsync<ArgumentException>")

    checks += must_contain(
        source["ui"], "new WindowsMoveOperationExecutionValidator()",
        "new WindowsFidelityVerifiedFileCrossVolumeMoveSourceDeletePrimitive()")
    checks += must_not_contain(source["ui"], "new WindowsFileCrossVolumeMoveSourceDeletePrimitive()")
    checks += must_contain(
        source["xaml"], "same-volume regular-file Move can execute; cross-volume and directory Move remain disabled",
        "cross-volume Move remains product-disabled pending fidelity hardening")

    raw_ctor = "new WindowsFileCrossVolumeMoveSourceDeletePrimitive("
    wrapper_path = (root / files["wrapper"]).resolve()
    for path in (root / "src").rglob("*.cs"):
        if path.resolve() == wrapper_path:
            continue
        assert raw_ctor not in path.read_text(encoding="utf-8"), f"raw cross-volume source-delete bypass: {path}"
        checks += 1

    checks += must_contain(
        source["readme"], "Different-volume Move is currently product-disabled",
        "not currently reachable through production Move validation",
        "Main/unnamed-stream stability is the destructive invariant")
    checks += must_contain(
        source["files_doc"], "Different-volume Move is currently product-disabled",
        "dormant cross-volume transaction contract", "Preflight deliberately does not carry stable filesystem root identities")
    checks += must_contain(
        source["cross_doc"], "Production Files execution does not currently enter this transaction.",
        "different root volume serials return `Blocked`",
        "MovesSelectedSourceDirectoryEntryOnly",
        "A generic oplock is **not** a prerequisite",
        "These are transaction-engine semantics, not current Files production outcomes.")
    checks += must_contain(
        source["gate"], "verify_file_cross_volume_move.py --repo-root $repoRoot --cases 50000",
        "verify_files_same_volume_move_ui.py --repo-root $repoRoot --cases 50000")
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--self-test-only", action="store_true")
    parser.add_argument("--cases", type=int, default=50000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error("--cases must be greater than zero")
    print(f"PASS cross-volume Move transaction model: {check_properties(args.cases):,} checks")
    if not args.self_test_only:
        print(f"PASS cross-volume Move source contract: {check_repository(args.repo_root.resolve()):,} checks")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
