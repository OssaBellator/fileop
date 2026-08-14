#!/usr/bin/env python3
"""Zero-Actions model/source checks for durable cross-volume regular-file Move."""
from __future__ import annotations

import argparse
import random
import sys
from pathlib import Path


SAFE_STATES = {
    'Pending',
    'DestinationCommitted',
    'Moved',
    'Skipped',
}
RECOVERY_STATES = {
    'CopyMutationStarted',
    'SourceDeleteStarted',
    'RecoveryRequired',
}


def model_entry(
    *,
    cancel_at: str | None,
    copy_ok: bool,
    delete_prepare_ok: bool,
    pre_fidelity_ok: bool,
    post_fidelity_ok: bool,
    delete_ok: bool,
) -> tuple[str, list[str]]:
    trace = ['fresh-validate']
    if cancel_at == 'before-copy':
        return 'Pending', trace + ['cancel-safe']

    trace += ['copy-barrier', 'copy']
    if not copy_ok:
        return 'RecoveryRequired', trace + ['recovery']

    trace += ['destination-commit']
    if cancel_at == 'after-destination-commit':
        return 'DestinationCommitted', trace + ['cancel-safe-source-retained']

    trace += ['acquire-source-delete-lease']
    if not delete_prepare_ok:
        return 'DestinationCommitted', trace + ['safe-failure-source-retained']

    trace += ['pre-barrier-fidelity-proof']
    if not pre_fidelity_ok:
        return 'DestinationCommitted', trace + ['safe-fidelity-refusal-source-retained']
    if cancel_at == 'before-delete-barrier':
        return 'DestinationCommitted', trace + ['cancel-safe-source-retained']

    trace += ['source-delete-barrier', 'mint-delete-authorization', 'post-barrier-fidelity-recheck']
    if not post_fidelity_ok:
        return 'RecoveryRequired', trace + ['recovery-no-delete']

    trace += ['same-handle-delete', 'release-delete-lease']
    if not delete_ok:
        return 'RecoveryRequired', trace + ['recovery']

    return 'Moved', trace + ['source-delete-commit']


def check_properties(cases: int) -> int:
    state, trace = model_entry(
        cancel_at='after-destination-commit',
        copy_ok=True,
        delete_prepare_ok=True,
        pre_fidelity_ok=True,
        post_fidelity_ok=True,
        delete_ok=True,
    )
    assert state == 'DestinationCommitted'
    assert 'source-delete-barrier' not in trace
    assert trace.index('copy-barrier') < trace.index('copy') < trace.index('destination-commit')

    state, trace = model_entry(
        cancel_at=None,
        copy_ok=True,
        delete_prepare_ok=True,
        pre_fidelity_ok=True,
        post_fidelity_ok=True,
        delete_ok=True,
    )
    assert state == 'Moved'
    assert trace.index('destination-commit') < trace.index('acquire-source-delete-lease')
    assert trace.index('acquire-source-delete-lease') < trace.index('pre-barrier-fidelity-proof')
    assert trace.index('pre-barrier-fidelity-proof') < trace.index('source-delete-barrier')
    assert trace.index('source-delete-barrier') < trace.index('mint-delete-authorization')
    assert trace.index('mint-delete-authorization') < trace.index('post-barrier-fidelity-recheck')
    assert trace.index('post-barrier-fidelity-recheck') < trace.index('same-handle-delete')
    assert trace.index('same-handle-delete') < trace.index('release-delete-lease') < trace.index('source-delete-commit')

    state, trace = model_entry(
        cancel_at=None,
        copy_ok=True,
        delete_prepare_ok=True,
        pre_fidelity_ok=False,
        post_fidelity_ok=True,
        delete_ok=True,
    )
    assert state == 'DestinationCommitted'
    assert 'safe-fidelity-refusal-source-retained' in trace
    assert 'source-delete-barrier' not in trace
    assert 'same-handle-delete' not in trace

    state, trace = model_entry(
        cancel_at=None,
        copy_ok=True,
        delete_prepare_ok=True,
        pre_fidelity_ok=True,
        post_fidelity_ok=False,
        delete_ok=True,
    )
    assert state == 'RecoveryRequired'
    assert 'source-delete-barrier' in trace
    assert 'post-barrier-fidelity-recheck' in trace
    assert 'same-handle-delete' not in trace

    rng = random.Random(20260815)
    checks = 22
    cancel_points = [None, 'before-copy', 'after-destination-commit', 'before-delete-barrier']
    for _ in range(cases):
        cancel_at = rng.choice(cancel_points)
        copy_ok = bool(rng.getrandbits(1))
        delete_prepare_ok = bool(rng.getrandbits(1))
        pre_fidelity_ok = bool(rng.getrandbits(1))
        post_fidelity_ok = bool(rng.getrandbits(1))
        delete_ok = bool(rng.getrandbits(1))
        state, trace = model_entry(
            cancel_at=cancel_at,
            copy_ok=copy_ok,
            delete_prepare_ok=delete_prepare_ok,
            pre_fidelity_ok=pre_fidelity_ok,
            post_fidelity_ok=post_fidelity_ok,
            delete_ok=delete_ok,
        )

        if 'same-handle-delete' in trace:
            assert 'source-delete-barrier' in trace
            assert trace.index('source-delete-barrier') < trace.index('same-handle-delete')
            assert 'destination-commit' in trace
            assert trace.index('destination-commit') < trace.index('source-delete-barrier')
            assert 'pre-barrier-fidelity-proof' in trace
            assert 'post-barrier-fidelity-recheck' in trace
            checks += 5
        if 'cancel-safe-source-retained' in trace:
            assert state == 'DestinationCommitted'
            assert 'same-handle-delete' not in trace
            checks += 2
        if 'safe-fidelity-refusal-source-retained' in trace:
            assert state == 'DestinationCommitted'
            assert 'source-delete-barrier' not in trace
            assert 'same-handle-delete' not in trace
            checks += 3
        if 'recovery-no-delete' in trace:
            assert state == 'RecoveryRequired'
            assert 'source-delete-barrier' in trace
            assert 'same-handle-delete' not in trace
            checks += 3
        if state in RECOVERY_STATES:
            assert 'recovery' in trace or 'recovery-no-delete' in trace
            checks += 1
        if state == 'Moved':
            assert trace[-1] == 'source-delete-commit'
            checks += 1
        assert state in SAFE_STATES | RECOVERY_STATES
        checks += 1

    return checks


