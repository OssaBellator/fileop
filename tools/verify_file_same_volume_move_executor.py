#!/usr/bin/env python3
"""Zero-Actions model/source checks for same-volume regular-file and directory Move execution."""
from __future__ import annotations

import argparse
import random
import sys
from pathlib import Path

from verify_directory_same_volume_move import (
    check_repository as check_directory_move_repository,
    run_model as run_directory_move_model,
)


def executable(*, kind: str, ready: bool, regular_files: bool, same_volume: bool, local: bool) -> bool:
    return kind == 'Move' and ready and regular_files and same_volume and local


def expected_trace(entries: list[str], cancel_after: int | None = None) -> list[str]:
    trace = ['validate', 'begin-history', 'running']
    completed = 0
    for decision in entries:
        if cancel_after is not None and completed >= cancel_after:
            trace += ['cancel-requested', 'complete-cancelled']
            return trace
        if decision == 'Skip':
            trace.append('skip')
            completed += 1
            continue
        trace += ['fresh-validate', 'mutation-started', 'rename', 'commit-move']
        completed += 1
    trace.append('complete-succeeded')
    return trace


def check_properties(cases: int) -> int:
    assert executable(kind='Move', ready=True, regular_files=True, same_volume=True, local=True)
    assert not executable(kind='Copy', ready=True, regular_files=True, same_volume=True, local=True)
    assert not executable(kind='Move', ready=True, regular_files=True, same_volume=False, local=True)
    assert not executable(kind='Move', ready=True, regular_files=False, same_volume=True, local=True)
    assert expected_trace(['Ready']) == [
        'validate', 'begin-history', 'running', 'fresh-validate',
        'mutation-started', 'rename', 'commit-move', 'complete-succeeded'
    ]
    assert expected_trace(['Skip']) == [
        'validate', 'begin-history', 'running', 'skip', 'complete-succeeded'
    ]

    rng = random.Random(20260814)
    checks = 6
    for _ in range(cases):
        values = dict(
            kind=rng.choice(['Copy', 'Move']),
            ready=bool(rng.getrandbits(1)),
            regular_files=bool(rng.getrandbits(1)),
            same_volume=bool(rng.getrandbits(1)),
            local=bool(rng.getrandbits(1)),
        )
        expected = all([
            values['kind'] == 'Move',
            values['ready'],
            values['regular_files'],
            values['same_volume'],
            values['local'],
        ])
        assert executable(**values) == expected
        checks += 1

        entry_count = rng.randrange(1, 9)
        entries = [rng.choice(['Ready', 'Skip']) for _ in range(entry_count)]
        cancel_after = rng.choice([None, *range(entry_count + 1)])
        trace = expected_trace(entries, cancel_after)
        if 'rename' in trace:
            rename_index = trace.index('rename')
            assert trace[rename_index - 1] == 'mutation-started'
            assert trace[rename_index + 1] == 'commit-move'
            checks += 2
        if 'cancel-requested' in trace:
            assert not trace[-1] == 'commit-move'
            checks += 1

    return checks


def check_repository(root: Path) -> int:
    paths = {
        'executor': root / 'src/FileOp.Core/Operations/FileSameVolumeMoveOperationExecutor.cs',
        'history': root / 'src/FileOp.Core/Operations/FileOperationActionHistory.cs',
        'store': root / 'src/FileOp.Core/Operations/SqliteFileOperationActionHistoryStore.cs',
        'strategy': root / 'src/FileOp.Core/Operations/FileMoveExecutionStrategy.cs',
        'primitive': root / 'src/FileOp.Windows/Operations/WindowsFileSameVolumeMoveMutationPrimitive.cs',
        'gate': root / 'tools/test-local.ps1',
    }
    missing = [str(path) for path in paths.values() if not path.is_file()]
    if missing:
        raise FileNotFoundError(', '.join(missing))

    source = {name: path.read_text(encoding='utf-8') for name, path in paths.items()}

    executor_order = source['executor']
    begin = executor_order.index('await _historyStore.BeginAsync(validation, UtcNow())')
    mutation_started = executor_order.index('.MarkMutationStartedAsync(plan.Id, ordinal, UtcNow())')
    rename = executor_order.index('.RenameFileAsync(new FileSameVolumeMoveMutationRequest(')
    commit = executor_order.index('.CommitSameVolumeMoveAsync(')
    assert begin < mutation_started < rename < commit

    required_executor = [
        'public sealed class FileSameVolumeMoveOperationExecutor : IFileOperationExecutor',
        'plan.Kind != FileOperationKind.Move',
        'FileMoveExecutionStrategy.SameVolumeRenameRequired',
        'FileMoveExecutionStrategy.SkipOnly',
        'problem = strategy.Summary;',
        'Directory Move is not supported',
        'MarkSameVolumeMoveRecoveryRequiredAsync(',
        'receipt.DestinationIdentity != expected',
        'Cancellation is intentionally not passed beyond MutationStarted.',
    ]
    for needle in required_executor:
        assert needle in source['executor'], needle

    required_primitive = [
        'public sealed class WindowsFileSameVolumeMoveMutationPrimitive',
        'DeleteAccess = 0x00010000',
        'SetFileInformationByHandle(',
        'FileRenameInfo = 3',
        'FlagsOrReplace',
        '0); // ReplaceIfExists == FALSE.',
        'RootDirectory',
        'destinationDirectory.DangerousGetHandle()',
        'OpenRelativeSourceFile(sourceDirectory, sourceLeafName)',
        'destinationIdentity != sourceIdentity',
        'PathsEqual(postRenamePath, destinationCanonicalPath)',
        'FileShare.ReadWrite | FileShare.Delete',
        'local drive-letter paths',
    ]
    for needle in required_primitive:
        assert needle in source['primitive'], needle

    for forbidden in ['File.Move(', 'MoveFile(', 'MoveFileEx(', 'File.Copy(', 'File.Delete(']:
        assert forbidden not in source['primitive'], forbidden

    assert 'IFileMoveOperationActionHistoryStore' in source['history']
    assert 'AND action.kind = @move_kind' in source['store']
    assert 'IsLocalDrivePath(validation.SourceDirectory.CanonicalPath)' in source['strategy']
    assert 'UNC/network Move remains unsupported' in source['strategy']
    assert 'verify_file_same_volume_move_executor.py --repo-root $repoRoot --cases 50000' in source['gate']

    return 4 + len(required_executor) + len(required_primitive) + 5 + 5


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('--repo-root', type=Path, default=Path.cwd())
    parser.add_argument('--self-test-only', action='store_true')
    parser.add_argument('--cases', type=int, default=50000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error('--cases must be greater than zero')

    print(f'PASS same-volume Move executor model: {check_properties(args.cases):,} checks')
    print(
        f'PASS same-volume directory Move model: '
        f'{run_directory_move_model(args.cases):,} checks across {args.cases:,} randomized cases'
    )
    if not args.self_test_only:
        print(f'PASS same-volume Move executor source wiring: {check_repository(args.repo_root.resolve())} checks')
        print(
            f'PASS same-volume directory Move source contract: '
            f'{check_directory_move_repository(args.repo_root.resolve())} checks'
        )
    return 0


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f'FAIL: {exc}', file=sys.stderr)
        raise SystemExit(1)
