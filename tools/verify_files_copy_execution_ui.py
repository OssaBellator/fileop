#!/usr/bin/env python3
"""Zero-Actions model/source checks for the Files regular-file Copy execution UI."""
from __future__ import annotations

import argparse
import ntpath
import random
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def norm(path: str) -> str:
    value = ntpath.normpath(path.replace('/', '\\'))
    if len(value) == 2 and value[1] == ':':
        value += '\\'
    return value.casefold()


def bound_to_current_state(plan: dict, panes: dict[str, dict]) -> bool:
    source = panes.get(plan['source_pane'])
    destination = panes.get(plan['destination_pane'])
    if source is None or destination is None or source is destination:
        return False
    return (
        source['ready']
        and destination['ready']
        and source['tab'] == plan['source_tab']
        and destination['tab'] == plan['destination_tab']
        and norm(source['path']) == norm(plan['source_path'])
        and norm(destination['path']) == norm(plan['destination_path'])
    )


def can_run(plan: dict, panes: dict[str, dict]) -> bool:
    return (
        plan['kind'] == 'Copy'
        and plan['entries'] > 0
        and not plan['has_directory']
        and plan['preflight'] == 'Ready'
        and bound_to_current_state(plan, panes)
    )


def reset_for_source_change(queue: list[str], active: str | None) -> list[str]:
    if active is None:
        return []
    return [operation_id for operation_id in queue if operation_id == active]


def cancellation_terminal(completed: int, total: int) -> str:
    if completed < 0 or total < 0 or completed > total:
        raise ValueError('invalid progress')
    return 'Succeeded' if completed == total else 'Cancelled'


def classify_history(terminal: str | None, entry_states: list[str]) -> str:
    if terminal == 'RecoveryRequired' or any(
        state in {'MutationStarted', 'RecoveryRequired'} for state in entry_states
    ):
        return 'RecoveryRequired'
    if terminal is None:
        return 'NonTerminal'
    return terminal


def check_properties(cases: int) -> int:
    base_plan = {
        'kind': 'Copy',
        'entries': 2,
        'has_directory': False,
        'preflight': 'Ready',
        'source_pane': 'Left',
        'destination_pane': 'Right',
        'source_tab': 'left-tab',
        'destination_tab': 'right-tab',
        'source_path': r'C:\Source',
        'destination_path': r'C:\Destination',
    }
    base_panes = {
        'Left': {'ready': True, 'tab': 'left-tab', 'path': r'c:\source'},
        'Right': {'ready': True, 'tab': 'right-tab', 'path': r'C:\Destination'},
    }

    assert can_run(base_plan, base_panes)
    assert not can_run({**base_plan, 'kind': 'Move'}, base_panes)
    assert not can_run({**base_plan, 'has_directory': True}, base_panes)
    assert not can_run({**base_plan, 'preflight': 'NeedsDecision'}, base_panes)
    assert not can_run({**base_plan, 'preflight': 'Blocked'}, base_panes)
    assert not can_run(base_plan, {**base_panes, 'Right': {**base_panes['Right'], 'tab': 'other'}})
    assert reset_for_source_change(['a', 'b'], None) == []
    assert reset_for_source_change(['a', 'b'], 'b') == ['b']
    assert cancellation_terminal(0, 2) == 'Cancelled'
    assert cancellation_terminal(2, 2) == 'Succeeded'
    assert classify_history('Failed', ['Committed', 'Failed']) == 'Failed'
    assert classify_history('Failed', ['Committed', 'RecoveryRequired']) == 'RecoveryRequired'
    assert classify_history(None, ['Committed', 'Pending']) == 'NonTerminal'
    assert classify_history('Succeeded', ['Committed', 'Skipped']) == 'Succeeded'

    rng = random.Random(20260814)
    checks = 14
    for case in range(cases):
        source_path = rf'C:\Root\Source{case % 31}'
        destination_path = rf'C:\Root\Destination{case % 37}'
        source_tab = f's-{case}'
        destination_tab = f'd-{case}'
        plan = {
            'kind': rng.choice(['Copy', 'Move']),
            'entries': rng.randint(0, 8),
            'has_directory': bool(rng.getrandbits(1)),
            'preflight': rng.choice(['Ready', 'NeedsDecision', 'Blocked', 'Missing']),
            'source_pane': 'Left',
            'destination_pane': 'Right',
            'source_tab': source_tab,
            'destination_tab': destination_tab,
            'source_path': source_path,
            'destination_path': destination_path,
        }
        panes = {
            'Left': {
                'ready': bool(rng.getrandbits(1)),
                'tab': source_tab if rng.random() < 0.8 else f'other-s-{case}',
                'path': source_path if rng.random() < 0.8 else rf'C:\Changed\S{case}',
            },
            'Right': {
                'ready': bool(rng.getrandbits(1)),
                'tab': destination_tab if rng.random() < 0.8 else f'other-d-{case}',
                'path': destination_path if rng.random() < 0.8 else rf'C:\Changed\D{case}',
            },
        }

        expected = (
            plan['kind'] == 'Copy'
            and plan['entries'] > 0
            and not plan['has_directory']
            and plan['preflight'] == 'Ready'
            and panes['Left']['ready']
            and panes['Right']['ready']
            and panes['Left']['tab'] == source_tab
            and panes['Right']['tab'] == destination_tab
            and norm(panes['Left']['path']) == norm(source_path)
            and norm(panes['Right']['path']) == norm(destination_path)
        )
        assert can_run(plan, panes) == expected
        checks += 1

        queue = [f'op-{case}-0', f'op-{case}-1', f'op-{case}-2']
        active = rng.choice([None, *queue])
        reset = reset_for_source_change(queue, active)
        assert reset == ([] if active is None else [active])
        assert active is not None or not reset
        checks += 2

        total = rng.randint(0, 12)
        completed = rng.randint(0, total)
        terminal = cancellation_terminal(completed, total)
        assert terminal == ('Succeeded' if completed == total else 'Cancelled')
        assert terminal != 'Cancelled' or completed < total
        checks += 2

        entry_states = [
            rng.choice([
                'Pending', 'MutationStarted', 'Committed', 'Skipped',
                'Failed', 'RecoveryRequired',
            ])
            for _ in range(rng.randint(0, 8))
        ]
        durable_terminal = rng.choice([
            None, 'Succeeded', 'Failed', 'Cancelled', 'RecoveryRequired',
        ])
        classification = classify_history(durable_terminal, entry_states)
        recovery_expected = (
            durable_terminal == 'RecoveryRequired'
            or any(state in {'MutationStarted', 'RecoveryRequired'} for state in entry_states)
        )
        assert (classification == 'RecoveryRequired') == recovery_expected
        assert recovery_expected or classification == (durable_terminal or 'NonTerminal')
        checks += 2

    return checks


