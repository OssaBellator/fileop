#!/usr/bin/env python3
"""Zero-Actions model/source checks for Files regular-file Move UI."""
from __future__ import annotations

import argparse
import random
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def can_attempt(
    *,
    kind: str,
    entries: int,
    has_directory: bool,
    preflight: str,
    preflight_running: bool,
    bound: bool,
    copy_busy: bool,
    move_busy: bool,
) -> bool:
    return (
        kind == 'Move'
        and entries > 0
        and not has_directory
        and preflight == 'Ready'
        and not preflight_running
        and bound
        and not copy_busy
        and not move_busy
    )


def classify_for_ui(strategy: str, cross_volume: bool) -> str:
    # Production Windows validation currently blocks every different-volume Move
    # before mutation readiness while #186/#187 remain open. The composite engine
    # is testable infrastructure, not a reachable Files mutation path.
    if cross_volume:
        return 'KeepQueuedProductDisabled'
    if strategy == 'Blocked':
        return 'KeepQueuedBlocked'
    if strategy == 'SkipOnly':
        return 'ExecuteSameVolumeNoMutation'
    if strategy == 'SameVolumeRenameRequired':
        return 'ExecuteSameVolume'
    if strategy == 'CrossVolumeCopyDeleteRequired':
        return 'KeepQueuedVolumeMismatch'
    return 'KeepQueuedUnsupported'


def check_properties(cases: int) -> int:
    base = dict(
        kind='Move',
        entries=1,
        has_directory=False,
        preflight='Ready',
        preflight_running=False,
        bound=True,
        copy_busy=False,
        move_busy=False,
    )
    assert can_attempt(**base)
    assert not can_attempt(**{**base, 'preflight_running': True})
    assert not can_attempt(**{**base, 'kind': 'Copy'})
    assert classify_for_ui('SameVolumeRenameRequired', False) == 'ExecuteSameVolume'
    assert classify_for_ui('SkipOnly', False) == 'ExecuteSameVolumeNoMutation'
    assert classify_for_ui('CrossVolumeCopyDeleteRequired', False) == 'KeepQueuedVolumeMismatch'
    assert classify_for_ui('CrossVolumeCopyDeleteRequired', True) == 'KeepQueuedProductDisabled'
    assert classify_for_ui('SkipOnly', True) == 'KeepQueuedProductDisabled'
    assert classify_for_ui('SameVolumeRenameRequired', True) == 'KeepQueuedProductDisabled'

    rng = random.Random(20260815)
    checks = 9
    for _ in range(cases):
        values = dict(
            kind=rng.choice(['Copy', 'Move']),
            entries=rng.randrange(0, 9),
            has_directory=bool(rng.getrandbits(1)),
            preflight=rng.choice(['Missing', 'Ready', 'NeedsDecision', 'Blocked']),
            preflight_running=bool(rng.getrandbits(1)),
            bound=bool(rng.getrandbits(1)),
            copy_busy=bool(rng.getrandbits(1)),
            move_busy=bool(rng.getrandbits(1)),
        )
        expected = (
            values['kind'] == 'Move'
            and values['entries'] > 0
            and not values['has_directory']
            and values['preflight'] == 'Ready'
            and not values['preflight_running']
            and values['bound']
            and not values['copy_busy']
            and not values['move_busy']
        )
        assert can_attempt(**values) == expected
        checks += 1

        strategy = rng.choice([
            'SameVolumeRenameRequired', 'SkipOnly',
            'CrossVolumeCopyDeleteRequired', 'Blocked'
        ])
        cross_volume = bool(rng.getrandbits(1))
        disposition = classify_for_ui(strategy, cross_volume)
        if cross_volume:
            assert disposition == 'KeepQueuedProductDisabled'
        elif strategy in {'SameVolumeRenameRequired', 'SkipOnly'}:
            assert disposition.startswith('Execute')
        else:
            assert disposition.startswith('KeepQueued')
        checks += 1
    return checks


