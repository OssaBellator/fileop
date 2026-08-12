using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileDeleteOperationPreMutationPreparationTests
{
    [TestMethod]
    public async Task PrepareAcquiresThenReadsHistoryAndHoldsLeaseUntilScopeDisposal()
    {
        var events = new List<string>();
        var authorization = CreateAuthorization(entryCount: 1);
        var lease = CreateLease(authorization, ordinal: 0);
        var provider = new FakeStabilityLeaseProvider(lease, events);
        var history = CreateHistory(authorization);
        await using var store = new FakeHistoryStore(history, events);

        var scope = await FileDeleteOperationPreMutationPreparation.PrepareAsync(
            authorization,
            0,
            provider,
            store);

        CollectionAssert.AreEqual(new[] { "acquire", "get" }, events);
        Assert.AreSame(authorization, scope.Authorization);
        Assert.AreSame(lease.Evidence, scope.StabilityEvidence);
        Assert.AreSame(history, scope.HistorySnapshot);
        Assert.AreEqual(0, scope.Ordinal);
        Assert.IsFalse(scope.DeleteMutationAuthorized);
        Assert.IsFalse(scope.MutationBarrierSatisfied);
        Assert.IsTrue(scope.StabilityLeaseProviderAcquisitionObserved);
        Assert.IsTrue(scope.HistoryStoreReadObserved);
        Assert.IsTrue(scope.StabilityLeaseHeld);
        Assert.IsFalse(scope.IsDisposed);
        Assert.AreEqual(0, lease.DisposeCount);
        Assert.AreEqual(0, store.MutationCallCount);

        await scope.DisposeAsync();
        await scope.DisposeAsync();

        Assert.AreEqual(1, lease.DisposeCount);
        Assert.IsFalse(scope.StabilityLeaseHeld);
        Assert.IsTrue(scope.IsDisposed);
        Assert.AreEqual(0, store.MutationCallCount);
    }

    [TestMethod]
    public async Task ProviderEvidenceFromDifferentAuthorizationFailsBeforeHistoryReadAndReleasesLease()
    {
        var events = new List<string>();
        var authorization = CreateAuthorization(entryCount: 1);
        var otherAuthorization = CreateAuthorization(entryCount: 1);
        var lease = CreateLease(otherAuthorization, ordinal: 0);
        var provider = new FakeStabilityLeaseProvider(lease, events);
        await using var store = new FakeHistoryStore(CreateHistory(authorization), events);

        await AssertThrowsAsync<InvalidOperationException>(async () =>
            await FileDeleteOperationPreMutationPreparation.PrepareAsync(
                authorization,
                0,
                provider,
                store));

        CollectionAssert.AreEqual(new[] { "acquire" }, events);
        Assert.AreEqual(1, lease.DisposeCount);
        Assert.AreEqual(0, store.MutationCallCount);
    }

    [TestMethod]
    public async Task MutationAuthorizingProviderLeaseFailsBeforeHistoryReadAndReleasesLease()
    {
        var events = new List<string>();
        var authorization = CreateAuthorization(entryCount: 1);
        var lease = CreateLease(authorization, ordinal: 0, deleteMutationAuthorized: true);
        var provider = new FakeStabilityLeaseProvider(lease, events);
        await using var store = new FakeHistoryStore(CreateHistory(authorization), events);

        await AssertThrowsAsync<InvalidOperationException>(async () =>
            await FileDeleteOperationPreMutationPreparation.PrepareAsync(
                authorization,
                0,
                provider,
                store));

        CollectionAssert.AreEqual(new[] { "acquire" }, events);
        Assert.AreEqual(1, lease.DisposeCount);
        Assert.AreEqual(0, store.MutationCallCount);
    }

    [TestMethod]
    public async Task MissingHistoryFailsAfterAcquisitionAndReleasesLeaseWithoutCrossingBarrier()
    {
        var events = new List<string>();
        var authorization = CreateAuthorization(entryCount: 1);
        var lease = CreateLease(authorization, ordinal: 0);
        var provider = new FakeStabilityLeaseProvider(lease, events);
        await using var store = new FakeHistoryStore(history: null, events);

        await AssertThrowsAsync<InvalidOperationException>(async () =>
            await FileDeleteOperationPreMutationPreparation.PrepareAsync(
                authorization,
                0,
                provider,
                store));

        CollectionAssert.AreEqual(new[] { "acquire", "get" }, events);
        Assert.AreEqual(1, lease.DisposeCount);
        Assert.AreEqual(0, store.MutationCallCount);
    }

    [TestMethod]
    public async Task NonPendingOrTerminalHistoryFailsClosedAndReleasesLease()
    {
        var authorization = CreateAuthorization(entryCount: 1);

        var nonPendingLease = CreateLease(authorization, ordinal: 0);
        var nonPendingProvider = new FakeStabilityLeaseProvider(nonPendingLease);
        await using (var nonPendingStore = new FakeHistoryStore(
            CreateHistory(authorization, FileDeleteOperationActionEntryState.MutationStarted)))
        {
            await AssertThrowsAsync<ArgumentException>(async () =>
                await FileDeleteOperationPreMutationPreparation.PrepareAsync(
                    authorization,
                    0,
                    nonPendingProvider,
                    nonPendingStore));
            Assert.AreEqual(1, nonPendingLease.DisposeCount);
            Assert.AreEqual(0, nonPendingStore.MutationCallCount);
        }

        var terminalLease = CreateLease(authorization, ordinal: 0);
        var terminalProvider = new FakeStabilityLeaseProvider(terminalLease);
        await using var terminalStore = new FakeHistoryStore(
            CreateHistory(
                authorization,
                FileDeleteOperationActionEntryState.Failed,
                FileDeleteOperationActionTerminalState.Failed));
        await AssertThrowsAsync<ArgumentException>(async () =>
            await FileDeleteOperationPreMutationPreparation.PrepareAsync(
                authorization,
                0,
                terminalProvider,
                terminalStore));
        Assert.AreEqual(1, terminalLease.DisposeCount);
        Assert.AreEqual(0, terminalStore.MutationCallCount);
    }

    [TestMethod]
    public async Task HistoryReadFailureReleasesLeaseAndPreservesPrimaryFailure()
    {
        var authorization = CreateAuthorization(entryCount: 1);
        var lease = CreateLease(authorization, ordinal: 0);
        var provider = new FakeStabilityLeaseProvider(lease);
        var expected = new InvalidOperationException("history read failed");
        await using var store = new FakeHistoryStore(CreateHistory(authorization), getFailure: expected);

        try
        {
            await FileDeleteOperationPreMutationPreparation.PrepareAsync(
                authorization,
                0,
                provider,
                store);
            Assert.Fail("Expected history read failure.");
        }
        catch (InvalidOperationException exception)
        {
            Assert.AreSame(expected, exception);
        }

        Assert.AreEqual(1, lease.DisposeCount);
        Assert.AreEqual(0, store.MutationCallCount);
    }

    [TestMethod]
    public async Task PreCancelledPreparationDoesNotAcquireOrReadHistory()
    {
        var authorization = CreateAuthorization(entryCount: 1);
        var lease = CreateLease(authorization, ordinal: 0);
        var provider = new FakeStabilityLeaseProvider(lease);
        await using var store = new FakeHistoryStore(CreateHistory(authorization));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await AssertThrowsAsync<OperationCanceledException>(async () =>
            await FileDeleteOperationPreMutationPreparation.PrepareAsync(
                authorization,
                0,
                provider,
                store,
                cancellation.Token));

        Assert.AreEqual(0, provider.AcquireCount);
        Assert.AreEqual(0, store.GetCount);
        Assert.AreEqual(0, lease.DisposeCount);
        Assert.AreEqual(0, store.MutationCallCount);
    }

    private static FakeStabilityLease CreateLease(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        int ordinal,
        bool deleteMutationAuthorized = false)
    {
        var request = new FileDeleteOperationStabilityLeaseRequest(authorization, ordinal);
        var item = authorization.Items[ordinal];
        var evidence = new FileDeleteOperationStabilityLeaseEvidence(
            request,
            authorization.CanonicalSourceDirectoryPath,
            authorization.SourceDirectoryIdentity,
            item.CanonicalPath,
            item.Identity);
        return new FakeStabilityLease(evidence, deleteMutationAuthorized);
    }

    private static FileDeleteOperationActionHistory CreateHistory(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        FileDeleteOperationActionEntryState state = FileDeleteOperationActionEntryState.Pending,
        FileDeleteOperationActionTerminalState? terminalState = null)
    {
        var item = authorization.Items[0];
        var mutationStartedAt = state is FileDeleteOperationActionEntryState.MutationStarted or
            FileDeleteOperationActionEntryState.Committed or
            FileDeleteOperationActionEntryState.RecoveryRequired
            ? authorization.AuthorizedAtUtc.AddSeconds(1)
            : (DateTimeOffset?)null;
        var completedAt = state is FileDeleteOperationActionEntryState.Committed or
            FileDeleteOperationActionEntryState.Failed or
            FileDeleteOperationActionEntryState.RecoveryRequired
            ? authorization.AuthorizedAtUtc.AddSeconds(2)
            : (DateTimeOffset?)null;
        var failure = state is FileDeleteOperationActionEntryState.Failed or
            FileDeleteOperationActionEntryState.RecoveryRequired
            ? new FileOperationFailure("test", "test failure", item.CanonicalPath, Retryable: false)
            : null;
        var operationCompletedAt = terminalState.HasValue
            ? authorization.AuthorizedAtUtc.AddSeconds(3)
            : (DateTimeOffset?)null;

        return new FileDeleteOperationActionHistory(
            authorization.PlanId,
            authorization.AuthorizationId,
            authorization.Plan.QueuedAtUtc,
            authorization.ValidatedAtUtc,
            authorization.AuthorizedAtUtc,
            authorization.AuthorizedAtUtc.AddMilliseconds(1),
            operationCompletedAt,
            authorization.Plan.Intent.SourcePane,
            authorization.Plan.Intent.SourceTabId,
            authorization.CanonicalSourceDirectoryPath,
            authorization.SourceDirectoryIdentity,
            terminalState,
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

    private static FileDeleteOperationUserAuthorizationReceipt CreateAuthorization(int entryCount)
    {
        var entries = new FileOperationEntry[entryCount];
        var validationItems = new FileDeleteOperationExecutionValidationItem[entryCount];
        for (var ordinal = 0; ordinal < entryCount; ordinal++)
        {
            var path = $@"C:\Users\Alice\Temp\prepare-{ordinal}.tmp";
            var entry = new FileOperationEntry(path, $"prepare-{ordinal}.tmp", IsDirectory: false);
            entries[ordinal] = entry;
            validationItems[ordinal] = new FileDeleteOperationExecutionValidationItem(
                entry,
                new FileOperationCanonicalPath(
                    path,
                    path,
                    FileOperationCanonicalPathState.File,
                    IsLeafReparsePoint: false,
                    new FileIdentity(9, checked((ulong)(901 + ordinal)))),
                FileDeleteOperationExecutionValidationDecision.ReadyForAuthorizationReview,
                "ready");
        }

        var intent = new FileDeleteOperationIntent(
            "left",
            Guid.NewGuid(),
            @"C:\Users\Alice\Temp",
            entries);
        var plan = new FileDeleteOperationPlan(Guid.NewGuid(), DateTimeOffset.UtcNow, intent);
        var validation = new FileDeleteOperationExecutionValidationResult(
            plan,
            new FileOperationCanonicalPath(
                intent.SourceDirectoryPath,
                intent.SourceDirectoryPath,
                FileOperationCanonicalPathState.Directory,
                IsLeafReparsePoint: false,
                new FileIdentity(9, 900)),
            validationItems,
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

    private sealed class FakeStabilityLeaseProvider : IFileDeleteOperationStabilityLeaseProvider
    {
        private readonly IFileDeleteOperationStabilityLease _lease;
        private readonly List<string>? _events;

        public FakeStabilityLeaseProvider(
            IFileDeleteOperationStabilityLease lease,
            List<string>? events = null)
        {
            _lease = lease;
            _events = events;
        }

        public int AcquireCount { get; private set; }

        public FileDeleteOperationStabilityLeaseRequest? LastRequest { get; private set; }

        public ValueTask<IFileDeleteOperationStabilityLease> AcquireAsync(
            FileDeleteOperationStabilityLeaseRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcquireCount++;
            LastRequest = request;
            _events?.Add("acquire");
            return new ValueTask<IFileDeleteOperationStabilityLease>(_lease);
        }
    }

    private sealed class FakeStabilityLease : IFileDeleteOperationStabilityLease
    {
        public FakeStabilityLease(
            FileDeleteOperationStabilityLeaseEvidence evidence,
            bool deleteMutationAuthorized)
        {
            Evidence = evidence;
            DeleteMutationAuthorized = deleteMutationAuthorized;
        }

        public FileDeleteOperationStabilityLeaseEvidence Evidence { get; }

        public bool DeleteMutationAuthorized { get; }

        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeHistoryStore : IFileDeleteOperationActionHistoryStore
    {
        private readonly FileDeleteOperationActionHistory? _history;
        private readonly List<string>? _events;
        private readonly Exception? _getFailure;

        public FakeHistoryStore(
            FileDeleteOperationActionHistory? history,
            List<string>? events = null,
            Exception? getFailure = null)
        {
            _history = history;
            _events = events;
            _getFailure = getFailure;
        }

        public int GetCount { get; private set; }

        public int MutationCallCount { get; private set; }

        public ValueTask<FileDeleteOperationActionHistory?> GetAsync(
            Guid operationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCount++;
            _events?.Add("get");
            if (_getFailure is not null)
            {
                return new ValueTask<FileDeleteOperationActionHistory?>(
                    Task.FromException<FileDeleteOperationActionHistory?>(_getFailure));
            }

            return new ValueTask<FileDeleteOperationActionHistory?>(_history);
        }

        public ValueTask<FileDeleteOperationActionHistory> BeginAsync(
            FileDeleteOperationUserAuthorizationReceipt authorization,
            CancellationToken cancellationToken = default) =>
            UnexpectedMutationCall();

        public ValueTask<FileDeleteOperationActionHistory> MarkMutationStartedAsync(
            Guid operationId,
            int ordinal,
            CancellationToken cancellationToken = default) =>
            UnexpectedMutationCall();

        public ValueTask<FileDeleteOperationActionHistory> CommitDeletedAsync(
            Guid operationId,
            int ordinal,
            FileIdentity deletedSourceIdentity,
            CancellationToken cancellationToken = default) =>
            UnexpectedMutationCall();

        public ValueTask<FileDeleteOperationActionHistory> MarkFailedAsync(
            Guid operationId,
            int ordinal,
            FileOperationFailure failure,
            CancellationToken cancellationToken = default) =>
            UnexpectedMutationCall();

        public ValueTask<FileDeleteOperationActionHistory> MarkMutationRecoveryRequiredAsync(
            Guid operationId,
            int ordinal,
            FileOperationFailure failure,
            CancellationToken cancellationToken = default) =>
            UnexpectedMutationCall();

        public ValueTask<FileDeleteOperationActionHistory> CompleteAsync(
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            UnexpectedMutationCall();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private ValueTask<FileDeleteOperationActionHistory> UnexpectedMutationCall()
        {
            MutationCallCount++;
            throw new InvalidOperationException(
                "Pre-mutation preparation must not mutate delete action history.");
        }
    }
}
