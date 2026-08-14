#!/usr/bin/env python3
"""Zero-Actions model/source checks for Files same-volume regular-file Move UI."""
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


def classify_for_ui(strategy: str) -> str:
    if strategy == 'SameVolumeRenameRequired':
        return 'Execute'
    if strategy == 'SkipOnly':
        return 'ExecuteNoMutation'
    if strategy == 'CrossVolumeCopyDeleteRequired':
        return 'KeepQueuedCrossVolume'
    return 'KeepQueuedBlocked'


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
    assert classify_for_ui('SameVolumeRenameRequired') == 'Execute'
    assert classify_for_ui('SkipOnly') == 'ExecuteNoMutation'
    assert classify_for_ui('CrossVolumeCopyDeleteRequired') == 'KeepQueuedCrossVolume'

    rng = random.Random(20260814)
    checks = 6
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
        disposition = classify_for_ui(strategy)
        assert (disposition.startswith('Execute')) == strategy in {'SameVolumeRenameRequired', 'SkipOnly'}
        assert ('KeepQueued' in disposition) == strategy in {'CrossVolumeCopyDeleteRequired', 'Blocked'}
        checks += 2
    return checks


def check_repository(root: Path) -> int:
    paths = {
        'xaml': root / 'src/FileOp.App/FilesView.xaml',
        'move': root / 'src/FileOp.App/FilesView.Move.cs',
        'copy': root / 'src/FileOp.App/FilesView.Copy.cs',
        'source': root / 'src/FileOp.App/MainWindow.StorageSourceIdentity.cs',
        'executor': root / 'src/FileOp.Core/Operations/FileSameVolumeMoveOperationExecutor.cs',
        'primitive': root / 'src/FileOp.Windows/Operations/WindowsFileSameVolumeMoveMutationPrimitive.cs',
        'move_validator': root / 'src/FileOp.Windows/Operations/WindowsMoveOperationExecutionValidator.cs',
        'namespace': root / 'src/FileOp.Windows/Operations/WindowsFileOperationNamespaceCapability.cs',
        'tests': root / 'tests/FileOp.Windows.Tests/WindowsMoveOperationExecutionValidatorTests.cs',
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
        'cross-volume and directory Move remain disabled',
    ]
    for needle in required_xaml:
        assert needle in source['xaml'], needle

    required_move = [
        'public bool IsFileOperationExecutionBusy => _copyExecutionRunning || _moveExecutionRunning;',
        'if (IsFileOperationExecutionBusy ||',
        '_preflightRunning ||',
        'if (_preflightRunning)',
        'Wait for the current read-only preflight to finish before running Move.',
        'new WindowsMoveOperationExecutionValidator()',
        'FileMoveExecutionStrategyClassifier.Classify(executionValidation)',
        'FileMoveExecutionStrategy.CrossVolumeCopyDeleteRequired',
        'The queued plan was not consumed.',
        'new FileSameVolumeMoveOperationExecutor(',
        'new WindowsFileSameVolumeMoveMutationPrimitive()',
        'finalSnapshot = await executor.ExecuteAsync(plan, progress);',
        '_queuedOperations.RemoveAll(operation => operation.Id == plan.Id);',
        '_preflightSnapshots.Remove(plan.Id);',
        'RequestRefreshForMoveEndpoint(plan.Intent.SourceDirectoryPath);',
        'RequestRefreshForMoveEndpoint(plan.Intent.DestinationDirectoryPath);',
        'await executor.RequestCancellationAsync(operationId)',
        'ReassertOperationExecutionBusyAfterSourceChange()',
        'will not replay, rollback or reinterpret the original operation ID automatically',
    ]
    for needle in required_move:
        assert needle in source['move'], needle
    assert source['move'].count('new WindowsMoveOperationExecutionValidator()') >= 2

    required_validator = [
        'public sealed class WindowsMoveOperationExecutionValidator : IFileOperationExecutionValidator',
        'IFileOperationNamespaceCapabilityProbe? namespaceProbe = null',
        '_inner = inner ?? new WindowsFileOperationExecutionValidator();',
        'validation.SourceDirectory.CanonicalPath',
        'validation.DestinationDirectory.CanonicalPath',
        '_namespaceProbe.QueryDirectory(path)',
        'if (!capability.CanUseCurrentMutationModel)',
        'plan.Kind != FileOperationKind.Move || !validation.CanBeginMutation',
        'FileOperationExecutionValidationStatus.Blocked',
        'before durable mutation history',
        'No MutationStarted record or filesystem mutation was created',
    ]
    for needle in required_validator:
        assert needle in source['move_validator'], needle

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

    required_tests = [
        'CaseSensitiveSourceBlocksReadyMoveBeforeMutationHistory',
        'CaseSensitiveDestinationBlocksReadyMoveAfterCheckingBothRoots',
        'UnavailableNamespaceCapabilityBlocksReadyMove',
        'SupportedNamespacesReturnOriginalReadyMoveValidation',
        'NonMoveValidationDoesNotInvokeMoveNamespaceCapability',
        'Assert.AreEqual(1, probe.QueryCalls);',
        'Assert.AreEqual(2, probe.QueryCalls);',
        'Assert.AreEqual(0, probe.QueryCalls);',
        'No MutationStarted record',
    ]
    for needle in required_tests:
        assert needle in source['tests'], needle

    assert source['move'].index('FileMoveExecutionStrategyClassifier.Classify(executionValidation)') < source['move'].index('_moveExecutionRunning = true;')
    assert '_filesView.ReassertOperationExecutionBusyAfterSourceChange();' in source['source']
    assert 'FileSameVolumeMoveOperationExecutor : IFileOperationExecutor' in source['executor']
    assert 'SetFileInformationByHandle(' in source['primitive']
    for forbidden in ['File.Move(', 'File.Copy(', 'File.Delete(']:
        assert forbidden not in source['move'], forbidden
    assert 'verify_files_same_volume_move_ui.py --repo-root $repoRoot --cases 50000' in source['gate']

    return (
        len(required_xaml) + len(required_move) + len(required_validator) +
        len(required_namespace) + len(required_tests) + 9
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('--repo-root', type=Path, default=Path.cwd())
    parser.add_argument('--self-test-only', action='store_true')
    parser.add_argument('--cases', type=int, default=50000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error('--cases must be greater than zero')
    print(f'PASS Files same-volume Move UI model: {check_properties(args.cases):,} checks')
    if not args.self_test_only:
        print(f'PASS Files same-volume Move UI source wiring: {check_repository(args.repo_root.resolve())} checks')
    return 0


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ET.ParseError, ValueError) as exc:
        print(f'FAIL: {exc}', file=sys.stderr)
        raise SystemExit(1)
