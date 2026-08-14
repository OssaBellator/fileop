#!/usr/bin/env python3
"""Zero-Actions checks for FileOp's planned operation execution contract/state machine."""
from __future__ import annotations
import argparse, random, re, sys
from dataclasses import dataclass
from enum import Enum, auto
from pathlib import Path


class State(Enum):
    PLANNED = auto()
    VALIDATING = auto()
    RUNNING = auto()
    CANCELLATION_REQUESTED = auto()
    SUCCEEDED = auto()
    FAILED = auto()
    CANCELLED = auto()


@dataclass(frozen=True)
class Snapshot:
    state: State
    completed: int
    total: int
    failed: bool = False

    @staticmethod
    def planned(total: int) -> 'Snapshot':
        if total <= 0:
            raise ValueError('operation requires at least one entry')
        return Snapshot(State.PLANNED, 0, total)

    def begin_validation(self) -> 'Snapshot':
        if self.state is not State.PLANNED:
            raise ValueError('invalid validation transition')
        return Snapshot(State.VALIDATING, self.completed, self.total)

    def begin_running(self) -> 'Snapshot':
        if self.state is not State.VALIDATING:
            raise ValueError('invalid running transition')
        return Snapshot(State.RUNNING, self.completed, self.total)

    def progress(self, completed: int) -> 'Snapshot':
        if self.state not in {State.RUNNING, State.CANCELLATION_REQUESTED}:
            raise ValueError('progress outside running state')
        if completed < self.completed or completed > self.total:
            raise ValueError('non-monotonic or excessive progress')
        return Snapshot(self.state, completed, self.total, self.failed)

    def request_cancellation(self) -> 'Snapshot':
        if self.state in {State.PLANNED, State.VALIDATING}:
            return Snapshot(State.CANCELLED, self.completed, self.total)
        if self.state is State.RUNNING:
            return Snapshot(State.CANCELLATION_REQUESTED, self.completed, self.total)
        if self.state is State.CANCELLATION_REQUESTED:
            return self
        raise ValueError('terminal cancellation request')

    def cancel_at_safe_boundary(self) -> 'Snapshot':
        if self.state is not State.CANCELLATION_REQUESTED:
            raise ValueError('not waiting for a cancellation boundary')
        final = State.SUCCEEDED if self.completed == self.total else State.CANCELLED
        return Snapshot(final, self.completed, self.total)

    def complete(self) -> 'Snapshot':
        if self.state is not State.RUNNING or self.completed != self.total:
            raise ValueError('cannot complete early')
        return Snapshot(State.SUCCEEDED, self.completed, self.total)

    def fail(self) -> 'Snapshot':
        if self.state not in {State.VALIDATING, State.RUNNING, State.CANCELLATION_REQUESTED}:
            raise ValueError('invalid failure transition')
        return Snapshot(State.FAILED, self.completed, self.total, True)


def expect_invalid(action) -> None:
    try:
        action()
    except ValueError:
        return
    raise AssertionError('expected invalid state transition')


def check_properties(cases: int) -> int:
    assert Snapshot.planned(1).request_cancellation().state is State.CANCELLED
    assert Snapshot.planned(1).begin_validation().request_cancellation().state is State.CANCELLED
    assert (
        Snapshot.planned(1)
        .begin_validation()
        .begin_running()
        .progress(1)
        .request_cancellation()
        .cancel_at_safe_boundary()
        .state is State.SUCCEEDED
    )
    assert (
        Snapshot.planned(2)
        .begin_validation()
        .begin_running()
        .progress(1)
        .request_cancellation()
        .cancel_at_safe_boundary()
        .state is State.CANCELLED
    )
    expect_invalid(lambda: Snapshot.planned(2).begin_validation().begin_running().progress(1).complete())
    checks = 5

    rng = random.Random(20260808)
    for _ in range(cases):
        total = rng.randint(1, 32)
        snap = Snapshot.planned(total)

        if rng.random() < 0.05:
            snap = snap.request_cancellation()
            assert snap.state is State.CANCELLED
            checks += 1
            continue

        snap = snap.begin_validation()
        checks += 1
        if rng.random() < 0.08:
            snap = snap.fail()
            assert snap.state is State.FAILED
            checks += 1
            continue
        if rng.random() < 0.08:
            snap = snap.request_cancellation()
            assert snap.state is State.CANCELLED
            checks += 1
            continue

        snap = snap.begin_running()
        checks += 1
        completed = 0
        while completed < total:
            completed = min(total, completed + rng.randint(1, min(4, total - completed)))
            snap = snap.progress(completed)
            checks += 1

            if rng.random() < 0.02:
                snap = snap.fail()
                assert snap.state is State.FAILED
                checks += 1
                break

            if rng.random() < 0.04:
                snap = snap.request_cancellation()
                checks += 1
                if completed < total and rng.random() < 0.5:
                    completed += 1
                    snap = snap.progress(completed)
                    checks += 1
                snap = snap.cancel_at_safe_boundary()
                assert snap.state is (State.SUCCEEDED if completed == total else State.CANCELLED)
                checks += 1
                break

        if snap.state is State.RUNNING:
            snap = snap.complete()
            assert snap.state is State.SUCCEEDED
            checks += 1

        if snap.state in {State.SUCCEEDED, State.FAILED, State.CANCELLED}:
            expect_invalid(snap.begin_validation)
            expect_invalid(snap.begin_running)
            expect_invalid(lambda: snap.progress(snap.completed))
            checks += 3

    return checks