def check_repository(root: Path) -> int:
    paths = {
        'history': root / 'src/FileOp.Core/Operations/FileCrossVolumeMoveActionHistory.cs',
        'store': root / 'src/FileOp.Core/Operations/SqliteFileCrossVolumeMoveActionHistoryStore.cs',
        'delete_contract': root / 'src/FileOp.Core/Operations/FileCrossVolumeMoveSourceDelete.cs',
        'executor': root / 'src/FileOp.Core/Operations/FileCrossVolumeMoveOperationExecutor.cs',
        'fidelity': root / 'src/FileOp.Core/Operations/FileCrossVolumeMoveFidelity.cs',
        'delete_primitive': root / 'src/FileOp.Windows/Operations/WindowsFileCrossVolumeMoveSourceDeletePrimitive.cs',
        'fidelity_wrapper': root / 'src/FileOp.Windows/Operations/WindowsFidelityVerifiedFileCrossVolumeMoveSourceDeletePrimitive.cs',
        'fidelity_verifier': root / 'src/FileOp.Windows/Operations/WindowsFileCrossVolumeMoveFidelityVerifier.cs',
        'fidelity_tests': root / 'tests/FileOp.Windows.Tests/FileCrossVolumeMoveFidelityTests.cs',
        'fidelity_lease_tests': root / 'tests/FileOp.Windows.Tests/FileCrossVolumeMoveFidelityLeaseTests.cs',
        'fidelity_share_tests': root / 'tests/FileOp.Windows.Tests/FileCrossVolumeMoveFidelityShareCompatibilityTests.cs',
        'history_invariant_tests': root / 'tests/FileOp.Windows.Tests/FileCrossVolumeMoveActionHistoryInvariantTests.cs',
        'history_corruption_tests': root / 'tests/FileOp.Windows.Tests/FileCrossVolumeMovePersistedCorruptionTests.cs',
        'move_ui': root / 'src/FileOp.App/FilesView.Move.cs',
        'gate': root / 'tools/test-local.ps1',
    }
    missing = [str(path) for path in paths.values() if not path.is_file()]
    if missing:
        raise FileNotFoundError(', '.join(missing))
    source = {name: path.read_text(encoding='utf-8') for name, path in paths.items()}

    required_history = [
        'CopyMutationStarted',
        'DestinationCommitted',
        'SourceDeleteStarted',
        'RecoveryRequired',
        'sourceDirectoryIdentity.VolumeSerialNumber == destinationDirectoryIdentity.VolumeSerialNumber',
        'source-delete barrier must be RecoveryRequired, not Failed',
        'source entry identity must remain bound to the durable source-root volume',
        'destination evidence must remain bound to the durable destination-root volume',
        'RequireNull(entry.CopyMutationStartedAtUtc, nameof(entry.CopyMutationStartedAtUtc));',
        'RequirePresent(entry.CopyMutationStartedAtUtc, nameof(entry.CopyMutationStartedAtUtc));',
        'Independent composite journal for cross-volume Move',
    ]
    for needle in required_history:
        assert needle in source['history'] or needle in source['store'], needle

    required_store = [
        'file_cross_volume_move_schema',
        'file_cross_volume_move_actions',
        'file_cross_volume_move_entries',
        'CHECK(source_root_volume_serial <> destination_root_volume_serial)',
        'MarkCopyMutationStartedAsync(',
        'CommitDestinationAsync(',
        'MarkSourceDeleteStartedAsync(',
        'CommitSourceDeletedAsync(',
    ]
    for needle in required_store:
        assert needle in source['store'], needle
    assert 'UPDATE file_operation_actions' not in source['store']
    assert 'UPDATE file_operation_action_entries' not in source['store']

    executor = source['executor']
    copy_barrier = executor.index('.MarkCopyMutationStartedAsync(')
    copy = executor.index('.CopyNewFileAsync(')
    destination_commit = executor.index('.CommitDestinationAsync(')
    acquire_delete = executor.index('.AcquireAsync(sourceDeleteRequest')
    delete_barrier = executor.index('.MarkSourceDeleteStartedAsync(')
    authorization = executor.index('new FileCrossVolumeMoveSourceDeleteAuthorization(')
    delete = executor.index('.MarkDeletePendingAsync(authorization, CancellationToken.None)')
    delete_commit = executor.index('.CommitSourceDeletedAsync(')
    assert copy_barrier < copy < destination_commit < acquire_delete < delete_barrier < authorization < delete < delete_commit

    required_executor = [
        'FileMoveExecutionStrategy.CrossVolumeCopyDeleteRequired',
        'DestinationCommitted is deliberately a cancellation-safe checkpoint.',
        'No cancellation is passed beyond SourceDeleteStarted',
        'CancellationToken.None',
        'The copied destination remains committed and the original source was retained',
        'MarkRecoveryRequiredAsync(',
    ]
    for needle in required_executor:
        assert needle in executor, needle

    required_contract = [
        'SourceDeleteMutationAuthorized => false',
        'SourceDeleteBarrierSatisfied => true',
        'SourceDeleteMutationAuthorized => true',
        'IsBoundTo(FileCrossVolumeMoveSourceDeleteEvidence evidence)',
        'IFileCrossVolumeMoveSourceDeleteLease',
    ]
    for needle in required_contract:
        assert needle in source['delete_contract'], needle

    required_fidelity = [
        'SourceContentChanged',
        'DestinationContentChanged',
        'StableBasicMetadataMismatch',
        'SourceUnsupportedAttributes',
        'DestinationUnsupportedAttributes',
        'SecurityDescriptorEvidenceIncomplete',
        'SecurityDescriptorMismatch',
        'SourceNamedDataStreams',
        'DestinationNamedDataStreams',
        'SourceHardLinks',
        'DestinationHardLinks',
        'SourceExtendedAttributes',
        'DestinationExtendedAttributes',
        'CanDeleteSourceAfterDurableBarrier',
        'FileBasicMetadataEvidence.StableCopiedAttributesMask',
    ]
    for needle in required_fidelity:
        assert needle in source['fidelity'], needle

    required_primitive = [
        'FileShare.Read,',
        'HashMainStream(destinationFile, cancellationToken)',
        'request.DestinationContentFingerprint',
        'Delete | FileReadAttributes | Synchronize',
        'NtCreateFile(',
        'NtSetInformationFile(',
        'FileDispositionInformationEx',
        'authorization.IsBoundTo(Evidence)',
        'DisposeNoThrow(_sourceFile)',
        'DisposeNoThrow(_destinationFile)',
    ]
    for needle in required_primitive:
        assert needle in source['delete_primitive'], needle

    required_fidelity_wrapper = [
        'IFileCrossVolumeMoveFidelityVerifier',
        'new WindowsFileCrossVolumeMoveSourceDeletePrimitive()',
        'new WindowsFileCrossVolumeMoveFidelityVerifier()',
        '.VerifyAsync(request, cancellationToken)',
        '.VerifyAsync(_request, CancellationToken.None)',
        'before the source-delete barrier',
        'after the durable source-delete barrier',
        'SourceDeleteMutationAuthorized',
        'inner.MarkDeletePendingAsync(authorization, CancellationToken.None)',
    ]
    for needle in required_fidelity_wrapper:
        assert needle in source['fidelity_wrapper'], needle

    required_fidelity_verifier = [
        'FileShare.Read | FileShare.Delete',
        'existing delete access',
        'BackupSecurityInformation',
        'AccessSystemSecurity',
        'NtQueryInformationFile(',
        'FileEaInformation',
        'WindowsFileNamedDataStreamTopologyDigest.Read(',
        'HashMainStream(',
        'GetKernelObjectSecurity(',
    ]
    for needle in required_fidelity_verifier:
        assert needle in source['fidelity_verifier'], needle
    assert source['fidelity_verifier'].count('FileShare.Read | FileShare.Delete') >= 2
    assert 'FileShare.Read,\n            IntPtr.Zero' not in source['fidelity_verifier']

    required_tests = [
        'EquivalentOrdinaryPinnedFilesMayReachLaterDeleteBarrier',
        'ContentDriftOnEitherPinnedObjectBlocksSourceDeletion',
        'MetadataOrSecurityUncertaintyBlocksSourceDeletion',
        'StreamsHardLinksAndExtendedAttributesBlockDestructiveCompletion',
    ]
    for needle in required_tests:
        assert needle in source['fidelity_tests'], needle

    required_lease_tests = [
        'PreBarrierFidelityRefusalRetainsSourceWithoutDeleteBarrier',
        'PostBarrierFidelityRefusalRequiresRecoveryWithoutInnerDeleteMutation',
        'TwoPositiveFidelityProofsPermitExactlyOneInnerDeleteMutation',
        'Assert.AreEqual(0, inner.MutationCount)',
        'FileCrossVolumeMoveTerminalState.RecoveryRequired',
    ]
    for needle in required_lease_tests:
        assert needle in source['fidelity_lease_tests'], needle

    required_share_tests = [
        'FidelityReadReopenMustShareDeleteWhileDeleteCapabilityIsLive',
        'ErrorSharingViolation',
        'FileShare.Read | FileShare.Delete',
        'incompatibleRead.IsInvalid',
        'compatibleRead.IsInvalid',
    ]
    for needle in required_share_tests:
        assert needle in source['fidelity_share_tests'], needle

    required_history_invariant_tests = [
        'FailedStateCannotHideUnresolvedCopyMutationBarrier',
        'SourceDeleteRecoveryRequiresEarlierCommittedDestinationChronology',
        'SourceIdentityMustRemainBoundToDurableSourceRootVolume',
        'DestinationEvidenceMustRemainBoundToDurableDestinationRootVolume',
        'CopyBarrierRecoveryMayDurablyCaptureObservedDestinationEvidence',
        'SafeFailureAfterDestinationCommitRetainsSourceDuplicateWithoutRecovery',
    ]
    for needle in required_history_invariant_tests:
        assert needle in source['history_invariant_tests'], needle

    required_corruption_tests = [
        'LoaderRejectsSafeFailureThatHidesUnresolvedCopyBarrier',
        'UPDATE file_cross_volume_move_entries',
        'copy_mutation_started_utc_ticks = @ticks',
        'state = 6',
        'ThrowsExactlyAsync<ArgumentException>',
    ]
    for needle in required_corruption_tests:
        assert needle in source['history_corruption_tests'], needle

    for forbidden in [
        'File.Delete(',
        'File.Move(',
        'MoveFile(',
        'MoveFileEx(',
        'ReplaceIfExists = true',
        'overwrite: true',
    ]:
        assert forbidden not in source['executor'], forbidden
        assert forbidden not in source['delete_primitive'], forbidden
        assert forbidden not in source['fidelity_wrapper'], forbidden
        assert forbidden not in source['fidelity_verifier'], forbidden

    # Files must compose the fidelity-verifying wrapper, not the raw destructive primitive.
    assert 'new WindowsFidelityVerifiedFileCrossVolumeMoveSourceDeletePrimitive()' in source['move_ui']
    assert 'new WindowsFileCrossVolumeMoveSourceDeletePrimitive());' not in source['move_ui']

    assert 'verify_file_cross_volume_move.py --repo-root $repoRoot --cases 50000' in source['gate']
    return (
        22 + len(required_history) + len(required_store) + 2 + 8 +
        len(required_executor) + len(required_contract) + len(required_fidelity) +
        len(required_primitive) + len(required_fidelity_wrapper) +
        len(required_fidelity_verifier) + len(required_tests) +
        len(required_lease_tests) + len(required_share_tests) +
        len(required_history_invariant_tests) + len(required_corruption_tests) +
        20 + 3
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('--repo-root', type=Path, default=Path.cwd())
    parser.add_argument('--self-test-only', action='store_true')
    parser.add_argument('--cases', type=int, default=50000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error('--cases must be greater than zero')

    print(f'PASS cross-volume Move composite/fidelity model: {check_properties(args.cases):,} checks')
    if not args.self_test_only:
        print(f'PASS cross-volume Move source wiring: {check_repository(args.repo_root.resolve())} checks')
    return 0


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f'FAIL: {exc}', file=sys.stderr)
        raise SystemExit(1)
