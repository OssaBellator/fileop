#!/usr/bin/env python3
"""Zero-Actions model/source checks for the file Move strategy boundary."""
from __future__ import annotations

import argparse
import random
import sys
from pathlib import Path


def classify(
    *,
    kind: str,
    validation_ready: bool,
    entry_count: int,
    item_count_matches: bool,
    has_directory: bool,
    roots_have_identity: bool,
    decisions_supported: bool,
    sources_bound_to_root_volume: bool,
    destinations_bound_to_root_volume: bool,
    ready_destinations_missing: bool,
    ready_count: int,
    same_volume: bool,
) -> str:
    if kind != 'Move':
        return 'Blocked'
    if not validation_ready:
        return 'Blocked'
    if entry_count <= 0 or not item_count_matches:
        return 'Blocked'
    if has_directory or not roots_have_identity:
        return 'Blocked'
    if not decisions_supported:
        return 'Blocked'
    if not sources_bound_to_root_volume or not destinations_bound_to_root_volume:
        return 'Blocked'
    if ready_count < 0 or ready_count > entry_count:
        return 'Blocked'
    if ready_count > 0 and not ready_destinations_missing:
        return 'Blocked'
    if ready_count == 0:
        return 'SkipOnly'
    return 'SameVolumeRenameRequired' if same_volume else 'CrossVolumeCopyDeleteRequired'


def check_properties(cases: int) -> int:
    base = dict(
        kind='Move',
        validation_ready=True,
        entry_count=2,
        item_count_matches=True,
        has_directory=False,
        roots_have_identity=True,
        decisions_supported=True,
        sources_bound_to_root_volume=True,
        destinations_bound_to_root_volume=True,
        ready_destinations_missing=True,
        ready_count=2,
        same_volume=True,
    )
    assert classify(**base) == 'SameVolumeRenameRequired'
    assert classify(**{**base, 'same_volume': False}) == 'CrossVolumeCopyDeleteRequired'
    assert classify(**{**base, 'ready_count': 0}) == 'SkipOnly'
    assert classify(**{**base, 'kind': 'Copy'}) == 'Blocked'
    assert classify(**{**base, 'has_directory': True}) == 'Blocked'
    assert classify(**{**base, 'roots_have_identity': False}) == 'Blocked'
    assert classify(**{**base, 'sources_bound_to_root_volume': False}) == 'Blocked'
    assert classify(**{**base, 'destinations_bound_to_root_volume': False}) == 'Blocked'
    assert classify(**{**base, 'ready_destinations_missing': False}) == 'Blocked'

    rng = random.Random(20260814)
    checks = 9
    for _ in range(cases):
        entry_count = rng.randrange(0, 9)
        ready_count = rng.randrange(-1, entry_count + 2)
        values = dict(
            kind=rng.choice(['Copy', 'Move']),
            validation_ready=bool(rng.getrandbits(1)),
            entry_count=entry_count,
            item_count_matches=bool(rng.getrandbits(1)),
            has_directory=bool(rng.getrandbits(1)),
            roots_have_identity=bool(rng.getrandbits(1)),
            decisions_supported=bool(rng.getrandbits(1)),
            sources_bound_to_root_volume=bool(rng.getrandbits(1)),
            destinations_bound_to_root_volume=bool(rng.getrandbits(1)),
            ready_destinations_missing=bool(rng.getrandbits(1)),
            ready_count=ready_count,
            same_volume=bool(rng.getrandbits(1)),
        )

        blocked = (
            values['kind'] != 'Move'
            or not values['validation_ready']
            or values['entry_count'] <= 0
            or not values['item_count_matches']
            or values['has_directory']
            or not values['roots_have_identity']
            or not values['decisions_supported']
            or not values['sources_bound_to_root_volume']
            or not values['destinations_bound_to_root_volume']
            or ready_count < 0
            or ready_count > entry_count
            or (ready_count > 0 and not values['ready_destinations_missing'])
        )
        if blocked:
            expected = 'Blocked'
        elif ready_count == 0:
            expected = 'SkipOnly'
        else:
            expected = (
                'SameVolumeRenameRequired'
                if values['same_volume']
                else 'CrossVolumeCopyDeleteRequired'
            )

        assert classify(**values) == expected
        checks += 1

        if expected == 'SameVolumeRenameRequired':
            assert values['same_volume'] and ready_count > 0
            checks += 2
        elif expected == 'CrossVolumeCopyDeleteRequired':
            assert not values['same_volume'] and ready_count > 0
            checks += 2
        elif expected == 'SkipOnly':
            assert ready_count == 0
            checks += 1

    return checks


