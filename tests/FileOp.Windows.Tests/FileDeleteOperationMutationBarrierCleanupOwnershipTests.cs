using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileDeleteOperationMutationBarrierCleanupOwnershipTests
{
    [TestMethod]
    public async Task FailedBarrierReleaseRetainsCleanupOnlyLeaseOwnershipUntilRetrySucceeds()
    {
        var authorization = CreateAuthorization();
        await using var historyStore = new PendingHistoryStore(CreateHistory(authorization));
        var finalLease = new FailingFinalLease(disposeFailures: 1);
        var finalScope = await CreateFinalScopeAsync(authorization, historyStore, finalLease);

        FileDeleteOperationFinalLeaseReleaseException cleanup;
        try
        {
            await FileDeleteOperationMutationBarrier.ClaimAsync(finalScope, historyStore);
            Assert.Fail("Expected cleanup ownership exception after synthetic barrier and release failures.");
            throw new InvalidOperationException("Unreachable.");
        }
        catch (FileDeleteOperationFinalLeaseReleaseException exception)
        {
            cleanup = exception;
        }

        Assert.IsFalse(finalScope.FinalLeaseHeld);
        Assert.AreEqual(1, historyStore.MarkMutationStartedCount);
        Assert.AreEqual(FileDeleteOperationActionEntryState.Pending, historyStore.Current.Entries[0].State);
        Assert.AreEqual(1, finalLease.DisposeCount);
        Assert.IsTrue(cleanup.FinalLeaseReleasePending);
        Assert.IsFalse(cleanup.DeleteMutationAuthorized);

        await cleanup.RetryFinalLeaseReleaseAsync();
        Assert.AreEqual(2, finalLease.DisposeCount);
        Assert.IsFalse(cleanup.FinalLeaseReleasePending);
        Assert.IsFalse(cleanup.DeleteMutationAuthorized);

        await cleanup.DisposeAsync();
        Assert.AreEqual(2, finalLease.DisposeCount);
    }

    private static async Task<FileDeleteOperationFinalMutationLeaseScope> CreateFinalScopeAsync(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        PendingHistoryStore historyStore,
        FailingFinalLease finalLease)
    {
        var readOnlyRequest = new FileDeleteOperationStabilityLeaseRequest(authorization, 0);
        var authorizedItem = authorization.Items[0];
        var readOnlyEvidence = new FileDeleteOperationStabilityLeaseEvidence(
            readOnlyRequest,
            authorization.CanonicalSourceDirectoryPath,
            authorization.SourceDirectoryIdentity,
            authorizedItem.CanonicalPath,
            authorizedItem.Identity);
        var preparation = await FileDeleteOperationPreMutationPreparation.PrepareAsync(
            authorization,
            0,
            new ReadOnlyProvider(new ReadOnlyLease(readOnlyEvidence)),
            historyStore);

        return await FileDeleteOperationFinalMutationLeasePreparation.AcquireAsync(
            preparation,
            new FinalProvider(finalLease));
    }

    private static FileDeleteOperationUserAuthorizationReceipt CreateAuthorization()
    {
        var path = $@"C:\Users\Alice\Temp\cleanup-{Guid.NewGuid():N}.tmp";
        var entry = new FileOperationEntry(path, Path.GetFileName(path), IsDirectory: false);
        var intent = new FileDeleteOperationIntent(
            "left",
            Guid.NewGuid(),
            @"C:\Users\Alice\Temp",
            new[] { entry });
        var plan = new FileDeleteOperationPlan(Guid.NewGuid(), DateTimeOffset.UtcNow, intent);
        var validation = new FileDeleteOperationExecutionValidationResult(
            plan,
            new FileOperationCanonicalPath(
                intent.SourceDirectoryPath,
                intent.SourceDirectoryPath,
                FileOperationCanonicalPathState.Directory,
                IsLeafReparsePoint: false,
                new FileIdentity(55, 5500)),
            new[]
            {
                new FileDeleteOperationExecutionValidationItem(
                    entry,
                    new FileOperationCanonicalPath(
                        path,
                        path,
                        FileOperationCanonicalPathState.File,
                        IsLeafReparsePoint: false,
                        new FileIdentity(55, 5501)),
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
        FileDeleteOperationUserAuthorizationReceipt authorization)
    {
        var item = authorization.Items[0];
        return new FileDeleteOperationActionHistory(
            authorization.PlanId,
            authorization.AuthorizationId,
            authorization.Plan.QueuedAtUtc,
            authorization.ValidatedAtUtc,
            authorization.AuthorizedAtUtc,
            authorization.AuthorizedAtUtc.AddMilliseconds(1),
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
                    FileDeleteOperationActionEntryState.Pending,
                    MutationStartedAtUtc: null,
                    CompletedAtUtc: null,
                    Failure: null),
            });
    }

    private sealed class PendingHistoryStore : IFileDeleteOperationActionHistoryStore
    {
        public PendingHistoryStore(FileDeleteOperationActionHistory history)
        {
            Current = history;
        }

        public FileDeleteOperationActionHistory Current { get; }
        public int MarkMutationStartedCount { get; private set; }

        public ValueTask<FileDeleteOperationActionHistory?> GetAsync(Guid operationId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<FileDeleteOperationActionHistory?>(Current);
        }

        public ValueTask<FileDeleteOperationActionHistory> MarkMutationStartedAsync(Guid operationId, int ordinal, CancellationToken cancellationToken = default)
        {
            MarkMutationStartedCount++;
            throw new InvalidOperationException("synthetic barrier failure before persistence");
        }

        public ValueTask<FileDeleteOperationActionHistory> BeginAsync(FileDeleteOperationUserAuthorizationReceipt authorization, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FileDeleteOperationActionHistory> CommitDeletedAsync(Guid operationId, int ordinal, FileIdentity deletedSourceIdentity, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FileDeleteOperationActionHistory> MarkFailedAsync(Guid operationId, int ordinal, FileOperationFailure failure, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FileDeleteOperationActionHistory> MarkMutationRecoveryRequiredAsync(Guid operationId, int ordinal, FileOperationFailure failure, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FileDeleteOperationActionHistory> CompleteAsync(Guid operationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ReadOnlyProvider : IFileDeleteOperationStabilityLeaseProvider
    {
        private readonly IFileDeleteOperationStabilityLease _lease;
        public ReadOnlyProvider(IFileDeleteOperationStabilityLease lease) => _lease = lease;
        public ValueTask<IFileDeleteOperationStabilityLease> AcquireAsync(FileDeleteOperationStabilityLeaseRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<IFileDeleteOperationStabilityLease>(_lease);
        }
    }

    private sealed class ReadOnlyLease : IFileDeleteOperationStabilityLease
    {
        public ReadOnlyLease(FileDeleteOperationStabilityLeaseEvidence evidence) => Evidence = evidence;
        public FileDeleteOperationStabilityLeaseEvidence Evidence { get; }
        public bool DeleteMutationAuthorized => false;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FinalProvider : IFileDeleteOperationFinalMutationLeaseProvider
    {
        private readonly FailingFinalLease _lease;
        public FinalProvider(FailingFinalLease lease) => _lease = lease;
        public ValueTask<IFileDeleteOperationFinalMutationLease> AcquireAsync(FileDeleteOperationFinalMutationLeaseRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _lease.AttachEvidence(new FileDeleteOperationFinalMutationLeaseEvidence(
                request,
                request.Authorization.CanonicalSourceDirectoryPath,
                request.Authorization.SourceDirectoryIdentity,
                request.AuthorizedItem.CanonicalPath,
                request.AuthorizedItem.Identity));
            return new ValueTask<IFileDeleteOperationFinalMutationLease>(_lease);
        }
    }

    private sealed class FailingFinalLease : IFileDeleteOperationFinalMutationLease
    {
        private int _remainingDisposeFailures;
        private bool _held = true;
        private FileDeleteOperationFinalMutationLeaseEvidence? _evidence;
        public FailingFinalLease(int disposeFailures) => _remainingDisposeFailures = disposeFailures;
        public FileDeleteOperationFinalMutationLeaseEvidence Evidence => _evidence ?? throw new InvalidOperationException("Final evidence has not been attached.");
        public bool DeleteAccessCapabilityHeld => _held;
        public bool DeleteMutationAuthorized => false;
        public int DisposeCount { get; private set; }
        public void AttachEvidence(FileDeleteOperationFinalMutationLeaseEvidence evidence) => _evidence = evidence;
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (_remainingDisposeFailures > 0)
            {
                _remainingDisposeFailures--;
                throw new InvalidOperationException("synthetic detached final release failure");
            }
            _held = false;
            return ValueTask.CompletedTask;
        }
    }
}
