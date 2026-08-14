#!/usr/bin/env python3
"""Zero-Actions model/source checks for explicit Files Copy collision resolution."""
from __future__ import annotations

import argparse
import random
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass, replace
from pathlib import Path


@dataclass(frozen=True)
class Plan:
    operation_id: int
    kind: str
    policy: str
    intent_id: int
    entries: int
    has_directory: bool
    preflight: str
    needs_decision_count: int
    blocked_count: int
    bound_to_current_state: bool


def can_resolve(plan: Plan) -> bool:
    return (
        plan.kind == 'Copy'
        and plan.policy == 'Ask'
        and plan.entries > 0
        and not plan.has_directory
        and plan.preflight == 'NeedsDecision'
        and plan.needs_decision_count > 0
        and plan.blocked_count == 0
        and plan.bound_to_current_state
    )


def resolve(plan: Plan, policy: str, new_id: int) -> Plan | None:
    if policy not in {'Skip', 'Stop'} or not can_resolve(plan):
        return None
    return replace(
        plan,
        operation_id=new_id,
        policy=policy,
        preflight='Missing',
        needs_decision_count=0,
        blocked_count=0,
    )


def check_properties(cases: int) -> int:
    base = Plan(1, 'Copy', 'Ask', 42, 2, False, 'NeedsDecision', 1, 0, True)
    resolved_skip = resolve(base, 'Skip', 2)
    resolved_stop = resolve(base, 'Stop', 3)
    assert resolved_skip is not None and resolved_skip.operation_id != base.operation_id
    assert resolved_stop is not None and resolved_stop.operation_id != base.operation_id
    assert resolved_skip.intent_id == base.intent_id == resolved_stop.intent_id
    assert resolved_skip.preflight == 'Missing' and resolved_stop.preflight == 'Missing'
    assert resolve(base, 'Replace', 4) is None
    assert resolve(replace(base, policy='Skip'), 'Stop', 5) is None
    assert resolve(replace(base, preflight='Ready'), 'Skip', 6) is None
    assert resolve(replace(base, bound_to_current_state=False), 'Skip', 7) is None

    rng = random.Random(20260814)
    checks = 8
    for case in range(cases):
        plan = Plan(
            operation_id=case + 100,
            kind=rng.choice(['Copy', 'Move']),
            policy=rng.choice(['Ask', 'Skip', 'Stop']),
            intent_id=rng.randrange(1, 1_000_000),
            entries=rng.randrange(0, 9),
            has_directory=bool(rng.getrandbits(1)),
            preflight=rng.choice(['Missing', 'Ready', 'NeedsDecision', 'Blocked']),
            needs_decision_count=rng.randrange(0, 4),
            blocked_count=rng.randrange(0, 3),
            bound_to_current_state=bool(rng.getrandbits(1)),
        )
        expected = (
            plan.kind == 'Copy'
            and plan.policy == 'Ask'
            and plan.entries > 0
            and not plan.has_directory
            and plan.preflight == 'NeedsDecision'
            and plan.needs_decision_count > 0
            and plan.blocked_count == 0
            and plan.bound_to_current_state
        )
        assert can_resolve(plan) == expected
        checks += 1

        for policy in ('Skip', 'Stop', 'Replace'):
            new_id = 10_000_000 + case * 3 + {'Skip': 0, 'Stop': 1, 'Replace': 2}[policy]
            result = resolve(plan, policy, new_id)
            should_resolve = expected and policy in {'Skip', 'Stop'}
            assert (result is not None) == should_resolve
            if result is not None:
                assert result.operation_id == new_id and result.operation_id != plan.operation_id
                assert result.intent_id == plan.intent_id
                assert result.policy == policy
                assert result.preflight == 'Missing'
            checks += 4

    return checks


def check_repository(root: Path) -> int:
    paths = {
        'xaml': root / 'src/FileOp.App/FilesView.xaml',
        'collision': root / 'src/FileOp.App/FilesView.CopyCollision.cs',
        'copy': root / 'src/FileOp.App/FilesView.Copy.cs',
        'primitive': root / 'src/FileOp.Windows/Operations/WindowsFileCopyMutationPrimitive.cs',
        'gate': root / 'tools/test-local.ps1',
    }
    missing = [str(path) for path in paths.values() if not path.is_file()]
    if missing:
        raise FileNotFoundError(', '.join(missing))

    source = {name: path.read_text(encoding='utf-8') for name, path in paths.items()}
    ET.fromstring(source['xaml'])

    required_xaml = [
        'x:Name="CopyCollisionDecisionPanel"',
        'Text="Resolve Ask-later Copy collisions:"',
        'x:Name="ResolveCopyCollisionSkipButton"',
        'Content="Skip existing"',
        'Loaded="ResolveCopyCollisionSkipButton_Loaded"',
        'x:Name="ResolveCopyCollisionStopButton"',
        'Content="Stop on collision"',
        'fresh preflight is required',
    ]
    for needle in required_xaml:
        assert needle in source['xaml'], needle

    required_collision = [
        'ResolveCopyCollisionSkipButton.Click += ResolveCopyCollisionSkipButton_Click;',
        'ResolveCopyCollisionStopButton.Click += ResolveCopyCollisionStopButton_Click;',
        'ResolveSelectedCopyCollision(FileOperationCollisionPolicy.Skip);',
        'ResolveSelectedCopyCollision(FileOperationCollisionPolicy.Stop);',
        'resolvedPolicy is not FileOperationCollisionPolicy.Skip and',
        'not FileOperationCollisionPolicy.Stop',
        'plan.CollisionPolicy != FileOperationCollisionPolicy.Ask',
        'preflight.Result.Status != FileOperationPreflightStatus.NeedsDecision',
        'preflight.Result.NeedsDecisionCount == 0',
        'preflight.Result.BlockedCount != 0',
        'IsPlanBoundToCurrentFilesState(plan)',
        'var resolved = new FileOperationPlan(',
        'Guid.NewGuid()',
        'DateTimeOffset.UtcNow',
        'original.Kind',
        'resolvedPolicy',
        'original.Intent',
        '_queuedOperations[index] = resolved;',
        '_preflightSnapshots.Remove(original.Id);',
        'run fresh preflight before execution can become available',
    ]
    for needle in required_collision:
        assert needle in source['collision'], needle

    assert 'Click="ResolveCopyCollisionSkipButton_Click"' not in source['xaml']
    assert 'Click="ResolveCopyCollisionStopButton_Click"' not in source['xaml']
    assert 'FileOperationCollisionPolicy.Replace' not in source['collision']
    assert 'Replace existing' not in source['xaml']
    assert 'Overwrite' not in source['xaml']
    assert 'new WindowsFileCopyMutationPrimitive()' in source['copy']
    assert 'FileCreate' in source['primitive']
    assert 'File.Copy(' not in source['collision']
    assert 'File.Move(' not in source['collision']
    assert 'verify_files_copy_collision_resolution.py --repo-root $repoRoot --cases 50000' in source['gate']

    return len(required_xaml) + len(required_collision) + 10


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('--repo-root', type=Path, default=Path.cwd())
    parser.add_argument('--self-test-only', action='store_true')
    parser.add_argument('--cases', type=int, default=50000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error('--cases must be greater than zero')

    print(f'PASS Files Copy collision-resolution model: {check_properties(args.cases):,} checks')
    if not args.self_test_only:
        print(f'PASS Files Copy collision-resolution source wiring: {check_repository(args.repo_root.resolve())} checks')
    return 0


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ET.ParseError, ValueError) as exc:
        print(f'FAIL: {exc}', file=sys.stderr)
        raise SystemExit(1)
