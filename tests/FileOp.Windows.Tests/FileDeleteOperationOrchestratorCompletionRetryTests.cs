using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileDeleteOperationOrchestratorCompletionRetryTests
{
    [TestMethod]
    public async Task CompletionThrowBeforePersistenceRetriesCompletionWithoutMutationReplay()
    {
        var authorization = CreateAuthorization(2);
        var history = CreateCommittedHistory(authorization);
        await using var historyStore = new ThrowOnceCompletionHistoryStore(history);
        var stabilityProvider = new CountingStabilityProvider();
        var finalProvider = new CountingFinalProvider();

        var firstException = await CaptureThrowsAsync<InvalidOperationException>(async () =>
            await FileDeleteOperationOrchestrator.ExecuteAsync(
                authorization,
                stabilityProvider,
                finalProvider,
                historyStore));

        StringAssert.Contains(firstException.Message, "completion was not proven");
        Assert.AreEqual(1, historyStore.CompleteCount);
        Assert.IsFalse(historyStore.Current.TerminalState.HasValue);
        Assert.IsTrue(historyStore.Current.Entries.All(static entry =>
            entry.State == FileDeleteOperationActionEntryState.Committed));
        Assert.AreEqual(0, stabilityProvider.AcquireCount);
        Assert.AreEqual(0, finalProvider.AcquireCount);

        var result = await FileDeleteOperationOrchestrator.ExecuteAsync(
            authorization,
            stabilityProvider,
            finalProvider,
            historyStore);

        Assert.AreEqual(2, historyStore.CompleteCount);
        Assert.AreEqual(0, result.MutatedEntryCount);
        Assert.AreEqual(2, result.PreviouslyTerminalEntryCount);
        Assert.AreEqual(
            FileDeleteOperationActionTerminalState.Succeeded,
            result.CompletedHistory.TerminalState);
        Assert.IsTrue(result.CompletedHistory.CompletedAtUtc.HasValue);
        Assert.IsFalse(result.CompletionObservedFromExistingHistory);
        Assert.AreEqual(0, stabilityProvider.AcquireCount);
        Assert.AreEqual(0, finalProvider.AcquireCount);
    }

    private static FileDeleteOperationUserAuthorizationReceipt CreateAuthorization(int count)
    {
        var root = @"C:\Users\Alice\Temp";
        var entries = new FileOperationEntry[count];
        var validationItems = new FileDeleteOperationExecutionValidationItem[count];
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            var path = $@"{root}\completion-retry-{ordinal}-{Guid.NewGuid():N}.tmp";
            var entry = new FileOperationEntry(path, Path.GetFileName(path), IsDirectory: false);
            entries[ordinal] = entry;
            validationItems[ordinal] = new FileDeleteOperationExecutionValidationItem(
                entry,
                new FileOperationCanonicalPath(
                    path,
                    path,
                    FileOperationCanonicalPathState.File,
                    IsLeafReparsePoint: false,
                    new FileIdentity(101, checked((ulong)(10101 + ordinal)))),
                FileDeleteOperationExecutionValidationDecision.ReadyForAuthorizationReview,
                "ready");
        }

        var plan = new FileDeleteOperationPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new FileDeleteOperationIntent(
                "left",
                Guid.NewGuid(),
                root,
                entries));
        var validation = new FileDeleteOperationExecutionValidationResult(
            plan,
            new FileOperationCanonicalPath(
                root,
                root,
                FileOperationCanonicalPathState.Directory,
                IsLeafReparsePoint: false,
                new FileIdentity(101, 10100)),
            validationItems,
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
            authorization.Items.Select((item, ordinal) =>
            {
                var mutationStartedAt = startedAt.AddMilliseconds(ordinal + 1);
                return new FileDeleteOperationActionEntry(
                    ordinal,
                    item.Entry,
                    item.CanonicalPath,
                    item.Identity,
                    FileDeleteOperationActionEntryState.Committed,
                    mutationStartedAt,
                    mutationStartedAt.AddMilliseconds(10),
                    Failure: null);
            }));
    }

    private static FileDeleteOperationActionHistory CompleteSucceeded(
        FileDeleteOperationActionHistory history) =>
        new(
            history.OperationId,
            history.AuthorizationId,
            history.QueuedAtUtc,
            history.ValidatedAtUtc,
            history.AuthorizedAtUtc,
            history.StartedAtUtc,
            history.StartedAtUtc.AddMinutes(1),
            history.SourcePaneId,
            history.SourceTabId,
            history.CanonicalSourceDirectoryPath,
            history.SourceDirectoryIdentity,
            FileDeleteOperationActionTerminalState.Succeeded,
            history.Entries);

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

    private sealed class ThrowOnceCompletionHistoryStore : IFileDeleteOperationActionHistoryStore
    {
        public ThrowOnceCompletionHistoryStore(FileDeleteOperationActionHistory history)
        {
            Current = history;
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
            RequireOperation(operationId);
            Assert.IsFalse(cancellationToken.CanBeCanceled);
            CompleteCount++;
            if (CompleteCount == 1)
            {
                throw new InvalidOperationException("synthetic completion failure before persistence");
            }

            Current = CompleteSucceeded(Current);
            return new ValueTask<FileDeleteOperationActionHistory>(Current);
        }

        public ValueTask<FileDeleteOperationActionHistory?> GetAsync(
            Guid operationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireOperation(operationId);
            return new ValueTask<FileDeleteOperationActionHistory?>(Current);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void RequireOperation(Guid operationId)
        {
            if (operationId != Current.OperationId)
            {
                throw new InvalidOperationException("operation mismatch");
            }
        }
    }

    private sealed class CountingStabilityProvider : IFileDeleteOperationStabilityLeaseProvider
    {
        public int AcquireCount { get; private set; }

        public ValueTask<IFileDeleteOperationStabilityLease> AcquireAsync(
            FileDeleteOperationStabilityLeaseRequest request,
            CancellationToken cancellationToken = default)
        {
            AcquireCount++;
            throw new InvalidOperationException("completion-only retry must not reacquire stability");
        }
    }

    private sealed class CountingFinalProvider : IFileDeleteOperationFinalMutationLeaseProvider
    {
        public int AcquireCount { get; private set; }

        public ValueTask<IFileDeleteOperationFinalMutationLease> AcquireAsync(
            FileDeleteOperationFinalMutationLeaseRequest request,
            CancellationToken cancellationToken = default)
        {
            AcquireCount++;
            throw new InvalidOperationException("completion-only retry must not reacquire final mutation capability");
        }
    }
}
