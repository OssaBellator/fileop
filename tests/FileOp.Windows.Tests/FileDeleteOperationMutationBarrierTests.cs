using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileDeleteOperationMutationBarrierTests
{
    [TestMethod]
    public async Task SuccessfulClaimTransfersFinalLeaseAndCreatesLiveBarrierAuthority()
    {
        var authorization = CreateAuthorization();
        await using var historyStore = new FakeHistoryStore(CreateHistory(authorization));
        var (finalScope, finalLease) = await CreateFinalScopeAsync(authorization, historyStore);

        var barrierScope = await FileDeleteOperationMutationBarrier.ClaimAsync(
            finalScope,
            historyStore);

        Assert.AreEqual(1, historyStore.MarkMutationStartedCount);
        Assert.IsFalse(historyStore.LastMutationStartedTokenCanBeCanceled);
        Assert.IsFalse(finalScope.FinalLeaseHeld);
        Assert.IsFalse(finalScope.DeleteAccessCapabilityHeld);
        Assert.IsTrue(barrierScope.FinalLeaseHeld);
        Assert.IsTrue(barrierScope.DeleteAccessCapabilityHeld);
        Assert.IsTrue(barrierScope.MutationBarrierSatisfied);
        Assert.IsTrue(barrierScope.DeleteMutationAuthorized);
        Assert.IsFalse(barrierScope.DeleteMutationPerformed);
        Assert.AreEqual(
            FileDeleteOperationActionEntryState.MutationStarted,
            barrierScope.BarrierHistory.Entries[0].State);

        await finalScope.DisposeAsync();
        Assert.AreEqual(0, finalLease.DisposeCount);

        await barrierScope.DisposeAsync();
        Assert.AreEqual(1, finalLease.DisposeCount);
        Assert.IsFalse(barrierScope.FinalLeaseHeld);
        Assert.IsFalse(barrierScope.DeleteAccessCapabilityHeld);
        Assert.IsFalse(barrierScope.DeleteMutationAuthorized);
        Assert.IsTrue(barrierScope.MutationBarrierSatisfied);
    }

    [TestMethod]
    public async Task RetainedFinalScopeAliasCannotDisposeLeaseAfterTransfer()
    {
        var authorization = CreateAuthorization();
        await using var historyStore = new FakeHistoryStore(
            CreateHistory(authorization),
            blockMutationStarted: true);
        var (finalScope, finalLease) = await CreateFinalScopeAsync(authorization, historyStore);

        var claimTask = FileDeleteOperationMutationBarrier
            .ClaimAsync(finalScope, historyStore)
            .AsTask();
        await historyStore.MutationStartedEntered.Task;

        Assert.IsFalse(finalScope.FinalLeaseHeld);
        await finalScope.DisposeAsync();
        Assert.AreEqual(0, finalLease.DisposeCount);

        historyStore.ReleaseMutationStarted.TrySetResult(true);
        var barrierScope = await claimTask;
        Assert.IsTrue(barrierScope.DeleteMutationAuthorized);

        await barrierScope.DisposeAsync();
        Assert.AreEqual(1, finalLease.DisposeCount);
    }

    [TestMethod]
    public async Task CancellationBeforeDetachLeavesFinalScopeUntouched()
    {
        var authorization = CreateAuthorization();
        await using var historyStore = new FakeHistoryStore(CreateHistory(authorization));
        var (finalScope, finalLease) = await CreateFinalScopeAsync(authorization, historyStore);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await AssertThrowsAsync<OperationCanceledException>(async () =>
            await FileDeleteOperationMutationBarrier.ClaimAsync(
                finalScope,
                historyStore,
                cancellation.Token));

        Assert.IsTrue(finalScope.FinalLeaseHeld);
        Assert.IsTrue(finalScope.DeleteAccessCapabilityHeld);
        Assert.AreEqual(0, historyStore.MarkMutationStartedCount);
        Assert.AreEqual(0, finalLease.DisposeCount);

        await finalScope.DisposeAsync();
        Assert.AreEqual(1, finalLease.DisposeCount);
    }

    [TestMethod]
    public async Task CancellationAfterDetachCannotInterruptDurableBarrierSection()
    {
        var authorization = CreateAuthorization();
        await using var historyStore = new FakeHistoryStore(
            CreateHistory(authorization),
            blockMutationStarted: true);
        var (finalScope, finalLease) = await CreateFinalScopeAsync(authorization, historyStore);
        using var cancellation = new CancellationTokenSource();

        var claimTask = FileDeleteOperationMutationBarrier
            .ClaimAsync(finalScope, historyStore, cancellation.Token)
            .AsTask();
        await historyStore.MutationStartedEntered.Task;
        cancellation.Cancel();
        historyStore.ReleaseMutationStarted.TrySetResult(true);

        var barrierScope = await claimTask;
        Assert.IsFalse(historyStore.LastMutationStartedTokenCanBeCanceled);
        Assert.IsTrue(barrierScope.DeleteMutationAuthorized);
        Assert.AreEqual(0, historyStore.MarkRecoveryRequiredCount);

        await barrierScope.DisposeAsync();
        Assert.AreEqual(1, finalLease.DisposeCount);
    }

    [TestMethod]
    public async Task BarrierWriteThrowWithPendingHistoryReleasesWithoutRecovery()
    {
        var authorization = CreateAuthorization();
        await using var historyStore = new FakeHistoryStore(
            CreateHistory(authorization),
            behavior: MutationStartedBehavior.ThrowBeforePersist);
        var (finalScope, finalLease) = await CreateFinalScopeAsync(authorization, historyStore);

        await AssertThrowsAsync<InvalidOperationException>(async () =>
            await FileDeleteOperationMutationBarrier.ClaimAsync(finalScope, historyStore));

        Assert.IsFalse(finalScope.FinalLeaseHeld);
        Assert.AreEqual(1, historyStore.MarkMutationStartedCount);
        Assert.AreEqual(0, historyStore.MarkRecoveryRequiredCount);
        Assert.AreEqual(FileDeleteOperationActionEntryState.Pending, historyStore.Current.Entries[0].State);
        Assert.AreEqual(1, finalLease.DisposeCount);
    }

    [TestMethod]
    public async Task BarrierWriteThrowAfterPersistIsInspectedRecoveredAndReleased()
    {
        var events = new List<string>();
        var authorization = CreateAuthorization();
        await using var historyStore = new FakeHistoryStore(
            CreateHistory(authorization),
            behavior: MutationStartedBehavior.ThrowAfterPersist,
            events: events);
        var (finalScope, finalLease) = await CreateFinalScopeAsync(
            authorization,
            historyStore,
            events: events);

        await AssertThrowsAsync<InvalidOperationException>(async () =>
            await FileDeleteOperationMutationBarrier.ClaimAsync(finalScope, historyStore));

        Assert.AreEqual(1, historyStore.MarkMutationStartedCount);
        Assert.AreEqual(1, historyStore.MarkRecoveryRequiredCount);
        Assert.IsFalse(historyStore.LastRecoveryTokenCanBeCanceled);
        Assert.AreEqual(
            FileDeleteOperationActionEntryState.RecoveryRequired,
            historyStore.Current.Entries[0].State);
        Assert.AreEqual("DeleteMutationBarrierOutcomeAmbiguous", historyStore.Current.Entries[0].Failure!.Code);
        Assert.AreEqual(1, finalLease.DisposeCount);
        CollectionAssert.AreEqual(
            new[] { "mark-started", "get-history", "mark-recovery", "final-dispose" },
            events.GetRange(events.Count - 4, 4));
    }

    [TestMethod]
    public async Task InvalidReturnedBarrierHistoryMarksRecoveryBeforeLeaseRelease()
    {
        var events = new List<string>();
        var authorization = CreateAuthorization();
        await using var historyStore = new FakeHistoryStore(
            CreateHistory(authorization),
            behavior: MutationStartedBehavior.ReturnCorruptHistory,
            events: events);
        var (finalScope, finalLease) = await CreateFinalScopeAsync(
            authorization,
            historyStore,
            events: events);

        await AssertThrowsAsync<InvalidOperationException>(async () =>
            await FileDeleteOperationMutationBarrier.ClaimAsync(finalScope, historyStore));

        Assert.AreEqual(1, historyStore.MarkRecoveryRequiredCount);
        Assert.AreEqual(
            FileDeleteOperationActionEntryState.RecoveryRequired,
            historyStore.Current.Entries[0].State);
        Assert.AreEqual("DeleteMutationBarrierValidationFailed", historyStore.Current.Entries[0].Failure!.Code);
        Assert.AreEqual(1, finalLease.DisposeCount);
        Assert.IsTrue(events.IndexOf("mark-recovery") < events.IndexOf("final-dispose"));
    }

    [TestMethod]
    public async Task RecoveryPersistenceFailureLeavesDurableMutationStartedSignal()
    {
        var authorization = CreateAuthorization();
        await using var historyStore = new FakeHistoryStore(
            CreateHistory(authorization),
            behavior: MutationStartedBehavior.ThrowAfterPersist,
            failRecovery: true);
        var (finalScope, finalLease) = await CreateFinalScopeAsync(authorization, historyStore);

        await AssertThrowsAsync<AggregateException>(async () =>
            await FileDeleteOperationMutationBarrier.ClaimAsync(finalScope, historyStore));

        Assert.AreEqual(1, historyStore.MarkRecoveryRequiredCount);
        Assert.AreEqual(
            FileDeleteOperationActionEntryState.MutationStarted,
            historyStore.Current.Entries[0].State);
        Assert.AreEqual(1, finalLease.DisposeCount);
    }

    [TestMethod]
    public async Task BarrierScopeDisposalFailureRetainsAuthorityUntilRetrySucceeds()
    {
        var authorization = CreateAuthorization();
        await using var historyStore = new FakeHistoryStore(CreateHistory(authorization));
        var (finalScope, finalLease) = await CreateFinalScopeAsync(
            authorization,
            historyStore,
            finalDisposeFailures: 1);
        var barrierScope = await FileDeleteOperationMutationBarrier.ClaimAsync(
            finalScope,
            historyStore);

        await AssertThrowsAsync<InvalidOperationException>(async () =>
            await barrierScope.DisposeAsync());

        Assert.AreEqual(1, finalLease.DisposeCount);
        Assert.IsTrue(barrierScope.FinalLeaseHeld);
        Assert.IsTrue(barrierScope.DeleteAccessCapabilityHeld);
        Assert.IsTrue(barrierScope.DeleteMutationAuthorized);

        await barrierScope.DisposeAsync();
        await barrierScope.DisposeAsync();

        Assert.AreEqual(2, finalLease.DisposeCount);
        Assert.IsFalse(barrierScope.FinalLeaseHeld);
        Assert.IsFalse(barrierScope.DeleteMutationAuthorized);
    }

    private static async Task<(FileDeleteOperationFinalMutationLeaseScope Scope, FakeFinalLease Lease)> CreateFinalScopeAsync(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        FakeHistoryStore historyStore,
        int finalDisposeFailures = 0,
        List<string>? events = null)
    {
        var readOnlyProvider = new FakeReadOnlyProvider(CreateReadOnlyLease(authorization));
        var preparation = await FileDeleteOperationPreMutationPreparation.PrepareAsync(
            authorization,
            0,
            readOnlyProvider,
            historyStore);
        var finalProvider = new FakeFinalProvider(finalDisposeFailures, events);
        var scope = await FileDeleteOperationFinalMutationLeasePreparation.AcquireAsync(
            preparation,
            finalProvider);
        return (scope, finalProvider.LastLease!);
    }

    private static FakeReadOnlyLease CreateReadOnlyLease(
        FileDeleteOperationUserAuthorizationReceipt authorization)
    {
        var request = new FileDeleteOperationStabilityLeaseRequest(authorization, 0);
        var item = authorization.Items[0];
        var evidence = new FileDeleteOperationStabilityLeaseEvidence(
            request,
            authorization.CanonicalSourceDirectoryPath,
            authorization.SourceDirectoryIdentity,
            item.CanonicalPath,
            item.Identity);
        return new FakeReadOnlyLease(evidence);
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

    private static FileDeleteOperationUserAuthorizationReceipt CreateAuthorization()
    {
        var path = $@"C:\Users\Alice\Temp\barrier-{Guid.NewGuid():N}.tmp";
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
                new FileIdentity(44, 4400)),
            new[]
            {
                new FileDeleteOperationExecutionValidationItem(
                    entry,
                    new FileOperationCanonicalPath(
                        path,
                        path,
                        FileOperationCanonicalPathState.File,
                        IsLeafReparsePoint: false,
                        new FileIdentity(44, 4401)),
                    FileDeleteOperationExecutionValidationDecision.ReadyForAuthorizationReview,
                    "ready"),
            },
            FileDeleteOperationExecutionValidationStatus.ReadyForAuthorizationReview,
            DateTimeOffset.UtcNow,
            "ready");
        return new FileDeleteOperationUserAuthorizationIssuer()
            .IssueAfterExplicitUserConfirmation(validation);
    }

    private static FileDeleteOperationActionHistory WithSelectedState(
        FileDeleteOperationActionHistory history,
        FileDeleteOperationActionEntryState state,
        FileOperationFailure? failure = null,
        bool corruptSourcePane = false)
    {
        var previous = history.Entries[0];
        DateTimeOffset? mutationStarted = state == FileDeleteOperationActionEntryState.Pending
            ? null
            : previous.MutationStartedAtUtc ?? history.AuthorizedAtUtc.AddSeconds(1);
        var completed = state == FileDeleteOperationActionEntryState.RecoveryRequired
            ? mutationStarted!.Value.AddMilliseconds(1)
            : (DateTimeOffset?)null;
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
            corruptSourcePane ? history.SourcePaneId + "-corrupt" : history.SourcePaneId,
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

    private enum MutationStartedBehavior
    {
        Success,
        ThrowBeforePersist,
        ThrowAfterPersist,
        ReturnCorruptHistory,
    }

    private sealed class FakeHistoryStore : IFileDeleteOperationActionHistoryStore
    {
        private readonly MutationStartedBehavior _behavior;
        private readonly bool _failRecovery;
        private readonly bool _blockMutationStarted;
        private readonly List<string>? _events;

        public FakeHistoryStore(
            FileDeleteOperationActionHistory history,
            MutationStartedBehavior behavior = MutationStartedBehavior.Success,
            bool failRecovery = false,
            bool blockMutationStarted = false,
            List<string>? events = null)
        {
            Current = history;
            _behavior = behavior;
            _failRecovery = failRecovery;
            _blockMutationStarted = blockMutationStarted;
            _events = events;
        }

        public FileDeleteOperationActionHistory Current { get; private set; }

        public int MarkMutationStartedCount { get; private set; }

        public int MarkRecoveryRequiredCount { get; private set; }

        public bool LastMutationStartedTokenCanBeCanceled { get; private set; }

        public bool LastRecoveryTokenCanBeCanceled { get; private set; }

        public TaskCompletionSource<bool> MutationStartedEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseMutationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<FileDeleteOperationActionHistory> BeginAsync(
            FileDeleteOperationUserAuthorizationReceipt authorization,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async ValueTask<FileDeleteOperationActionHistory> MarkMutationStartedAsync(
            Guid operationId,
            int ordinal,
            CancellationToken cancellationToken = default)
        {
            MarkMutationStartedCount++;
            LastMutationStartedTokenCanBeCanceled = cancellationToken.CanBeCanceled;
            _events?.Add("mark-started");
            MutationStartedEntered.TrySetResult(true);
            if (_blockMutationStarted)
            {
                await ReleaseMutationStarted.Task.ConfigureAwait(false);
            }

            if (_behavior == MutationStartedBehavior.ThrowBeforePersist)
            {
                throw new InvalidOperationException("synthetic barrier failure before persistence");
            }

            Current = WithSelectedState(Current, FileDeleteOperationActionEntryState.MutationStarted);
            if (_behavior == MutationStartedBehavior.ThrowAfterPersist)
            {
                throw new InvalidOperationException("synthetic barrier failure after persistence");
            }

            if (_behavior == MutationStartedBehavior.ReturnCorruptHistory)
            {
                return WithSelectedState(Current, FileDeleteOperationActionEntryState.MutationStarted, corruptSourcePane: true);
            }

            return Current;
        }

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
            CancellationToken cancellationToken = default)
        {
            MarkRecoveryRequiredCount++;
            LastRecoveryTokenCanBeCanceled = cancellationToken.CanBeCanceled;
            _events?.Add("mark-recovery");
            if (_failRecovery)
            {
                throw new InvalidOperationException("synthetic recovery persistence failure");
            }
            if (Current.Entries[ordinal].State != FileDeleteOperationActionEntryState.MutationStarted)
            {
                throw new InvalidOperationException("recovery requires MutationStarted");
            }

            Current = WithSelectedState(
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
            _events?.Add("get-history");
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
        private readonly int _disposeFailures;
        private readonly List<string>? _events;

        public FakeFinalProvider(int disposeFailures, List<string>? events)
        {
            _disposeFailures = disposeFailures;
            _events = events;
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
            LastLease = new FakeFinalLease(evidence, _disposeFailures, _events);
            return new ValueTask<IFileDeleteOperationFinalMutationLease>(LastLease);
        }
    }

    private sealed class FakeFinalLease : IFileDeleteOperationFinalMutationLease
    {
        private int _remainingDisposeFailures;
        private readonly List<string>? _events;
        private bool _held = true;

        public FakeFinalLease(
            FileDeleteOperationFinalMutationLeaseEvidence evidence,
            int disposeFailures,
            List<string>? events)
        {
            Evidence = evidence;
            _remainingDisposeFailures = disposeFailures;
            _events = events;
        }

        public FileDeleteOperationFinalMutationLeaseEvidence Evidence { get; }

        public bool DeleteAccessCapabilityHeld => _held;

        public bool DeleteMutationAuthorized => false;

        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _events?.Add("final-dispose");
            if (_remainingDisposeFailures > 0)
            {
                _remainingDisposeFailures--;
                throw new InvalidOperationException("synthetic final disposal failure");
            }

            _held = false;
            return ValueTask.CompletedTask;
        }
    }
}
