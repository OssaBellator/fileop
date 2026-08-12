using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileDeleteOperationFinalMutationLeaseContractTests
{
    [TestMethod]
    public async Task FinalProviderRunsOnlyAfterReadOnlyPreparationLeaseIsReleased()
    {
        var events = new List<string>();
        var authorization = CreateAuthorization();
        var readOnlyLease = CreateReadOnlyLease(authorization, events);
        var readOnlyProvider = new FakeReadOnlyProvider(readOnlyLease, events);
        await using var historyStore = new FakeHistoryStore(CreateHistory(authorization));
        var preparation = await FileDeleteOperationPreMutationPreparation.PrepareAsync(
            authorization,
            0,
            readOnlyProvider,
            historyStore);
        var finalProvider = new FakeFinalProvider(events: events);

        var finalScope = await FileDeleteOperationFinalMutationLeasePreparation.AcquireAsync(
            preparation,
            finalProvider);
        var finalLease = finalProvider.LastLease!;

        CollectionAssert.AreEqual(
            new[] { "read-only-acquire", "read-only-dispose", "final-acquire" },
            events);
        Assert.IsFalse(preparation.StabilityLeaseHeld);
        Assert.IsTrue(preparation.IsDisposed);
        Assert.AreEqual(1, finalProvider.AcquireCount);
        Assert.AreSame(authorization, finalProvider.LastRequest!.Authorization);
        Assert.AreEqual(0, finalProvider.LastRequest.Ordinal);
        Assert.AreSame(authorization, finalScope.Authorization);
        Assert.AreSame(finalLease.Evidence, finalScope.FinalEvidence);
        Assert.IsTrue(finalScope.PriorReadOnlyLeaseReleaseObserved);
        Assert.IsTrue(finalScope.FinalLeaseProviderAcquisitionObserved);
        Assert.IsTrue(finalScope.FinalLeaseHeld);
        Assert.IsTrue(finalScope.DeleteAccessCapabilityHeld);
        Assert.IsFalse(finalScope.DeleteMutationAuthorized);
        Assert.IsFalse(finalScope.MutationBarrierSatisfied);
        Assert.IsFalse(finalScope.DeleteMutationPerformed);
        Assert.IsFalse(finalScope.FinalEvidence.ProviderAcquisitionProven);
        Assert.IsFalse(finalScope.FinalEvidence.DeleteAccessCapabilityProven);
        Assert.IsFalse(finalScope.FinalEvidence.LeaseLivenessProven);

        await finalScope.DisposeAsync();
        Assert.AreEqual(1, finalLease.DisposeCount);
        Assert.IsFalse(finalScope.FinalLeaseHeld);
        Assert.IsFalse(finalScope.DeleteAccessCapabilityHeld);
    }

    [TestMethod]
    public async Task ReadOnlyDisposalFailurePreventsFinalProviderAcquisition()
    {
        var events = new List<string>();
        var authorization = CreateAuthorization();
        var readOnlyLease = CreateReadOnlyLease(authorization, events, disposeFailures: 1);
        var readOnlyProvider = new FakeReadOnlyProvider(readOnlyLease, events);
        await using var historyStore = new FakeHistoryStore(CreateHistory(authorization));
        var preparation = await FileDeleteOperationPreMutationPreparation.PrepareAsync(
            authorization,
            0,
            readOnlyProvider,
            historyStore);
        var finalProvider = new FakeFinalProvider(events: events);

        await AssertThrowsAsync<InvalidOperationException>(async () =>
            await FileDeleteOperationFinalMutationLeasePreparation.AcquireAsync(
                preparation,
                finalProvider));

        CollectionAssert.AreEqual(
            new[] { "read-only-acquire", "read-only-dispose" },
            events);
        Assert.AreEqual(0, finalProvider.AcquireCount);
        Assert.IsTrue(preparation.StabilityLeaseHeld);
        Assert.IsFalse(preparation.IsDisposed);
    }

    [TestMethod]
    public async Task PreCancellationLeavesReadOnlyPreparationHeldAndDoesNotInvokeFinalProvider()
    {
        var authorization = CreateAuthorization();
        var readOnlyLease = CreateReadOnlyLease(authorization);
        var readOnlyProvider = new FakeReadOnlyProvider(readOnlyLease);
        await using var historyStore = new FakeHistoryStore(CreateHistory(authorization));
        var preparation = await FileDeleteOperationPreMutationPreparation.PrepareAsync(
            authorization,
            0,
            readOnlyProvider,
            historyStore);
        var finalProvider = new FakeFinalProvider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await AssertThrowsAsync<OperationCanceledException>(async () =>
            await FileDeleteOperationFinalMutationLeasePreparation.AcquireAsync(
                preparation,
                finalProvider,
                cancellation.Token));

        Assert.IsTrue(preparation.StabilityLeaseHeld);
        Assert.AreEqual(0, readOnlyLease.DisposeCount);
        Assert.AreEqual(0, finalProvider.AcquireCount);
        await preparation.DisposeAsync();
    }

    [TestMethod]
    public async Task ReplayedFinalLeaseFromDifferentAuthorizationFailsAndIsReleased()
    {
        var firstAuthorization = CreateAuthorization();
        var firstPreparation = await CreatePreparationAsync(firstAuthorization);
        var firstProvider = new FakeFinalProvider();
        var firstScope = await FileDeleteOperationFinalMutationLeasePreparation.AcquireAsync(
            firstPreparation,
            firstProvider);
        var staleLease = firstProvider.LastLease!;

        var secondAuthorization = CreateAuthorization();
        var secondPreparation = await CreatePreparationAsync(secondAuthorization);
        var replayProvider = new FakeFinalProvider(fixedLease: staleLease);

        await AssertThrowsAsync<InvalidOperationException>(async () =>
            await FileDeleteOperationFinalMutationLeasePreparation.AcquireAsync(
                secondPreparation,
                replayProvider));

        Assert.AreEqual(1, replayProvider.AcquireCount);
        Assert.AreEqual(1, staleLease.DisposeCount);
        Assert.IsFalse(staleLease.DeleteAccessCapabilityHeld);

        await firstScope.DisposeAsync();
        Assert.AreEqual(2, staleLease.DisposeCount);
    }

    [TestMethod]
    public async Task FinalProviderMustHoldCapabilityWithoutClaimingMutationAuthorization()
    {
        var authorization = CreateAuthorization();

        var noCapabilityPreparation = await CreatePreparationAsync(authorization);
        var noCapabilityProvider = new FakeFinalProvider(deleteAccessCapabilityHeld: false);
        await AssertThrowsAsync<InvalidOperationException>(async () =>
            await FileDeleteOperationFinalMutationLeasePreparation.AcquireAsync(
                noCapabilityPreparation,
                noCapabilityProvider));
        Assert.AreEqual(1, noCapabilityProvider.LastLease!.DisposeCount);

        var authorizingPreparation = await CreatePreparationAsync(authorization);
        var authorizingProvider = new FakeFinalProvider(deleteMutationAuthorized: true);
        await AssertThrowsAsync<InvalidOperationException>(async () =>
            await FileDeleteOperationFinalMutationLeasePreparation.AcquireAsync(
                authorizingPreparation,
                authorizingProvider));
        Assert.AreEqual(1, authorizingProvider.LastLease!.DisposeCount);
    }

    [TestMethod]
    public async Task FinalScopeDisposalFailureRetainsCapabilityOwnershipAndAllowsRetry()
    {
        var authorization = CreateAuthorization();
        var preparation = await CreatePreparationAsync(authorization);
        var finalProvider = new FakeFinalProvider(disposeFailures: 1);
        var scope = await FileDeleteOperationFinalMutationLeasePreparation.AcquireAsync(
            preparation,
            finalProvider);
        var finalLease = finalProvider.LastLease!;

        await AssertThrowsAsync<InvalidOperationException>(async () =>
            await scope.DisposeAsync());

        Assert.AreEqual(1, finalLease.DisposeCount);
        Assert.IsTrue(scope.FinalLeaseHeld);
        Assert.IsTrue(scope.DeleteAccessCapabilityHeld);
        Assert.IsFalse(scope.DeleteMutationAuthorized);
        Assert.IsFalse(scope.MutationBarrierSatisfied);

        await scope.DisposeAsync();
        await scope.DisposeAsync();

        Assert.AreEqual(2, finalLease.DisposeCount);
        Assert.IsFalse(scope.FinalLeaseHeld);
        Assert.IsFalse(scope.DeleteAccessCapabilityHeld);
    }

    [TestMethod]
    public async Task ValueEvidenceCannotExposeReusableRequestOrClaimCapabilityProof()
    {
        Assert.AreEqual(
            0,
            typeof(FileDeleteOperationFinalMutationLeaseRequest)
                .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .Length);
        Assert.IsNull(typeof(FileDeleteOperationFinalMutationLeaseEvidence).GetProperty("Request"));

        var authorization = CreateAuthorization();
        var preparation = await CreatePreparationAsync(authorization);
        var provider = new FakeFinalProvider();
        var scope = await FileDeleteOperationFinalMutationLeasePreparation.AcquireAsync(
            preparation,
            provider);
        var evidence = scope.FinalEvidence;

        Assert.AreSame(authorization, evidence.Authorization);
        Assert.IsTrue(evidence.IsBoundTo(authorization, 0));
        Assert.IsFalse(evidence.DeleteMutationAuthorized);
        Assert.IsFalse(evidence.ProviderAcquisitionProven);
        Assert.IsFalse(evidence.DeleteAccessCapabilityProven);
        Assert.IsFalse(evidence.LeaseLivenessProven);
        Assert.IsFalse(provider.LastRequest!.PriorReadOnlyLeaseReleaseProven);

        await scope.DisposeAsync();
    }

    private static async Task<FileDeleteOperationPreMutationPreparationScope> CreatePreparationAsync(
        FileDeleteOperationUserAuthorizationReceipt authorization)
    {
        var provider = new FakeReadOnlyProvider(CreateReadOnlyLease(authorization));
        var store = new FakeHistoryStore(CreateHistory(authorization));
        try
        {
            return await FileDeleteOperationPreMutationPreparation.PrepareAsync(
                authorization,
                0,
                provider,
                store);
        }
        finally
        {
            await store.DisposeAsync();
        }
    }

    private static FakeReadOnlyLease CreateReadOnlyLease(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        List<string>? events = null,
        int disposeFailures = 0)
    {
        var request = new FileDeleteOperationStabilityLeaseRequest(authorization, 0);
        var item = authorization.Items[0];
        var evidence = new FileDeleteOperationStabilityLeaseEvidence(
            request,
            authorization.CanonicalSourceDirectoryPath,
            authorization.SourceDirectoryIdentity,
            item.CanonicalPath,
            item.Identity);
        return new FakeReadOnlyLease(evidence, events, disposeFailures);
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
        var path = $@"C:\Users\Alice\Temp\final-{Guid.NewGuid():N}.tmp";
        var entry = new FileOperationEntry(path, System.IO.Path.GetFileName(path), IsDirectory: false);
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
                new FileIdentity(33, 3300)),
            new[]
            {
                new FileDeleteOperationExecutionValidationItem(
                    entry,
                    new FileOperationCanonicalPath(
                        path,
                        path,
                        FileOperationCanonicalPathState.File,
                        IsLeafReparsePoint: false,
                        new FileIdentity(33, 3301)),
                    FileDeleteOperationExecutionValidationDecision.ReadyForAuthorizationReview,
                    "ready"),
            },
            FileDeleteOperationExecutionValidationStatus.ReadyForAuthorizationReview,
            DateTimeOffset.UtcNow,
            "ready");
        return new FileDeleteOperationUserAuthorizationIssuer()
            .IssueAfterExplicitUserConfirmation(validation);
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

    private sealed class FakeReadOnlyProvider : IFileDeleteOperationStabilityLeaseProvider
    {
        private readonly IFileDeleteOperationStabilityLease _lease;
        private readonly List<string>? _events;

        public FakeReadOnlyProvider(
            IFileDeleteOperationStabilityLease lease,
            List<string>? events = null)
        {
            _lease = lease;
            _events = events;
        }

        public ValueTask<IFileDeleteOperationStabilityLease> AcquireAsync(
            FileDeleteOperationStabilityLeaseRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events?.Add("read-only-acquire");
            return new ValueTask<IFileDeleteOperationStabilityLease>(_lease);
        }
    }

    private sealed class FakeReadOnlyLease : IFileDeleteOperationStabilityLease
    {
        private readonly List<string>? _events;
        private int _remainingDisposeFailures;

        public FakeReadOnlyLease(
            FileDeleteOperationStabilityLeaseEvidence evidence,
            List<string>? events,
            int disposeFailures)
        {
            Evidence = evidence;
            _events = events;
            _remainingDisposeFailures = disposeFailures;
        }

        public FileDeleteOperationStabilityLeaseEvidence Evidence { get; }

        public bool DeleteMutationAuthorized => false;

        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _events?.Add("read-only-dispose");
            if (_remainingDisposeFailures > 0)
            {
                _remainingDisposeFailures--;
                throw new InvalidOperationException("synthetic read-only disposal failure");
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeFinalProvider : IFileDeleteOperationFinalMutationLeaseProvider
    {
        private readonly List<string>? _events;
        private readonly bool _deleteAccessCapabilityHeld;
        private readonly bool _deleteMutationAuthorized;
        private readonly int _disposeFailures;
        private readonly FakeFinalLease? _fixedLease;

        public FakeFinalProvider(
            List<string>? events = null,
            bool deleteAccessCapabilityHeld = true,
            bool deleteMutationAuthorized = false,
            int disposeFailures = 0,
            FakeFinalLease? fixedLease = null)
        {
            _events = events;
            _deleteAccessCapabilityHeld = deleteAccessCapabilityHeld;
            _deleteMutationAuthorized = deleteMutationAuthorized;
            _disposeFailures = disposeFailures;
            _fixedLease = fixedLease;
        }

        public int AcquireCount { get; private set; }

        public FileDeleteOperationFinalMutationLeaseRequest? LastRequest { get; private set; }

        public FakeFinalLease? LastLease { get; private set; }

        public ValueTask<IFileDeleteOperationFinalMutationLease> AcquireAsync(
            FileDeleteOperationFinalMutationLeaseRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcquireCount++;
            LastRequest = request;
            _events?.Add("final-acquire");

            var lease = _fixedLease ?? CreateLease(request);
            LastLease = lease;
            return new ValueTask<IFileDeleteOperationFinalMutationLease>(lease);
        }

        private FakeFinalLease CreateLease(FileDeleteOperationFinalMutationLeaseRequest request)
        {
            var item = request.AuthorizedItem;
            var evidence = new FileDeleteOperationFinalMutationLeaseEvidence(
                request,
                request.Authorization.CanonicalSourceDirectoryPath,
                request.Authorization.SourceDirectoryIdentity,
                item.CanonicalPath,
                item.Identity);
            return new FakeFinalLease(
                evidence,
                _events,
                _deleteAccessCapabilityHeld,
                _deleteMutationAuthorized,
                _disposeFailures);
        }
    }

    private sealed class FakeFinalLease : IFileDeleteOperationFinalMutationLease
    {
        private readonly List<string>? _events;
        private readonly bool _deleteAccessCapabilityHeld;
        private int _remainingDisposeFailures;
        private bool _disposed;

        public FakeFinalLease(
            FileDeleteOperationFinalMutationLeaseEvidence evidence,
            List<string>? events,
            bool deleteAccessCapabilityHeld,
            bool deleteMutationAuthorized,
            int disposeFailures)
        {
            Evidence = evidence;
            _events = events;
            _deleteAccessCapabilityHeld = deleteAccessCapabilityHeld;
            DeleteMutationAuthorized = deleteMutationAuthorized;
            _remainingDisposeFailures = disposeFailures;
        }

        public FileDeleteOperationFinalMutationLeaseEvidence Evidence { get; }

        public bool DeleteAccessCapabilityHeld => !_disposed && _deleteAccessCapabilityHeld;

        public bool DeleteMutationAuthorized { get; }

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

            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeHistoryStore : IFileDeleteOperationActionHistoryStore
    {
        private readonly FileDeleteOperationActionHistory _history;

        public FakeHistoryStore(FileDeleteOperationActionHistory history)
        {
            _history = history;
        }

        public ValueTask<FileDeleteOperationActionHistory?> GetAsync(
            Guid operationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<FileDeleteOperationActionHistory?>(_history);
        }

        public ValueTask<FileDeleteOperationActionHistory> BeginAsync(
            FileDeleteOperationUserAuthorizationReceipt authorization,
            CancellationToken cancellationToken = default) => Unexpected();

        public ValueTask<FileDeleteOperationActionHistory> MarkMutationStartedAsync(
            Guid operationId,
            int ordinal,
            CancellationToken cancellationToken = default) => Unexpected();

        public ValueTask<FileDeleteOperationActionHistory> CommitDeletedAsync(
            Guid operationId,
            int ordinal,
            FileIdentity deletedSourceIdentity,
            CancellationToken cancellationToken = default) => Unexpected();

        public ValueTask<FileDeleteOperationActionHistory> MarkFailedAsync(
            Guid operationId,
            int ordinal,
            FileOperationFailure failure,
            CancellationToken cancellationToken = default) => Unexpected();

        public ValueTask<FileDeleteOperationActionHistory> MarkMutationRecoveryRequiredAsync(
            Guid operationId,
            int ordinal,
            FileOperationFailure failure,
            CancellationToken cancellationToken = default) => Unexpected();

        public ValueTask<FileDeleteOperationActionHistory> CompleteAsync(
            Guid operationId,
            CancellationToken cancellationToken = default) => Unexpected();

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static ValueTask<FileDeleteOperationActionHistory> Unexpected() =>
            new(Task.FromException<FileDeleteOperationActionHistory>(
                new AssertFailedException("Final-lease contract must not mutate delete history.")));
    }
}
