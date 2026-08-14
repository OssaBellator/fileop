#!/usr/bin/env python3
"""Zero-Actions model/source checks for fail-closed release-completion boundaries."""
from __future__ import annotations

import argparse
import random
import sys
from pathlib import Path


UNSUPPORTED_DIRECTORY_FEATURE_MASK = (1 << 9) - 1


def classify_directory(
    *,
    canonical_root_present: bool,
    enumeration_complete: bool,
    metadata_complete: bool,
    observed_features: int,
) -> str:
    if not canonical_root_present or not enumeration_complete or not metadata_complete:
        return 'EvidenceIncomplete'
    if observed_features & UNSUPPORTED_DIRECTORY_FEATURE_MASK:
        return 'Unsupported'
    return 'EligibleForFuturePlainTreeExecutor'


def can_use_permanent_delete_authorization(disposition: str) -> bool:
    if disposition == 'PermanentReviewed':
        return True
    if disposition == 'RecycleBin':
        return False
    raise ValueError(disposition)


def can_publish_rebuild(
    *,
    state: str,
    live_readable: bool,
    checkpoint_valid: bool,
    exclusive_lease: bool,
    distinct_paths: bool,
) -> bool:
    return (
        state == 'ShadowCheckpointVerified'
        and live_readable
        and checkpoint_valid
        and exclusive_lease
        and distinct_paths
    )


def check_properties(cases: int) -> int:
    assert classify_directory(
        canonical_root_present=True,
        enumeration_complete=True,
        metadata_complete=True,
        observed_features=0,
    ) == 'EligibleForFuturePlainTreeExecutor'
    assert classify_directory(
        canonical_root_present=True,
        enumeration_complete=False,
        metadata_complete=True,
        observed_features=0,
    ) == 'EvidenceIncomplete'
    assert classify_directory(
        canonical_root_present=True,
        enumeration_complete=True,
        metadata_complete=True,
        observed_features=1,
    ) == 'Unsupported'
    assert can_use_permanent_delete_authorization('PermanentReviewed')
    assert not can_use_permanent_delete_authorization('RecycleBin')
    assert can_publish_rebuild(
        state='ShadowCheckpointVerified',
        live_readable=True,
        checkpoint_valid=True,
        exclusive_lease=True,
        distinct_paths=True,
    )

    rng = random.Random(20260814)
    checks = 6
    states = ['BuildingShadow', 'ShadowCheckpointVerified', 'Published', 'Abandoned']
    for _ in range(cases):
        root = bool(rng.getrandbits(1))
        enumeration = bool(rng.getrandbits(1))
        metadata = bool(rng.getrandbits(1))
        features = rng.randrange(0, 1 << 11)
        actual_directory = classify_directory(
            canonical_root_present=root,
            enumeration_complete=enumeration,
            metadata_complete=metadata,
            observed_features=features,
        )
        if not root or not enumeration or not metadata:
            expected_directory = 'EvidenceIncomplete'
        elif features & UNSUPPORTED_DIRECTORY_FEATURE_MASK:
            expected_directory = 'Unsupported'
        else:
            expected_directory = 'EligibleForFuturePlainTreeExecutor'
        assert actual_directory == expected_directory
        checks += 1

        state = rng.choice(states)
        live = bool(rng.getrandbits(1))
        checkpoint = bool(rng.getrandbits(1))
        lease = bool(rng.getrandbits(1))
        distinct = bool(rng.getrandbits(1))
        expected_publish = (
            state == 'ShadowCheckpointVerified'
            and live and checkpoint and lease and distinct
        )
        assert can_publish_rebuild(
            state=state,
            live_readable=live,
            checkpoint_valid=checkpoint,
            exclusive_lease=lease,
            distinct_paths=distinct,
        ) == expected_publish
        checks += 1

        disposition = rng.choice(['PermanentReviewed', 'RecycleBin'])
        assert can_use_permanent_delete_authorization(disposition) == (
            disposition == 'PermanentReviewed'
        )
        checks += 1

    return checks


