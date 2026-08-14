#!/usr/bin/env python3
"""Zero-Actions model/source checks for same-volume file Move durable history."""
from __future__ import annotations

import argparse
import random
import sys
from dataclasses import dataclass, replace
from pathlib import Path


@dataclass(frozen=True)
class Entry:
    state: str
    source_volume: int
    source_ref: int
    destination_volume: int | None = None
    destination_ref: int | None = None
    is_directory: bool = False
    undo: str = 'None'
    has_copy_fingerprint: bool = False


@dataclass(frozen=True)
class History:
    kind: str
    source_root_volume: int
    destination_root_volume: int
    terminal: str | None
    entry: Entry


def can_settle_same_volume_move(history: History, observed: tuple[int, int] | None) -> bool:
    entry = history.entry
    if history.kind != 'Move' or history.terminal is not None:
        return False
    if history.source_root_volume != history.destination_root_volume:
        return False
    if entry.is_directory or entry.state != 'MutationStarted':
        return False
    if entry.source_volume != history.source_root_volume:
        return False
    if entry.destination_volume is not None or entry.destination_ref is not None:
        return False
    if entry.undo != 'None' or entry.has_copy_fingerprint:
        return False
    if observed is not None and observed != (entry.source_volume, entry.source_ref):
        return False
    return True


def commit_move(history: History, observed: tuple[int, int]) -> History | None:
    if not can_settle_same_volume_move(history, observed):
        return None
    volume, reference = observed
    return replace(
        history,
        entry=replace(
            history.entry,
            state='Committed',
            destination_volume=volume,
            destination_ref=reference,
        ),
    )


def require_recovery(history: History, observed: tuple[int, int] | None) -> History | None:
    if not can_settle_same_volume_move(history, observed):
        return None
    destination_volume = None if observed is None else observed[0]
    destination_ref = None if observed is None else observed[1]
    return replace(
        history,
        entry=replace(
            history.entry,
            state='RecoveryRequired',
            destination_volume=destination_volume,
            destination_ref=destination_ref,
        ),
    )


def persisted_shape_valid(history: History) -> bool:
    entry = history.entry
    if history.kind == 'Move' and (entry.undo != 'None' or entry.has_copy_fingerprint):
        return False
    if entry.state == 'Committed' and history.kind == 'Move':
        return (
            not entry.is_directory
            and entry.destination_volume == entry.source_volume
            and entry.destination_ref == entry.source_ref
        )
    if entry.state == 'RecoveryRequired' and history.kind == 'Move':
        if entry.destination_volume is None and entry.destination_ref is None:
            return True
        return (
            entry.destination_volume == entry.source_volume
            and entry.destination_ref == entry.source_ref
        )
    return True


def check_properties(cases: int) -> int:
    base = History(
        kind='Move',
        source_root_volume=10,
        destination_root_volume=10,
        terminal=None,
        entry=Entry('MutationStarted', 10, 99),
    )
    assert can_settle_same_volume_move(base, (10, 99))
    committed = commit_move(base, (10, 99))
    assert committed is not None and persisted_shape_valid(committed)
    recovery_unknown = require_recovery(base, None)
    assert recovery_unknown is not None and persisted_shape_valid(recovery_unknown)
    recovery_observed = require_recovery(base, (10, 99))
    assert recovery_observed is not None and persisted_shape_valid(recovery_observed)
    assert commit_move(base, (10, 100)) is None
    assert commit_move(replace(base, destination_root_volume=11), (10, 99)) is None
    assert commit_move(replace(base, kind='Copy'), (10, 99)) is None
    assert commit_move(replace(base, entry=replace(base.entry, is_directory=True)), (10, 99)) is None
    assert not persisted_shape_valid(replace(committed, entry=replace(committed.entry, undo='DeleteCreatedDestination')))
    assert not persisted_shape_valid(replace(committed, entry=replace(committed.entry, has_copy_fingerprint=True)))

    rng = random.Random(20260814)
    checks = 10
    states = ['Pending', 'MutationStarted', 'Committed', 'Skipped', 'Failed', 'RecoveryRequired']
    for case in range(cases):
        root_source = rng.randrange(1, 64)
        root_destination = rng.randrange(1, 64)
        source_volume = rng.randrange(1, 64)
        source_ref = rng.randrange(1, 1_000_000)
        entry = Entry(
            state=rng.choice(states),
            source_volume=source_volume,
            source_ref=source_ref,
            destination_volume=None,
            destination_ref=None,
            is_directory=bool(rng.getrandbits(1)),
            undo=rng.choice(['None', 'DeleteCreatedDestination']),
            has_copy_fingerprint=bool(rng.getrandbits(1)),
        )
        history = History(
            kind=rng.choice(['Copy', 'Move']),
            source_root_volume=root_source,
            destination_root_volume=root_destination,
            terminal=rng.choice([None, 'Succeeded', 'Failed', 'Cancelled', 'RecoveryRequired']),
            entry=entry,
        )
        observed_mode = rng.randrange(3)
        if observed_mode == 0:
            observed = None
        elif observed_mode == 1:
            observed = (source_volume, source_ref)
        else:
            observed = (rng.randrange(1, 64), rng.randrange(1, 1_000_000))

        expected = (
            history.kind == 'Move'
            and history.terminal is None
            and root_source == root_destination
            and not entry.is_directory
            and entry.state == 'MutationStarted'
            and source_volume == root_source
            and entry.undo == 'None'
            and not entry.has_copy_fingerprint
            and (observed is None or observed == (source_volume, source_ref))
        )
        assert can_settle_same_volume_move(history, observed) == expected
        checks += 1

        if observed is not None:
            committed = commit_move(history, observed)
            assert (committed is not None) == expected
            if committed is not None:
                assert persisted_shape_valid(committed)
                assert committed.entry.destination_volume == source_volume
                assert committed.entry.destination_ref == source_ref
            checks += 3

        recovered = require_recovery(history, observed)
        assert (recovered is not None) == expected
        if recovered is not None:
            assert persisted_shape_valid(recovered)
        checks += 2

    return checks