def check_repository(root: Path) -> int:
    paths = {
        'viewx': root / 'src/FileOp.App/FilesView.xaml',
        'viewc': root / 'src/FileOp.App/FilesView.Copy.cs',
        'pane_refresh': root / 'src/FileOp.App/FilesPaneView.CopyRefresh.cs',
        'source_identity': root / 'src/FileOp.App/MainWindow.StorageSourceIdentity.cs',
        'history': root / 'src/FileOp.Core/Operations/FileOperationActionHistory.cs',
        'executor': root / 'src/FileOp.Core/Operations/FileCopyOperationExecutor.cs',
        'mutation': root / 'src/FileOp.Windows/Operations/WindowsFileCopyMutationPrimitive.cs',
        'docs': root / 'docs/files-browser.md',
        'gate': root / 'tools/test-local.ps1',
    }
    missing = [str(path) for path in paths.values() if not path.is_file()]
    if missing:
        raise FileNotFoundError(', '.join(missing))

    source = {name: path.read_text(encoding='utf-8') for name, path in paths.items()}
    ET.fromstring(source['viewx'])

    required_view = [
        'x:Name="RunQueuedCopyButton"',
        'Content="Run selected Copy"',
        'Loaded="RunQueuedCopyButton_Loaded"',
        'x:Name="CancelQueuedCopyButton"',
        'Content="Cancel Copy"',
        'x:Name="CopyProgressBar"',
        'private async Task RunSelectedCopyAsync()',
        'plan.Kind != FileOperationKind.Copy',
        'entry => entry.IsDirectory',
        'FileOperationPreflightStatus.Ready',
        'IsPlanBoundToCurrentFilesState(plan)',
        'new SqliteFileOperationActionHistoryStore(',
        'new WindowsFileOperationExecutionValidator()',
        'new WindowsFileCopyMutationPrimitive()',
        'new FileCopyOperationExecutor(',
        'finalSnapshot = await executor.ExecuteAsync(plan, progress);',
        'await executor.RequestCancellationAsync(operationId)',
        'finalHistory = await historyStore.GetAsync(plan.Id);',
        'history?.RequiresRecovery == true',
        'FileOperationActionTerminalState.Succeeded',
        'FileOperationActionTerminalState.Cancelled',
        'FileOperationActionTerminalState.Failed',
        '_queuedOperations.RemoveAll(operation => operation.Id == plan.Id);',
        '_preflightSnapshots.Remove(plan.Id);',
        'RequestRefreshForCopyDestination(plan.Intent.DestinationDirectoryPath);',
        'original operation ID will not be replayed automatically',
        'FileOperationHistoryDatabaseName = "file-operation-actions.sqlite"',
    ]
    combined_view = source['viewx'] + source['viewc']
    for needle in required_view:
        assert needle in combined_view, needle

    required_busy_ownership = [
        'SetCopyExecutionUiBusy(true);',
        'PrepareLeftToRightButton.IsEnabled = false;',
        'PrepareRightToLeftButton.IsEnabled = false;',
        'CollisionPolicyBox.IsEnabled = false;',
        'QueueCopyButton.IsEnabled = false;',
        'QueueMoveButton.IsEnabled = false;',
        'OperationQueueList.IsEnabled = false;',
        'CancelQueuedCopyButton.Visibility = Visibility.Visible;',
        'if (_copyExecutionRunning)',
        '// reviewed executor is active.',
    ]
    for needle in required_busy_ownership:
        assert needle in source['viewc'], needle

    required_progress = [
        'BeginCopyProgressPresentation(plan.Intent.Entries.Count);',
        'UpdateCopyProgressPresentation(snapshot);',
        'CopyProgressBar.Maximum = Math.Max(1, snapshot.TotalEntryCount);',
        'snapshot.CompletedEntryCount',
        'CopyProgressBar.IsIndeterminate',
    ]
    for needle in required_progress:
        assert needle in source['viewc'], needle

    assert 'public void RequestRefresh() => RefreshRequested?.Invoke(this, EventArgs.Empty);' in source['pane_refresh']

    required_source_lifetime = [
        'QueueFilesOperationPlanningResetForSourceChange();',
        '_filesView.ResetOperationPlanningForSourceChange();',
        'public void ResetOperationPlanningForSourceChange()',
        '_queuedOperations.Clear();',
        '_preflightSnapshots.Clear();',
    ]
    lifetime = source['source_identity'] + source['viewc']
    for needle in required_source_lifetime:
        assert needle in lifetime, needle

    history = source['history']
    assert 'public bool RequiresRecovery =>' in history
    assert 'FileOperationActionTerminalState.RecoveryRequired' in history
    assert 'ValueTask<FileOperationActionHistory?> GetAsync(' in history

    executor_order = source['executor']
    begin = executor_order.index('await _historyStore.BeginAsync(validation, UtcNow())')
    mutation_started = executor_order.index('.MarkMutationStartedAsync(plan.Id, ordinal, UtcNow())')
    mutation = executor_order.index('.CopyNewFileAsync(new FileCopyMutationRequest(')
    commit = executor_order.index('.CommitCopyAsync(')
    assert begin < mutation_started < mutation < commit
    assert 'Directory Copy is not supported by this executor boundary.' in executor_order
    assert 'FileOperationActionTerminalState.RecoveryRequired' in executor_order
    assert 'RequestCancellationAsync(' in executor_order
    assert 'SettleRunningCancellationAsync(' in executor_order
    assert 'Once MutationStarted is durable, cancellation is intentionally not' in executor_order
    assert 'CopyNewFileAsync(new FileCopyMutationRequest(' in executor_order

    primitive = source['mutation']
    assert 'FileCreate' in primitive
    assert 'CopyNewFileAsync(' in primitive
    for forbidden in ['File.Copy(', 'File.Move(', 'File.Delete(', 'Directory.Delete(']:
        assert forbidden not in source['viewc'], forbidden

    assert 'The Files **Copy/Move queue UI still does not instantiate or execute that pipeline**' not in source['docs']
    assert 'regular-file Copy' in source['docs']
    assert 'Move' in source['docs']
    assert 'verify_files_copy_execution_ui.py --repo-root $repoRoot --cases 50000' in source['gate']

    return (
        len(required_view) + len(required_busy_ownership) + len(required_progress) +
        len(required_source_lifetime) + 23
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('--repo-root', type=Path, default=Path.cwd())
    parser.add_argument('--self-test-only', action='store_true')
    parser.add_argument('--cases', type=int, default=50000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error('--cases must be greater than zero')

    print(f'PASS Files Copy execution model: {check_properties(args.cases):,} checks')
    if not args.self_test_only:
        print(f'PASS Files Copy execution source wiring: {check_repository(args.repo_root.resolve())} checks')
    return 0


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ET.ParseError, ValueError) as exc:
        print(f'FAIL: {exc}', file=sys.stderr)
        raise SystemExit(1)