def check_repository(root: Path) -> int:
    paths = {
        'directory': root / 'src/FileOp.Core/Operations/DirectoryOperationFidelity.cs',
        'delete': root / 'src/FileOp.Core/Operations/FileDeleteDispositionPolicy.cs',
        'rebuild': root / 'src/FileOp.Core/Indexing/IndexRebuildPublicationPolicy.cs',
        'namespace': root / 'src/FileOp.Windows/Operations/WindowsFileOperationNamespaceCapability.cs',
        'directory_docs': root / 'docs/directory-operations.md',
        'delete_docs': root / 'docs/delete-disposition-policy.md',
        'privilege_docs': root / 'docs/privilege-policy.md',
        'gate': root / 'tools/test-local.ps1',
    }
    missing = [str(path) for path in paths.values() if not path.is_file()]
    if missing:
        raise FileNotFoundError(', '.join(missing))
    source = {name: path.read_text(encoding='utf-8') for name, path in paths.items()}

    directory_needles = [
        'ReparsePoint = 1 << 0',
        'AlternateDataStreams = 1 << 1',
        'NonDefaultSecurityDescriptor = 1 << 2',
        'ExtendedAttributes = 1 << 3',
        'SparseData = 1 << 4',
        'CompressedData = 1 << 5',
        'EncryptedData = 1 << 6',
        'CaseSensitiveNamespace = 1 << 7',
        'HardLinks = 1 << 8',
        '!evidence.EnumerationComplete || !evidence.MetadataInspectionComplete',
        'classification still grants no mutation authority',
    ]
    for needle in directory_needles:
        assert needle in source['directory'], needle

    delete_needles = [
        'PermanentReviewed',
        'RecycleBin',
        'RequiresSeparateIdentitySafeRecycleContract',
        'CanUseExistingPermanentDeleteAuthorization',
        'A path-only shell delete is not equivalent',
    ]
    for needle in delete_needles:
        assert needle in source['delete'], needle

    rebuild_needles = [
        'State == IndexRebuildSnapshotState.ShadowCheckpointVerified',
        'LiveSnapshotReadable',
        'ShadowHasValidCheckpoint',
        'ExclusivePublicationLeaseHeld',
        '!string.Equals(LiveDatabasePath, ShadowDatabasePath, StringComparison.OrdinalIgnoreCase)',
        'if (!state.CanPublish)',
        'A published rebuild cannot be abandoned',
    ]
    for needle in rebuild_needles:
        assert needle in source['rebuild'], needle

    namespace_needles = [
        'FileCaseSensitiveInformation = 71',
        'FileCsFlagCaseSensitiveDir = 0x00000001',
        'NtQueryInformationFile(',
        'UnsupportedCaseSensitiveDirectory',
        'CanUseCurrentMutationModel',
        'throw new NotSupportedException(capability.Summary)',
    ]
    for needle in namespace_needles:
        assert needle in source['namespace'], needle

    assert 'no directory mutation primitive exists yet' in source['directory'].lower()
    assert 'not part of this release' in source['privilege_docs'].lower()
    assert 'same-account split-token elevation only' in source['privilege_docs']
    assert 'Recycle Bin' in source['delete_docs']
    assert 'verify_release_completion_boundaries.py --repo-root $repoRoot --cases 50000' in source['gate']
    return len(directory_needles) + len(delete_needles) + len(rebuild_needles) + len(namespace_needles) + 5


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('--repo-root', type=Path, default=Path.cwd())
    parser.add_argument('--self-test-only', action='store_true')
    parser.add_argument('--cases', type=int, default=50000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error('--cases must be greater than zero')

    print(f'PASS release completion boundary model: {check_properties(args.cases):,} checks')
    if not args.self_test_only:
        print(
            'PASS release completion boundary source wiring: '
            f'{check_repository(args.repo_root.resolve())} checks'
        )
    return 0


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f'FAIL: {exc}', file=sys.stderr)
        raise SystemExit(1)
