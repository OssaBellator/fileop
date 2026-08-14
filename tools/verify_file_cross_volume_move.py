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


def model_entry(*, cancel_at: str | None, copy_ok: bool, delete_prepare_ok: bool, delete_ok: bool) -> tuple[str, list[str]]:
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
    if cancel_at == 'before-delete-barrier':
        return 'DestinationCommitted', trace + ['cancel-safe-source-retained']

    trace += ['source-delete-barrier', 'mint-delete-authorization', 'same-handle-delete', 'release-delete-lease']
    if not delete_ok:
        return 'RecoveryRequired', trace + ['recovery']

    return 'Moved', trace + ['source-delete-commit']


def check_properties(cases: int) -> int:
    state, trace = model_entry(
        cancel_at='after-destination-commit',
        copy_ok=True,
        delete_prepare_ok=True,
        delete_ok=True,
    )
    assert state == 'DestinationCommitted'
    assert 'source-delete-barrier' not in trace
    assert trace.index('copy-barrier') < trace.index('copy') < trace.index('destination-commit')

    state, trace = model_entry(
        cancel_at=None,
        copy_ok=True,
        delete_prepare_ok=True,
        delete_ok=True,
    )
    assert state == 'Moved'
    assert trace.index('destination-commit') < trace.index('acquire-source-delete-lease')
    assert trace.index('acquire-source-delete-lease') < trace.index('source-delete-barrier')
    assert trace.index('source-delete-barrier') < trace.index('mint-delete-authorization')
    assert trace.index('mint-delete-authorization') < trace.index('same-handle-delete')
    assert trace.index('same-handle-delete') < trace.index('release-delete-lease') < trace.index('source-delete-commit')

    rng = random.Random(20260814)
    checks = 10
    cancel_points = [None, 'before-copy', 'after-destination-commit', 'before-delete-barrier']
    for _ in range(cases):
        cancel_at = rng.choice(cancel_points)
        copy_ok = bool(rng.getrandbits(1))
        delete_prepare_ok = bool(rng.getrandbits(1))
        delete_ok = bool(rng.getrandbits(1))
        state, trace = model_entry(
            cancel_at=cancel_at,
            copy_ok=copy_ok,
            delete_prepare_ok=delete_prepare_ok,
            delete_ok=delete_ok,
        )

        if 'same-handle-delete' in trace:
            assert 'source-delete-barrier' in trace
            assert trace.index('source-delete-barrier') < trace.index('same-handle-delete')
            assert 'destination-commit' in trace
            assert trace.index('destination-commit') < trace.index('source-delete-barrier')
            checks += 3
        if 'cancel-safe-source-retained' in trace:
            assert state == 'DestinationCommitted'
            assert 'same-handle-delete' not in trace
            checks += 2
        if state in RECOVERY_STATES:
            assert 'recovery' in trace
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
        'delete_primitive': root / 'src/FileOp.Windows/Operations/WindowsFileCrossVolumeMoveSourceDeletePrimitive.cs',
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
        'Cross-volume Move requires a separate composite transaction journal',
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
        'no cancellation is passed beyond SourceDeleteStarted',
        'CancellationToken.None',
        'The copied destination remains committed and the original source was retained',
        'MarkRecoveryRequiredAsync(',
    ]
    for needle in required_executor:
        assert needle.lower() in executor.lower(), needle

    required_contract = [
        'SourceDeleteMutationAuthorized => false',
        'SourceDeleteBarrierSatisfied => true',
        'SourceDeleteMutationAuthorized => true',
        'IsBoundTo(FileCrossVolumeMoveSourceDeleteEvidence evidence)',
        'IFileCrossVolumeMoveSourceDeleteLease',
    ]
    for needle in required_contract:
        assert needle in source['delete_contract'], needle

    required_primitive = [
        'FileShare.Read,',
        'HashMainStream(destinationFile, cancellationToken)',
        'destinationContentFingerprint',
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

    assert 'verify_file_cross_volume_move.py --repo-root $repoRoot --cases 50000' in source['gate']
    return 8 + len(required_history) + len(required_store) + 2 + 8 + len(required_executor) + len(required_contract) + len(required_primitive) + 12 + 1


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('--repo-root', type=Path, default=Path.cwd())
    parser.add_argument('--self-test-only', action='store_true')
    parser.add_argument('--cases', type=int, default=50000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error('--cases must be greater than zero')

    print(f'PASS cross-volume Move composite model: {check_properties(args.cases):,} checks')
    if not args.self_test_only:
        print(f'PASS cross-volume Move source wiring: {check_repository(args.repo_root.resolve())} checks')
    return 0


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f'FAIL: {exc}', file=sys.stderr)
        raise SystemExit(1)
