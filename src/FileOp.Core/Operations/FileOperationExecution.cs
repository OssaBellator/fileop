using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileOp.Core.Operations;

public enum FileOperationExecutionState
{
    Planned,
    Validating,
    Running,
    CancellationRequested,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record FileOperationFailure(
    string Code,
    string Message,
    string? Path,
    bool Retryable);

public sealed record FileOperationExecutionSnapshot
{
    private FileOperationExecutionSnapshot(
        FileOperationPlan plan,
        FileOperationExecutionState state,
        int completedEntryCount,
        string? currentPath,
        FileOperationFailure? failure)
    {
        Plan = plan;
        State = state;
        CompletedEntryCount = completedEntryCount;
        CurrentPath = currentPath;
        Failure = failure;
    }

    public FileOperationPlan Plan { get; private init; }

    public FileOperationExecutionState State { get; private init; }

    public int CompletedEntryCount { get; private init; }

    public int TotalEntryCount => Plan.Intent.Entries.Count;

    public string? CurrentPath { get; private init; }

    public FileOperationFailure? Failure { get; private init; }

    public bool IsTerminal =>
        State is FileOperationExecutionState.Succeeded or
            FileOperationExecutionState.Failed or
            FileOperationExecutionState.Cancelled;

    public static FileOperationExecutionSnapshot CreatePlanned(FileOperationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Intent.Entries.Count == 0)
        {
            throw new ArgumentException("A file operation plan must contain at least one entry.", nameof(plan));
        }

        return new FileOperationExecutionSnapshot(
            plan,
            FileOperationExecutionState.Planned,
            completedEntryCount: 0,
            currentPath: null,
            failure: null);
    }

    public FileOperationExecutionSnapshot BeginValidation()
    {
        EnsureState(FileOperationExecutionState.Planned);
        return this with
        {
            State = FileOperationExecutionState.Validating,
            CurrentPath = null,
            Failure = null,
        };
    }

    public FileOperationExecutionSnapshot BeginRunning()
    {
        EnsureState(FileOperationExecutionState.Validating);
        return this with
        {
            State = FileOperationExecutionState.Running,
            CurrentPath = null,
            Failure = null,
        };
    }

    public FileOperationExecutionSnapshot ReportProgress(int completedEntryCount, string? currentPath)
    {
        EnsureState(
            FileOperationExecutionState.Running,
            FileOperationExecutionState.CancellationRequested);

        if (completedEntryCount < CompletedEntryCount || completedEntryCount > TotalEntryCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedEntryCount),
                "Completed entry count must be monotonic and cannot exceed the operation total.");
        }

        return this with
        {
            CompletedEntryCount = completedEntryCount,
            CurrentPath = currentPath,
        };
    }

    public FileOperationExecutionSnapshot RequestCancellation()
    {
        return State switch
        {
            FileOperationExecutionState.Planned => this with
            {
                State = FileOperationExecutionState.Cancelled,
                CurrentPath = null,
            },
            FileOperationExecutionState.Validating or FileOperationExecutionState.Running => this with
            {
                State = FileOperationExecutionState.CancellationRequested,
            },
            FileOperationExecutionState.CancellationRequested => this,
            _ => throw new InvalidOperationException(
                $"Cancellation cannot be requested from terminal state {State}."),
        };
    }

    public FileOperationExecutionSnapshot CancelAtSafeBoundary()
    {
        EnsureState(FileOperationExecutionState.CancellationRequested);
        return this with
        {
            State = FileOperationExecutionState.Cancelled,
            CurrentPath = null,
        };
    }

    public FileOperationExecutionSnapshot Complete()
    {
        EnsureState(FileOperationExecutionState.Running);
        if (CompletedEntryCount != TotalEntryCount)
        {
            throw new InvalidOperationException(
                "An operation cannot succeed until every queued entry has completed.");
        }

        return this with
        {
            State = FileOperationExecutionState.Succeeded,
            CurrentPath = null,
        };
    }

    public FileOperationExecutionSnapshot Fail(FileOperationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        EnsureState(
            FileOperationExecutionState.Validating,
            FileOperationExecutionState.Running,
            FileOperationExecutionState.CancellationRequested);

        return this with
        {
            State = FileOperationExecutionState.Failed,
            CurrentPath = failure.Path,
            Failure = failure,
        };
    }

    private void EnsureState(params FileOperationExecutionState[] allowedStates)
    {
        foreach (var allowedState in allowedStates)
        {
            if (State == allowedState)
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"Operation state {State} does not allow this transition.");
    }
}

public interface IFileOperationExecutor
{
    ValueTask<FileOperationExecutionSnapshot> ExecuteAsync(
        FileOperationPlan plan,
        IProgress<FileOperationExecutionSnapshot>? progress = null,
        CancellationToken shutdownCancellationToken = default);

    ValueTask<bool> RequestCancellationAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);
}
