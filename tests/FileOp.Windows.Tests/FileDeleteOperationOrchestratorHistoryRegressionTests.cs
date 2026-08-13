using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileDeleteOperationOrchestratorHistoryRegressionTests
{
    [TestMethod]
    public async Task PreviouslyCommittedEntryRegressionToPendingIsRejectedBeforeProviderAcquisition()
    {
        var authorization = CreateAuthorization();
        var settled = CreateHistory(
            authorization,
            FileDeleteOperationActionEntryState.Committed);
        var regressed = CreateHistory(
            authorization,
            FileDeleteOperationActionEntryState.Pending,
            startedAtUtc: settled.StartedAtUtc);
        await AssertRegressionRejectedAsync(authorization, settled, regressed);
    }

    [TestMethod]
    public async Task PreviouslyFailedEntryRegressionToPendingIsRejectedBeforeProviderAcquisition()
    {
        var authorization = CreateAuthorization();
        var settled = CreateHistory(
            authorization,
            FileDeleteOperationActionEntryState.Failed);
        var regressed = CreateHistory(
            authorization,
            FileDeleteOperationActionEntryState.Pending,
            startedAtUtc: settled.StartedAtUtc);
        await AssertRegressionRejectedAsync(authorization, settled, regressed);
    }

    [TestMethod]
    public async Task PreviouslyCommittedEntryEscalatingToRecoveryRequiredStopsAsRecovery()
    {
        var authorization = CreateAuthorization();
        var settled = CreateHistory(
            authorization,
            FileDeleteOperationActionEntryState.Committed);
        var recovery = CreateHistory(
            authorization,
            FileDeleteOperationActionEntryState.RecoveryRequired,
            startedAtUtc: settled.StartedAtUtc);
        await using var historyStore = new RegressingHistoryStore(settled, recovery);
        var stabilityProvider = new CountingStabilityProvider();
        var finalProvider = new CountingFinalProvider();

        var exception = await CaptureThrowsAsync<FileDeleteOperationOrchestrationRecoveryRequiredException>(async () =>
            await FileDeleteOperationOrchestrator.ExecuteAsync(
                authorization,
                stabilityProvider,
                finalProvider,
                historyStore));

        Assert.AreSame(recovery, exception.History);
        Assert.IsFalse(exception.DeleteMutationAuthorized);
        Assert.AreEqual(2, historyStore.GetCount);
        Assert.AreEqual(0, stabilityProvider.AcquireCount);
        Assert.AreEqual(0, finalProvider.AcquireCount);
    }

    private static async Task AssertRegressionRejectedAsync(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        FileDeleteOperationActionHistory settled,
        FileDeleteOperationActionHistory regressed)
    {
        await using var historyStore = new RegressingHistoryStore(settled, regressed);
        var stabilityProvider = new CountingStabilityProvider();
        var finalProvider = new CountingFinalProvider();

        var exception = await CaptureThrowsAsync<InvalidOperationException>(async () =>
            await FileDeleteOperationOrchestrator.ExecuteAsync(
                authorization,
                stabilityProvider,
                finalProvider,
                historyStore));

        StringAssert.Contains(exception.Message, "destructive replay is refused");
        Assert.AreEqual(2, historyStore.GetCount);
        Assert.AreEqual(0, stabilityProvider.AcquireCount);
        Assert.AreEqual(0, finalProvider.AcquireCount);
    }

    private static FileDeleteOperationUserAuthorizationReceipt CreateAuthorization()
    {
        var root = @"C:\Users\Alice\Temp";
        var path = $@"{root}\history-regression-{Guid.NewGuid():N}.tmp";
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
                new FileIdentity(97, 9700)),
            new[]
            {
                new FileDeleteOperationExecutionValidationItem(
                    entry,
                    new FileOperationCanonicalPath(
                        path,
                        path,
                        FileOperationCanonicalPathState.File,
                        IsLeafReparsePoint: false,
                        new FileIdentity(97, 9701)),
                    FileDeleteOperationExecutionValidationDecision.ReadyForAuthorizationReview,
                    "ready"),
            },
            FileDeleteOperationExecutionValidationStatus.ReadyForAuthorizationReview,
            DateTimeOffset.UtcNow,
            "ready");
        return new FileDeleteOperationUserAuthorizationIssuer()
            .IssueAfterExplicitUserConfirmation(validation);
    }

    private static FileDeleteOperationActionHistory CreateHistory(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        FileDeleteOperationActionEntryState state,
        DateTimeOffset? startedAtUtc = null)
    {
        var startedAt = startedAtUtc ?? authorization.AuthorizedAtUtc.AddMilliseconds(1);
        var mutationStartedAt = state is FileDeleteOperationActionEntryState.Committed or
                FileDeleteOperationActionEntryState.RecoveryRequired
            ? startedAt.AddMilliseconds(1)
            : (DateTimeOffset?)null;
        var completedAt = state is FileDeleteOperationActionEntryState.Committed or
                FileDeleteOperationActionEntryState.Failed or
                FileDeleteOperationActionEntryState.RecoveryRequired
            ? startedAt.AddMilliseconds(2)
            : (DateTimeOffset?)null;
        var failure = state switch
        {
            FileDeleteOperationActionEntryState.Failed => new FileOperationFailure(
                "SyntheticSettledFailure",
                "Synthetic safe pre-mutation failure.",
                authorization.Items[0].CanonicalPath,
                Retryable: false),
            FileDeleteOperationActionEntryState.RecoveryRequired => new FileOperationFailure(
                "SyntheticRecoveryEscalation",
                "Synthetic recovery-sensitive escalation.",
                authorization.Items[0].CanonicalPath,
                Retryable: false),
            _ => null,
        };
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
                    state,
                    mutationStartedAt,
                    completedAt,
                    failure),
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

    private sealed class RegressingHistoryStore : IFileDeleteOperationActionHistoryStore
    {
        private readonly FileDeleteOperationActionHistory _settled;
        private readonly FileDeleteOperationActionHistory _next;

        public RegressingHistoryStore(
            FileDeleteOperationActionHistory settled,
            FileDeleteOperationActionHistory next)
        {
            _settled = settled;
            _next = next;
        }

        public int GetCount { get; private set; }

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
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<FileDeleteOperationActionHistory?> GetAsync(
            Guid operationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (operationId != _settled.OperationId)
            {
                throw new InvalidOperationException("operation mismatch");
            }

            GetCount++;
            return new ValueTask<FileDeleteOperationActionHistory?>(
                GetCount == 1 ? _settled : _next);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingStabilityProvider : IFileDeleteOperationStabilityLeaseProvider
    {
        public int AcquireCount { get; private set; }

        public ValueTask<IFileDeleteOperationStabilityLease> AcquireAsync(
            FileDeleteOperationStabilityLeaseRequest request,
            CancellationToken cancellationToken = default)
        {
            AcquireCount++;
            throw new InvalidOperationException("settled history must be rejected or stopped before stability acquisition");
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
            throw new InvalidOperationException("settled history must be rejected or stopped before final acquisition");
        }
    }
}