def check_repository(root: Path) -> int:
    paths = {
        'xaml': root / 'src/FileOp.App/FilesView.xaml',
        'move': root / 'src/FileOp.App/FilesView.Move.cs',
        'copy': root / 'src/FileOp.App/FilesView.Copy.cs',
        'source': root / 'src/FileOp.App/MainWindow.StorageSourceIdentity.cs',
        'same_executor': root / 'src/FileOp.Core/Operations/FileSameVolumeMoveOperationExecutor.cs',
        'cross_executor': root / 'src/FileOp.Core/Operations/FileCrossVolumeMoveOperationExecutor.cs',
        'cross_history': root / 'src/FileOp.Core/Operations/FileCrossVolumeMoveActionHistory.cs',
        'cross_store': root / 'src/FileOp.Core/Operations/SqliteFileCrossVolumeMoveActionHistoryStore.cs',
        'same_primitive': root / 'src/FileOp.Windows/Operations/WindowsFileSameVolumeMoveMutationPrimitive.cs',
        'cross_wrapper': root / 'src/FileOp.Windows/Operations/WindowsFidelityVerifiedFileCrossVolumeMoveSourceDeletePrimitive.cs',
        'move_validator': root / 'src/FileOp.Windows/Operations/WindowsMoveOperationExecutionValidator.cs',
        'namespace': root / 'src/FileOp.Windows/Operations/WindowsFileOperationNamespaceCapability.cs',
        'namespace_tests': root / 'tests/FileOp.Windows.Tests/WindowsMoveOperationExecutionValidatorTests.cs',
        'cross_tests': root / 'tests/FileOp.Windows.Tests/FileCrossVolumeMoveOperationExecutorTests.cs',
        'gate': root / 'tools/test-local.ps1',
    }
    missing = [str(path) for path in paths.values() if not path.is_file()]
    if missing:
        raise FileNotFoundError(', '.join(missing))
    source = {name: path.read_text(encoding='utf-8') for name, path in paths.items()}
    ET.fromstring(source['xaml'])

    required_xaml = [
        'x:Name="RunQueuedMoveButton"',
        'Loaded="RunQueuedMoveButton_Loaded"',
        'Content="Run Move"',
        'x:Name="CancelQueuedMoveButton"',
        'Content="Cancel Move"',
        'x:Name="MoveProgressBar"',
        'same-volume regular-file Move can execute; cross-volume and directory Move remain disabled',
        'cross-volume Move remains product-disabled pending fidelity hardening',
    ]
    for needle in required_xaml:
        assert needle in source['xaml'], needle
    for stale in [
        'reviewed regular-file Move supports same-volume rename and cross-volume Copy plus separately authorized source deletion',
        'same-volume identity-preserving rename or a cross-volume Copy plus separate durable source-delete boundary',
        'Ready regular-file Copy and reviewed regular-file Move can execute; directory Move remains disabled.',
        'pending #186/#187',
    ]:
        assert stale not in source['xaml'], stale

    required_move = [
        'public bool IsFileOperationExecutionBusy => _copyExecutionRunning || _moveExecutionRunning;',
        'if (IsFileOperationExecutionBusy ||',
        '_preflightRunning ||',
        'if (_preflightRunning)',
        'Wait for the current read-only preflight to finish before running Move.',
        'new WindowsMoveOperationExecutionValidator()',
        'FileMoveExecutionStrategyClassifier.Classify(executionValidation)',
        'sourceRootIdentity.VolumeSerialNumber != destinationRootIdentity.VolumeSerialNumber',
        'FileMoveExecutionStrategy.CrossVolumeCopyDeleteRequired',
        'new FileSameVolumeMoveOperationExecutor(',
        'new WindowsFileSameVolumeMoveMutationPrimitive()',
        'new SqliteFileCrossVolumeMoveActionHistoryStore(',
        'new FileCrossVolumeMoveOperationExecutor(',
        'new WindowsFileCopyMutationPrimitive()',
        'new WindowsFidelityVerifiedFileCrossVolumeMoveSourceDeletePrimitive()',
        'finalCrossVolumeHistory = await historyStore.GetAsync(plan.Id);',
        'FormatCrossVolumeMoveExecutionOutcome(',
        'destination copies were durably committed',
        'original source file(s) were retained',
        '_queuedOperations.RemoveAll(operation => operation.Id == plan.Id);',
        '_preflightSnapshots.Remove(plan.Id);',
        'RequestRefreshForMoveEndpoint(plan.Intent.SourceDirectoryPath);',
        'RequestRefreshForMoveEndpoint(plan.Intent.DestinationDirectoryPath);',
        'await executor.RequestCancellationAsync(operationId)',
        'ReassertOperationExecutionBusyAfterSourceChange()',
    ]
    for needle in required_move:
        assert needle in source['move'], needle
    assert source['move'].count('new WindowsMoveOperationExecutionValidator()') >= 3
    assert 'new WindowsFileCrossVolumeMoveSourceDeletePrimitive()' not in source['move']

    required_validator = [
        'public sealed class WindowsMoveOperationExecutionValidator : IFileOperationExecutionValidator',
        'IFileOperationNamespaceCapabilityProbe? namespaceProbe = null',
        '_inner = inner ?? new WindowsFileOperationExecutionValidator();',
        'CrossVolumeMoveDisabledSummary',
        'MissingRootIdentitySummary',
        'if (!TryClassifyVolumeRelationship(validation, out var isCrossVolume))',
        'return Block(validation, MissingRootIdentitySummary);',
        'if (isCrossVolume)',
        'return Block(validation, CrossVolumeMoveDisabledSummary);',
        'Repository tracking: #186 = ordinary-user security fidelity;',
        '#187 = final proof-to-mutation stability.',
        'security-fidelity and final mutation-stability',
        'Choose a destination on the same volume',
        'No durable history, destination Copy, or source-delete mutation',
        'validation.SourceDirectory.CanonicalPath',
        'validation.DestinationDirectory.CanonicalPath',
        '_namespaceProbe.QueryDirectory(path)',
        'if (!capability.CanUseCurrentMutationModel)',
        'plan.Kind != FileOperationKind.Move || !validation.CanBeginMutation',
        'FileOperationExecutionValidationStatus.Blocked',
        'before durable mutation history',
    ]
    for needle in required_validator:
        assert needle in source['move_validator'], needle
    assert source['move_validator'].index('TryClassifyVolumeRelationship(validation') < source['move_validator'].index('RequireSupportedMutationRoots(validation')
    assert 'throw new InvalidOperationException' not in source['move_validator']

    required_namespace = [
        'public interface IFileOperationNamespaceCapabilityProbe',
        'FileCaseSensitiveInformation = 71',
        'FileCsFlagCaseSensitiveDir = 0x00000001',
        'UnsupportedCaseSensitiveDirectory',
        'Unavailable',
        'if (!capability.CanUseCurrentMutationModel)',
        'throw new NotSupportedException(capability.Summary)',
    ]
    for needle in required_namespace:
        assert needle in source['namespace'], needle

    required_namespace_tests = [
        'CaseSensitiveSourceBlocksReadyMoveBeforeMutationHistory',
        'CaseSensitiveDestinationBlocksReadyMoveAfterCheckingBothRoots',
        'UnavailableNamespaceCapabilityBlocksReadyMove',
        'SupportedSameVolumeNamespacesReturnOriginalReadyMoveValidation',
        'MutationReadyMoveWithoutRootIdentityFailsClosedBeforeNamespaceProbe',
        'includeDestinationRootIdentity: false',
        'CrossVolumeMoveIsProductBlockedBeforeNamespaceProbeOrMutationHistory',
        'Assert.AreEqual(0, probe.QueryCalls)',
        'Cross-volume Move is currently disabled',
        'Choose a destination on the same volume',
        'result.Summary.Contains("#186"',
        'result.Summary.Contains("#187"',
        'NonMoveValidationDoesNotInvokeMoveNamespaceCapability',
    ]
    for needle in required_namespace_tests:
        assert needle in source['namespace_tests'], needle

    required_cross_tests = [
        'SuccessfulMoveCommitsCopyBeforeSourceDeleteBarrierAndAuthorization',
        'CancellationRequestedDuringCopyStopsAfterDestinationCommitWithoutDeleteAuthority',
        'SourceDeletePreparationFailureIsSafeFailureWithCommittedDestinationAndRetainedSource',
        'SourceDeleteMutationFailureAfterBarrierRequiresRecovery',
        'InvalidCopyReceiptNeverAcquiresSourceDeleteCapability',
        'FreshSourceIdentityChangeFailsBeforeCopyBarrier',
    ]
    for needle in required_cross_tests:
        assert needle in source['cross_tests'], needle

    assert source['move'].index('FileMoveExecutionStrategyClassifier.Classify(executionValidation)') < source['move'].index('_moveExecutionRunning = true;')
    assert '_filesView.ReassertOperationExecutionBusyAfterSourceChange();' in source['source']
    assert 'FileSameVolumeMoveOperationExecutor : IFileOperationExecutor' in source['same_executor']
    assert 'FileCrossVolumeMoveOperationExecutor : IFileOperationExecutor' in source['cross_executor']
    assert 'SetFileInformationByHandle(' in source['same_primitive']
    assert 'WindowsFidelityVerifiedFileCrossVolumeMoveSourceDeletePrimitive' in source['cross_wrapper']
    assert 'file_cross_volume_move_actions' in source['cross_store']
    assert 'SourceDeleteStarted' in source['cross_history']
    for forbidden in ['File.Move(', 'File.Copy(', 'File.Delete(']:
        assert forbidden not in source['move'], forbidden
    assert 'verify_files_same_volume_move_ui.py --repo-root $repoRoot --cases 50000' in source['gate']
    assert 'verify_file_cross_volume_move.py --repo-root $repoRoot --cases 50000' in source['gate']

    return (
        len(required_xaml) + len(required_move) + len(required_validator) +
        len(required_namespace) + len(required_namespace_tests) +
        len(required_cross_tests) + 17
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('--repo-root', type=Path, default=Path.cwd())
    parser.add_argument('--self-test-only', action='store_true')
    parser.add_argument('--cases', type=int, default=50000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error('--cases must be greater than zero')
    print(f'PASS Files regular-file Move UI model: {check_properties(args.cases):,} checks')
    if not args.self_test_only:
        print(f'PASS Files regular-file Move UI source wiring: {check_repository(args.repo_root.resolve())} checks')
    return 0


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ET.ParseError, ValueError) as exc:
        print(f'FAIL: {exc}', file=sys.stderr)
        raise SystemExit(1)