def check_repository(root: Path) -> int:
    paths = {
        'history': root / 'src/FileOp.Core/Operations/FileOperationActionHistory.cs',
        'store': root / 'src/FileOp.Core/Operations/SqliteFileOperationActionHistoryStore.cs',
        'strategy': root / 'src/FileOp.Core/Operations/FileMoveExecutionStrategy.cs',
        'gate': root / 'tools/test-local.ps1',
    }
    missing = [str(path) for path in paths.values() if not path.is_file()]
    if missing:
        raise FileNotFoundError(', '.join(missing))

    source = {name: path.read_text(encoding='utf-8') for name, path in paths.items()}

    required_history = [
        'public interface IFileMoveOperationActionHistoryStore',
        'CommitSameVolumeMoveAsync(',
        'MarkSameVolumeMoveRecoveryRequiredAsync(',
        'Move history cannot reuse Copy undo, content-fingerprint, or destination hard-link evidence.',
    ]
    for needle in required_history:
        assert needle in source['history'], needle

    required_store = [
        'IFileMoveOperationActionHistoryStore,',
        'public async ValueTask<FileOperationActionHistory> CommitSameVolumeMoveAsync(',
        'public async ValueTask<FileOperationActionHistory> MarkSameVolumeMoveRecoveryRequiredAsync(',
        'ValidateSameVolumeMoveMutation(current, ordinal, destinationIdentity);',
        'ValidateSameVolumeMoveMutation(current, ordinal, observedDestinationIdentity);',
        'AND action.kind = @move_kind',
        'destinationIdentity != sourceIdentity',
        'Committed same-volume Move history must preserve the exact source object identity without Copy-only evidence.',
        'Recovery-sensitive same-volume Move destination identity must match the original source identity.',
        'private const int SchemaVersion = 1;',
    ]
    for needle in required_store:
        assert needle in source['store'], needle

    # Copy and Move settlement must remain distinct at the SQL boundary.
    assert 'AND action.kind = @copy_kind' in source['store']
    assert source['store'].count('AND action.kind = @move_kind') >= 2
    assert 'DeleteCreatedDestination' in source['store']
    assert 'PersistDestinationContentFingerprintAsync(' in source['store']
    assert 'Existing Copy support alone is not Move authorization.' in source['strategy']
    assert 'verify_file_move_action_history.py --repo-root $repoRoot --cases 50000' in source['gate']

    return len(required_history) + len(required_store) + 6


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('--repo-root', type=Path, default=Path.cwd())
    parser.add_argument('--self-test-only', action='store_true')
    parser.add_argument('--cases', type=int, default=50000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error('--cases must be greater than zero')

    print(f'PASS same-volume Move action-history model: {check_properties(args.cases):,} checks')
    if not args.self_test_only:
        print(f'PASS same-volume Move action-history source wiring: {check_repository(args.repo_root.resolve())} checks')
    return 0


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f'FAIL: {exc}', file=sys.stderr)
        raise SystemExit(1)
