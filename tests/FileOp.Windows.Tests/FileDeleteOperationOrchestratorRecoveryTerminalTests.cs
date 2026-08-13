using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileDeleteOperationOrchestratorRecoveryTerminalTests
{
    [TestMethod]
    public async Task CompletionReturningRecoveryRequiredIsSurfacedAsRecovery()
    {
        var authorization = CreateAuthorization();
        await using var historyStore = new RecoveryCompletionHistoryStore(
            CreateCommittedHistory(authorization),
            throwAfterPersist: false);

        var exception = await CaptureThrowsAsync<FileDeleteOperationOrchestrationRecoveryRequiredException>(async () =>
            await FileDeleteOperationOrchestrator.ExecuteAsync(
                authorization,
                new NeverStabilityProvider(),
                new NeverFinalProvider(),
                historyStore));

        Assert.AreEqual(1, historyStore.CompleteCount);
        Assert.AreEqual(FileDeleteOperationActionTerminalState.RecoveryRequired, exception.History.TerminalState);
        Assert.AreEqual(FileDeleteOperationActionEntryState.RecoveryRequired, exception.History.Entries[0].State);
        Assert.IsFalse(exception.DeleteMutationAuthorized);
    }

    [TestMethod]
    public async Task CompletionThrowThenObservedRecoveryRequiredIsSurfacedAsRecovery()
    {
        var authorization = CreateAuthorization();
        await using var historyStore = new RecoveryCompletionHistoryStore(
            CreateCommittedHistory(authorization),
            throwAfterPersist: true);

        var exception = await CaptureThrowsAsync<FileDeleteOperationOrchestrationRecoveryRequiredException>(async () =>
            await FileDeleteOperationOrchestrator.ExecuteAsync(
                authorization,
                new NeverStabilityProvider(),
                new NeverFinalProvider(),
                historyStore));

        Assert.AreEqual(1, historyStore.CompleteCount);
        Assert.AreEqual(FileDeleteOperationActionTerminalState.RecoveryRequired, exception.History.TerminalState);
        Assert.AreEqual(FileDeleteOperationActionEntryState.RecoveryRequired, exception.History.Entries[0].State);
        Assert.IsFalse(exception.DeleteMutationAuthorized);
    }

    private static FileDeleteOperationUserAuthorizationReceipt CreateAuthorization()
    {
        var root = @"C:\Users\Alice\Temp";
        var path = $@"{root}\recovery-terminal-{Guid.NewGuid():N}.tmp";
        var entry = new FileOperationEntry(path, Path.GetFileName(path), IsDirectory: false);
        var plan = new FileDeleteOperationPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new FileDeleteOperationIntent(
                "left",
                Guid.NewGuid(),
                root,
                new[] { entry }));
        var validation = new FileDeleteOperationExecutionValidationResult(
            plan,
            new FileOperationCanonicalPath(
                root,
                root,
                FileOperationCanonicalPathState.Directory,
                IsLeafReparsePoint: false,
                new FileIdentity(91, 9100)),
            new[]
            {
                new FileDeleteOperationExecutionValidationItem(
                    entry,
                    new FileOperationCanonicalPath(
                        path,
                        path,
                        FileOperationCanonicalPathState.File,
                        IsLeafReparsePoint: false,
                        new FileIdentity(91, 9101)),
                    FileDeleteOperationExecutionValidationDecision.ReadyForAuthorizationReview,
                    "ready"),
            },
            FileDeleteOperationExecutionValidationStatus.ReadyForAuthorizationReview,
            DateTimeOffset.UtcNow,
            "ready");
        return new FileDeleteOperationUserAuthorizationIssuer()
            .IssueAfterExplicitUserConfirmation(validation);
    }

    private static FileDeleteOperationActionHistory CreateCommittedHistory(
        FileDeleteOperationUserAuthorizationReceipt authorization)
    {
        var startedAt = authorization.AuthorizedAtUtc.AddMilliseconds(1);
        var mutationStartedAt = startedAt.AddMilliseconds(1);
        var completedAt = mutationStartedAt.AddMilliseconds(1);
        var item = authorization.Items[0];
        return new FileDeleteOperationActionHistory(
            authorization.PlanId,
            authorization.AuthorizationId,
            authorization.Plan.QueuedAtUtc,
            authorization.ValidatedAtUtc,
            authorization.AuthorizedAtUtc,
            startedAt,
            completedAtUtc: null,
            authorization.Plan.Intent.SourcePane,
            authorization.Plan.Intent.SourceTabId,
            authorization.CanonicalSourceDirectoryPath,
            authorization.SourceDirectoryIdentity,
            terminalState: null,
            new[]
            {
                new FileDeleteOperationActionEntry(
                    0,
                    item.Entry,
                    item.CanonicalPath,
                    item.Identity,
                    FileDeleteOperationActionEntryState.Committed,
                    mutationStartedAt,
                    completedAt,
                    Failure: null),
            });
    }

    private static FileDeleteOperationActionHistory ToRecoveryTerminal(
        FileDeleteOperationActionHistory history)
    {
        var previous = history.Entries[0];
        var completedAt = previous.CompletedAtUtc ?? history.StartedAtUtc.AddMilliseconds(2);
        return new FileDeleteOperationActionHistory(
            history.OperationId,
            history.AuthorizationId,
            history.QueuedAtUtc,
            history.ValidatedAtUtc,
            history.AuthorizedAtUtc,
            history.StartedAtUtc,
            completedAt.AddMilliseconds(1),
            history.SourcePaneId,
            history.SourceTabId,
            history.CanonicalSourceDirectoryPath,
            history.SourceDirectoryIdentity,
            FileDeleteOperationActionTerminalState.RecoveryRequired,
            new[]
            {
                new FileDeleteOperationActionEntry(
                    previous.Ordinal,
                    previous.Entry,
                    previous.CanonicalSourcePath,
                    previous.SourceIdentity,
                    FileDeleteOperationActionEntryState.RecoveryRequired,
                    previous.MutationStartedAtUtc,
                    completedAt,
                    new FileOperationFailure(
                        "SyntheticCompletionRecovery",
                        "Synthetic recovery-sensitive completion evidence.",
                        previous.CanonicalSourcePath,
                        Retryable: false)),
            });
    }

    private static async Task<TException> CaptureThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }

        Assert.Fail($"Expected {typeof(TException).Name} to be thrown.");
        throw new InvalidOperationException("Assert.Fail should have thrown.");
    }

    private sealed class RecoveryCompletionHistoryStore : IFileDeleteOperationActionHistoryStore
    {
        private readonly bool _throwAfterPersist;

        public RecoveryCompletionHistoryStore(
            FileDeleteOperationActionHistory history,
            bool throwAfterPersist)
        {
            Current = history;
            _throwAfterPersist = throwAfterPersist;
        }

        public FileDeleteOperationActionHistory Current { get; private set; }

        public int CompleteCount { get; private set; }

        public ValueTask<FileDeleteOperationActionHistory> BeginAsync(
            FileDeleteOperationUserAuthorizationReceipt authorization,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<FileDeleteOperationActionHistory> MarkMutationStartedAsync(
            Guid operationId,
            int ordinal,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<FileDeleteOperationActionHistory> CommitDeletedAsync(
            Guid operationId,
            int ordinal,
            FileIdentity deletedSourceIdentity,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<FileDeleteOperationActionHistory> MarkFailedAsync(
            Guid operationId,
            int ordinal,
            FileOperationFailure failure,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<FileDeleteOperationActionHistory> MarkMutationRecoveryRequiredAsync(
            Guid operationId,
            int ordinal,
            FileOperationFailure failure,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<FileDeleteOperationActionHistory> CompleteAsync(
            Guid operationId,
            CancellationToken cancellationToken = default)
        {
            if (operationId != Current.OperationId)
            {
                throw new InvalidOperationException("operation mismatch");
            }
            Assert.IsFalse(cancellationToken.CanBeCanceled);
            CompleteCount++;
            Current = ToRecoveryTerminal(Current);
            if (_throwAfterPersist)
            {
                throw new InvalidOperationException("synthetic completion failure after recovery persistence");
            }
            return new ValueTask<FileDeleteOperationActionHistory>(Current);
        }

        public ValueTask<FileDeleteOperationActionHistory?> GetAsync(
            Guid operationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (operationId != Current.OperationId)
            {
                throw new InvalidOperationException("operation mismatch");
            }
            return new ValueTask<FileDeleteOperationActionHistory?>(Current);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NeverStabilityProvider : IFileDeleteOperationStabilityLeaseProvider
    {
        public ValueTask<IFileDeleteOperationStabilityLease> AcquireAsync(
            FileDeleteOperationStabilityLeaseRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("terminal entry must not reacquire a stability lease");
    }

    private sealed class NeverFinalProvider : IFileDeleteOperationFinalMutationLeaseProvider
    {
        public ValueTask<IFileDeleteOperationFinalMutationLease> AcquireAsync(
            FileDeleteOperationFinalMutationLeaseRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("terminal entry must not reacquire a final mutation lease");
    }
}