def check_repository(root: Path) -> int:
    paths = {
        'strategy': root / 'src/FileOp.Core/Operations/FileMoveExecutionStrategy.cs',
        'history': root / 'src/FileOp.Core/Operations/FileOperationActionHistory.cs',
        'validator': root / 'src/FileOp.Windows/Operations/WindowsFileOperationExecutionValidator.cs',
        'docs': root / 'docs/file-move-execution-strategy.md',
        'gate': root / 'tools/test-local.ps1',
    }
    missing = [str(path) for path in paths.values() if not path.is_file()]
    if missing:
        raise FileNotFoundError(', '.join(missing))

    source = {name: path.read_text(encoding='utf-8') for name, path in paths.items()}

    required_strategy = [
        'public enum FileMoveExecutionStrategy',
        'Blocked,',
        'SkipOnly,',
        'SameVolumeRenameRequired,',
        'CrossVolumeCopyDeleteRequired,',
        'plan.Kind != FileOperationKind.Move',
        '!validation.CanBeginMutation',
        'plan.Intent.Entries.Any(static entry => entry.IsDirectory)',
        'TryGetDirectoryIdentity(validation.SourceDirectory',
        'TryGetDirectoryIdentity(validation.DestinationDirectory',
        'sourceIdentity.VolumeSerialNumber != sourceDirectoryIdentity.VolumeSerialNumber',
        'destinationIdentity.VolumeSerialNumber != destinationDirectoryIdentity.VolumeSerialNumber',
        'item.Destination.State != FileOperationCanonicalPathState.Missing',
        'sourceDirectoryIdentity.VolumeSerialNumber ==',
        'destinationDirectoryIdentity.VolumeSerialNumber',
        'Existing Copy support alone is not Move authorization.',
        'No filesystem mutation authority was created.',
    ]
    for needle in required_strategy:
        assert needle in source['strategy'], needle

    for forbidden in [
        'File.Move(',
        'MoveFile(',
        'MoveFileEx(',
        'SetFileInformationByHandle(',
        'NtSetInformationFile(',
        'File.Copy(',
        'File.Delete(',
    ]:
        assert forbidden not in source['strategy'], forbidden

    assert 'Action-history schema v1 supports mutation state only for Copy files' in source['history']
    assert 'FileOperationCollisionPolicy.Ask' in source['validator']
    assert 'FileOperationCollisionPolicy.Skip' in source['validator']
    assert 'FileOperationCollisionPolicy.Stop' in source['validator']
    assert 'same-volume' in source['docs']
    assert 'cross-volume' in source['docs']
    assert 'classification is not mutation authorization' in source['docs'].lower()
    assert 'verify_file_move_strategy.py --repo-root $repoRoot --cases 50000' in source['gate']

    return len(required_strategy) + 11


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('--repo-root', type=Path, default=Path.cwd())
    parser.add_argument('--self-test-only', action='store_true')
    parser.add_argument('--cases', type=int, default=50000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error('--cases must be greater than zero')

    print(f'PASS file Move strategy model: {check_properties(args.cases):,} checks')
    if not args.self_test_only:
        print(f'PASS file Move strategy source wiring: {check_repository(args.repo_root.resolve())} checks')
    return 0


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f'FAIL: {exc}', file=sys.stderr)
        raise SystemExit(1)