def check_repository(root: Path) -> int:
    paths = {
        'plan': root / 'src/FileOp.Core/Operations/FileOperationPlan.cs',
        'execution': root / 'src/FileOp.Core/Operations/FileOperationExecution.cs',
        'view': root / 'src/FileOp.App/FilesView.xaml.cs',
        'docs': root / 'docs/files-browser.md',
        'local': root / 'tools/test-local.ps1',
    }
    missing = [str(path) for path in paths.values() if not path.is_file()]
    if missing:
        raise FileNotFoundError(', '.join(missing))

    source = {name: path.read_text(encoding='utf-8') for name, path in paths.items()}
    plan = source['plan']
    execution = source['execution']
    view = source['view']

    required_plan = [
        'namespace FileOp.Core.Operations;',
        'public enum FileOperationKind',
        'public enum FileOperationCollisionPolicy',
        'public sealed record FileOperationIntent',
        'public FileOperationIntent(',
        'IEnumerable<FileOperationEntry>? entries',
        'var entrySnapshot = entries.ToArray();',
        'Entries = Array.AsReadOnly(entrySnapshot);',
        'public sealed record FileOperationPlan(',
    ]
    for needle in required_plan:
        assert needle in plan, needle

    for state in [
        'Planned', 'Validating', 'Running', 'CancellationRequested',
        'Succeeded', 'Failed', 'Cancelled',
    ]:
        assert state in execution, state

    required_execution = [
        'public static FileOperationExecutionSnapshot CreatePlanned(FileOperationPlan plan)',
        'ArgumentNullException.ThrowIfNull(plan.Intent);',
        'ArgumentNullException.ThrowIfNull(plan.Intent.Entries);',
        'plan.Intent.Entries.Count == 0',
        'public FileOperationExecutionSnapshot BeginValidation()',
        'public FileOperationExecutionSnapshot BeginRunning()',
        'public FileOperationExecutionSnapshot ReportProgress(int completedEntryCount, string? currentPath)',
        'completedEntryCount < CompletedEntryCount || completedEntryCount > TotalEntryCount',
        'public FileOperationExecutionSnapshot RequestCancellation()',
        'FileOperationExecutionState.Planned or FileOperationExecutionState.Validating => this with',
        'FileOperationExecutionState.Running => this with',
        'State = FileOperationExecutionState.CancellationRequested',
        'public FileOperationExecutionSnapshot CancelAtSafeBoundary()',
        'CompletedEntryCount == TotalEntryCount',
        '? FileOperationExecutionState.Succeeded',
        ': FileOperationExecutionState.Cancelled',
        'public FileOperationExecutionSnapshot Complete()',
        'CompletedEntryCount != TotalEntryCount',
        'public FileOperationExecutionSnapshot Fail(FileOperationFailure failure)',
        'public interface IFileOperationExecutor',
        'IProgress<FileOperationExecutionSnapshot>? progress = null);',
        'ValueTask<bool> RequestCancellationAsync(',
    ]
    for needle in required_execution:
        assert needle in execution, needle

    execute_signature = re.search(
        r'ValueTask<FileOperationExecutionSnapshot> ExecuteAsync\(.*?\);',
        execution,
        re.S,
    )
    assert execute_signature, 'ExecuteAsync contract not found'
    assert 'CancellationToken' not in execute_signature.group(0), (
        'ExecuteAsync must not expose an arbitrary cancellation token; use RequestCancellationAsync'
    )
    assert 'shutdownCancellationToken' not in execution
    assert 'Retry(' not in execution
    assert 'RetryAsync(' not in execution

    for needle in [
        'using FileOp.Core.Operations;',
        'private readonly List<FileOperationPlan> _queuedOperations = [];',
        'private FileOperationIntent? _preparedIntent;',
        'new FileOperationEntry(',
        'new FileOperationIntent(',
        'QueuePreparedIntent(FileOperationKind.Copy);',
        'QueuePreparedIntent(FileOperationKind.Move);',
        'new FileOperationPlan(',
    ]:
        assert needle in view, needle

    for obsolete in [
        'public enum FileBrowserOperationKind',
        'public enum FileBrowserCollisionPolicy',
        'public sealed record FileBrowserOperationEntry(',
        'public sealed record FileBrowserOperationIntent(',
        'public sealed record FileBrowserQueuedOperation(',
    ]:
        assert obsolete not in view, obsolete

    combined = plan + execution + view
    for forbidden in [
        'File.Copy(', 'File.Move(', 'File.Delete(',
        'Directory.Move(', 'Directory.Delete(',
    ]:
        assert forbidden not in combined, forbidden

    assert '## Copy / Move planning boundary' in source['docs']
    assert 'Preparing or queueing a plan performs no filesystem write.' in source['docs']
    assert 'read-only Windows preflight' in source['docs']
    assert 'It passes the same immutable plan to the reviewed executor' in source['docs']
    assert '- regular-file Move executes only as a same-volume local rename; directory Move and cross-volume Move mutation are rejected;' in source['docs']
    assert 'verify_file_operation_state.py' in source['local']

    return len(required_plan) + len(required_execution) + 8 + 5 + 12


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('--repo-root', type=Path, default=Path.cwd())
    parser.add_argument('--self-test-only', action='store_true')
    parser.add_argument('--cases', type=int, default=20000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error('--cases must be greater than zero')

    print(f'PASS file operation state-machine properties: {check_properties(args.cases)} checks')
    if not args.self_test_only:
        print(f'PASS file operation state-machine source wiring: {check_repository(args.repo_root.resolve())} checks')
    return 0


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f'FAIL: {exc}', file=sys.stderr)
        raise SystemExit(1)
