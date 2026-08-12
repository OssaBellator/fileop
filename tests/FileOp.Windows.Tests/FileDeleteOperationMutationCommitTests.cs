using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileDeleteOperationMutationCommitTests
{
    [TestMethod]
    public async Task SuccessMutatesOnceReleasesThenCommitsExactIdentity()
    {
        var authorization = CreateAuthorization();
        await using var historyStore = new FakeHistoryStore(CreateHistory(authorization));
        var provider = new FakeFinalProvider();
        var barrier = await CreateBarrierAsync(authorization, historyStore, provider);

        var result = await FileDeleteOperationMutationCommit.ExecuteAsync(
            barrier,
            historyStore);

        Assert.AreEqual(1, provider.LastLease!.MutationCount);
        Assert.AreEqual(1, provider.LastLease.DisposeCount);
        Assert.IsFalse(provider.LastLease.LastMutationTokenCanBeCanceled);
        Assert.AreEqual(1, historyStore.CommitCount);
        Assert.IsFalse(historyStore.LastCommitTokenCanBeCanceled);
        Assert.AreEqual(0, historyStore.RecoveryCount);
        Assert.AreEqual(authorization.Items[0].Identity, historyStore.LastCommittedIdentity);
        Assert.AreEqual(
            FileDeleteOperationActionEntryState.Committed,
            result.CommittedHistory.Entries[0].State);
        Assert.IsTrue(result.DeleteMutationPerformed);
        Assert.IsTrue(result.DurableCommitObserved);
        Assert.IsFalse(result.DeleteMutationAuthorized);
        Assert.IsFalse(barrier.FinalLeaseHeld);
        Assert.IsFalse(barrier.DeleteMutationAuthorized);

        await AssertThrowsAsync<InvalidOperationException>(async () =>
            await FileDeleteOperationMutationCommit.ExecuteAsync(
                barrier,
                historyStore));
        Assert.AreEqual(1, provider.LastLease.MutationCount);
    }

    [TestMethod]
    public async Task MutationFailureMarksRecoveryReleasesAndNeverCommits()
    {
        var authorization = CreateAuthorization();
        await using var historyStore = new FakeHistoryStore(CreateHistory(authorization));
        var provider = new FakeFinalProvider(mutationFails: true);
        var barrier = await CreateBarrierAsync(authorization, historyStore, provider);

        await AssertThrowsAsync<InvalidOperationException>(async () =>
            await FileDeleteOperationMutationCommit.ExecuteAsync(
                barrier,
                historyStore));

        Assert.AreEqual(1, provider.LastLease!.MutationCount);
        Assert.AreEqual(1, provider.LastLease.DisposeCount);
        Assert.AreEqual(0, historyStore.CommitCount);
        Assert.AreEqual(1, historyStore.RecoveryCount);
        Assert.IsFalse(historyStore.LastRecoveryTokenCanBeCanceled);
        Assert.AreEqual(
            FileDeleteOperationActionEntryState.RecoveryRequired,
            historyStore.Current.Entries[0].State);
        Assert.AreEqual(
            "DeleteSameLeaseMutationFailed",
            historyStore.Current.Entries[0].Failure!.Code);
        Assert.IsFalse(barrier.FinalLeaseHeld);
    }

    [TestMethod]
    public async Task ReleaseFailureAfterMutationRetainsCleanupOwnershipAndNeverCommits()
    {
        var authorization = CreateAuthorization();
        await using var historyStore = new FakeHistoryStore(CreateHistory(authorization));
        var provider = new FakeFinalProvider(disposeFailures: 1);
        var barrier = await CreateBarrierAsync(authorization, historyStore, provider);

        FileDeleteOperationFinalLeaseReleaseException? releaseFailure = null;
        try
        {
            await FileDeleteOperationMutationCommit.ExecuteAsync(
                barrier,
                historyStore);
        }
        catch (FileDeleteOperationFinalLeaseReleaseException exception)
        {
            releaseFailure = exception;
        }

        Assert.IsNotNull(releaseFailure);
        Assert.IsTrue(releaseFailure.FinalLeaseReleasePending);
        Assert.IsFalse(releaseFailure.DeleteMutationAuthorized);
        Assert.AreEqual(1, provider.LastLease!.MutationCount);
        Assert.AreEqual(1, provider.LastLease.DisposeCount);
        Assert.AreEqual(0, historyStore.CommitCount);
        Assert.AreEqual(1, historyStore.RecoveryCount);
        Assert.AreEqual(
            FileDeleteOperationActionEntryState.RecoveryRequired,
            historyStore.Current.Entries[0].State);
        Assert.AreEqual(
            "DeleteSameLeaseReleaseFailed",
            historyStore.Current.Entries[0].Failure!.Code);

        await releaseFailure.RetryFinalLeaseReleaseAsync();
        Assert.IsFalse(releaseFailure.FinalLeaseReleasePending);
        Assert.AreEqual(2, provider.LastLease.DisposeCount);
    }

    [TestMethod]
    public async Task CommitThrowAfterPersistenceIsInspectedAndAcceptedWithoutReplay()
    {
        var authorization = CreateAuthorization();
        await using var historyStore = new FakeHistoryStore(
            CreateHistory(authorization),
            commitBehavior: CommitBehavior.ThrowAfterPersist);
        var provider = new FakeFinalProvider();
        var barrier = await CreateBarrierAsync(authorization, historyStore, provider);

        var result = await FileDeleteOperationMutationCommit.ExecuteAsync(
            barrier,
            historyStore);

        Assert.AreEqual(1, provider.LastLease!.MutationCount);
        Assert.AreEqual(1, provider.LastLease.DisposeCount);
        Assert.AreEqual(1, historyStore.CommitCount);
        Assert.AreEqual(1, historyStore.GetCountAfterBarrier);
        Assert.AreEqual(0, historyStore.RecoveryCount);
        Assert.AreEqual(
            FileDeleteOperationActionEntryState.Committed,
            result.CommittedHistory.Entries[0].State);
    }

    [TestMethod]
    public async Task CommitThrowBeforePersistenceMarksRecoveryWithoutMutationReplay()
    {
        var authorization = CreateAuthorization();
        await using var historyStore = new FakeHistoryStore(
            CreateHistory(authorization),
            commitBehavior: CommitBehavior.ThrowBeforePersist);
        var provider = new FakeFinalProvider();
        var barrier = await CreateBarrierAsync(authorization, historyStore, provider);

        await AssertThrowsAsync<InvalidOperationException>(async () =>
            await FileDeleteOperationMutationCommit.ExecuteAsync(
                barrier,
                historyStore));

        Assert.AreEqual(1, provider.LastLease!.MutationCount);
        Assert.AreEqual(1, provider.LastLease.DisposeCount);
        Assert.AreEqual(1, historyStore.CommitCount);
        Assert.AreEqual(1, historyStore.GetCountAfterBarrier);
        Assert.AreEqual(1, historyStore.RecoveryCount);
        Assert.AreEqual(
            FileDeleteOperationActionEntryState.RecoveryRequired,
            historyStore.Current.Entries[0].State);
        Assert.AreEqual(
            "DeleteMutationCommitOutcomeAmbiguous",
            historyStore.Current.Entries[0].Failure!.Code);
    }

    private static async Task<FileDeleteOperationMutationBarrierScope> CreateBarrierAsync(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        FakeHistoryStore historyStore,
        FakeFinalProvider finalProvider)
    {
        var preparation = await FileDeleteOperationPreMutationPreparation.PrepareAsync(
            authorization,
            0,
            new FakeReadOnlyProvider(CreateReadOnlyLease(authorization)),
            historyStore);
        var finalScope = await FileDeleteOperationFinalMutationLeasePreparation.AcquireAsync(
            preparation,
            finalProvider);
        return await FileDeleteOperationMutationBarrier.ClaimAsync(
            finalScope,
            historyStore);
    }

    private static FakeReadOnlyLease CreateReadOnlyLease(
        FileDeleteOperationUserAuthorizationReceipt authorization)
    {
        var request = new FileDeleteOperationStabilityLeaseRequest(authorization, 0);
        var item = authorization.Items[0];
        return new FakeReadOnlyLease(
            new FileDeleteOperationStabilityLeaseEvidence(
                request,
                authorization.CanonicalSourceDirectoryPath,
                authorization.SourceDirectoryIdentity,
                item.CanonicalPath,
                item.Identity));
    }

    private static FileDeleteOperationUserAuthorizationReceipt CreateAuthorization()
    {
        var path = $@"C:\Users\Alice\Temp\mutation-{Guid.NewGuid():N}.tmp";
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
                new FileIdentity(54, 5400)),
            new[]
            {
                new FileDeleteOperationExecutionValidationItem(
                    entry,
                    new FileOperationCanonicalPath(
                        path,
                        path,
                        FileOperationCanonicalPathState.File,
                        IsLeafReparsePoint: false,
                        new FileIdentity(54, 5401)),
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

    private static FileDeleteOperationActionHistory WithState(
        FileDeleteOperationActionHistory history,
        FileDeleteOperationActionEntryState state,
        FileOperationFailure? failure = null)
    {
        var previous = history.Entries[0];
        DateTimeOffset? mutationStarted = state == FileDeleteOperationActionEntryState.Pending
            ? null
            : previous.MutationStartedAtUtc ?? history.AuthorizedAtUtc.AddSeconds(1);
        DateTimeOffset? completed = state is FileDeleteOperationActionEntryState.Committed or
            FileDeleteOperationActionEntryState.RecoveryRequired
                ? mutationStarted!.Value.AddMilliseconds(1)
                : null;
        var entry = new FileDeleteOperationActionEntry(
            previous.Ordinal,
            previous.Entry,
            previous.CanonicalSourcePath,
            previous.SourceIdentity,
            state,
            mutationStarted,
            completed,
            failure);
        return new FileDeleteOperationActionHistory(
            history.OperationId,
            history.AuthorizationId,
            history.QueuedAtUtc,
            history.ValidatedAtUtc,
            history.AuthorizedAtUtc,
            history.StartedAtUtc,
            completedAtUtc: null,
            history.SourcePaneId,
            history.SourceTabId,
            history.CanonicalSourceDirectoryPath,
            history.SourceDirectoryIdentity,
            terminalState: null,
            new[] { entry });
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        Assert.Fail($"Expected {typeof(TException).Name} to be thrown.");
    }

    private enum CommitBehavior
    {
        Success,
        ThrowBeforePersist,
        ThrowAfterPersist,
    }

    private sealed class FakeHistoryStore : IFileDeleteOperationActionHistoryStore
    {
        private readonly CommitBehavior _commitBehavior;
        private bool _barrierClaimed;

        public FakeHistoryStore(
            FileDeleteOperationActionHistory history,
            CommitBehavior commitBehavior = CommitBehavior.Success)
        {
            Current = history;
            _commitBehavior = commitBehavior;
        }

        public FileDeleteOperationActionHistory Current { get; private set; }

        public int CommitCount { get; private set; }

        public int RecoveryCount { get; private set; }

        public int GetCountAfterBarrier { get; private set; }

        public FileIdentity? LastCommittedIdentity { get; private set; }

        public bool LastCommitTokenCanBeCanceled { get; private set; }

        public bool LastRecoveryTokenCanBeCanceled { get; private set; }

        public ValueTask<FileDeleteOperationActionHistory> BeginAsync(
            FileDeleteOperationUserAuthorizationReceipt authorization,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<FileDeleteOperationActionHistory> MarkMutationStartedAsync(
            Guid operationId,
            int ordinal,
            CancellationToken cancellationToken = default)
        {
            _barrierClaimed = true;
            Current = WithState(Current, FileDeleteOperationActionEntryState.MutationStarted);
            return new ValueTask<FileDeleteOperationActionHistory>(Current);
        }

        public ValueTask<FileDeleteOperationActionHistory> CommitDeletedAsync(
            Guid operationId,
            int ordinal,
            FileIdentity deletedSourceIdentity,
            CancellationToken cancellationToken = default)
        {
            CommitCount++;
            LastCommittedIdentity = deletedSourceIdentity;
            LastCommitTokenCanBeCanceled = cancellationToken.CanBeCanceled;
            if (deletedSourceIdentity != Current.Entries[ordinal].SourceIdentity)
            {
                throw new InvalidOperationException("commit identity mismatch");
            }
            if (_commitBehavior == CommitBehavior.ThrowBeforePersist)
            {
                throw new InvalidOperationException("synthetic commit failure before persistence");
            }

            Current = WithState(Current, FileDeleteOperationActionEntryState.Committed);
            if (_commitBehavior == CommitBehavior.ThrowAfterPersist)
            {
                throw new InvalidOperationException("synthetic commit failure after persistence");
            }

            return new ValueTask<FileDeleteOperationActionHistory>(Current);
        }

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
            CancellationToken cancellationToken = default)
        {
            RecoveryCount++;
            LastRecoveryTokenCanBeCanceled = cancellationToken.CanBeCanceled;
            if (Current.Entries[ordinal].State != FileDeleteOperationActionEntryState.MutationStarted)
            {
                throw new InvalidOperationException("recovery requires MutationStarted");
            }

            Current = WithState(
                Current,
                FileDeleteOperationActionEntryState.RecoveryRequired,
                failure);
            return new ValueTask<FileDeleteOperationActionHistory>(Current);
        }

        public ValueTask<FileDeleteOperationActionHistory> CompleteAsync(
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<FileDeleteOperationActionHistory?> GetAsync(
            Guid operationId,
            CancellationToken cancellationToken = default)
        {
            if (_barrierClaimed)
            {
                GetCountAfterBarrier++;
            }
            return new ValueTask<FileDeleteOperationActionHistory?>(Current);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeReadOnlyProvider : IFileDeleteOperationStabilityLeaseProvider
    {
        private readonly IFileDeleteOperationStabilityLease _lease;

        public FakeReadOnlyProvider(IFileDeleteOperationStabilityLease lease)
        {
            _lease = lease;
        }

        public ValueTask<IFileDeleteOperationStabilityLease> AcquireAsync(
            FileDeleteOperationStabilityLeaseRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<IFileDeleteOperationStabilityLease>(_lease);
        }
    }

    private sealed class FakeReadOnlyLease : IFileDeleteOperationStabilityLease
    {
        public FakeReadOnlyLease(FileDeleteOperationStabilityLeaseEvidence evidence)
        {
            Evidence = evidence;
        }

        public FileDeleteOperationStabilityLeaseEvidence Evidence { get; }

        public bool DeleteMutationAuthorized => false;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeFinalProvider : IFileDeleteOperationFinalMutationLeaseProvider
    {
        private readonly bool _mutationFails;
        private readonly int _disposeFailures;

        public FakeFinalProvider(
            bool mutationFails = false,
            int disposeFailures = 0)
        {
            _mutationFails = mutationFails;
            _disposeFailures = disposeFailures;
        }

        public FakeFinalLease? LastLease { get; private set; }

        public ValueTask<IFileDeleteOperationFinalMutationLease> AcquireAsync(
            FileDeleteOperationFinalMutationLeaseRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = request.AuthorizedItem;
            var evidence = new FileDeleteOperationFinalMutationLeaseEvidence(
                request,
                request.Authorization.CanonicalSourceDirectoryPath,
                request.Authorization.SourceDirectoryIdentity,
                item.CanonicalPath,
                item.Identity);
            LastLease = new FakeFinalLease(
                evidence,
                _mutationFails,
                _disposeFailures);
            return new ValueTask<IFileDeleteOperationFinalMutationLease>(LastLease);
        }
    }

    private sealed class FakeFinalLease :
        IFileDeleteOperationFinalMutationLease,
        IFileDeleteOperationSameLeaseMutation
    {
        private readonly bool _mutationFails;
        private int _remainingDisposeFailures;
        private bool _held = true;

        public FakeFinalLease(
            FileDeleteOperationFinalMutationLeaseEvidence evidence,
            bool mutationFails,
            int disposeFailures)
        {
            Evidence = evidence;
            _mutationFails = mutationFails;
            _remainingDisposeFailures = disposeFailures;
        }

        public FileDeleteOperationFinalMutationLeaseEvidence Evidence { get; }

        public bool DeleteAccessCapabilityHeld => _held;

        public bool DeleteMutationAuthorized => false;

        public int MutationCount { get; private set; }

        public int DisposeCount { get; private set; }

        public bool LastMutationTokenCanBeCanceled { get; private set; }

        public ValueTask MarkDeletePendingAsync(
            FileDeleteOperationMutationAuthorization authorization,
            CancellationToken cancellationToken = default)
        {
            MutationCount++;
            LastMutationTokenCanBeCanceled = cancellationToken.CanBeCanceled;
            if (!authorization.IsBoundTo(Evidence))
            {
                throw new UnauthorizedAccessException("mutation authorization mismatch");
            }
            if (_mutationFails)
            {
                throw new InvalidOperationException("synthetic same-lease mutation failure");
            }
            if (MutationCount != 1)
            {
                throw new InvalidOperationException("mutation replay");
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (_remainingDisposeFailures > 0)
            {
                _remainingDisposeFailures--;
                throw new InvalidOperationException("synthetic final lease release failure");
            }

            _held = false;
            return ValueTask.CompletedTask;
        }
    }
}
