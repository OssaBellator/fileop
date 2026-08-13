using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileDeleteOperationOrchestratorTests
{
    [TestMethod]
    public async Task PendingEntriesMutateOnceInOrderAndCompleteSucceeded()
    {
        var authorization = CreateAuthorization(3);
        await using var historyStore = new FakeHistoryStore(CreateHistory(authorization));
        var readOnlyProvider = new FakeReadOnlyProvider();
        var finalProvider = new FakeFinalProvider(authorization.Items.Count);

        var result = await FileDeleteOperationOrchestrator.ExecuteAsync(
            authorization,
            readOnlyProvider,
            finalProvider,
            historyStore);

        Assert.AreEqual(3, result.MutatedEntryCount);
        Assert.AreEqual(0, result.PreviouslyTerminalEntryCount);
        Assert.IsFalse(result.CompletionObservedFromExistingHistory);
        Assert.IsFalse(result.DeleteMutationAuthorized);
        Assert.AreEqual(FileDeleteOperationActionTerminalState.Succeeded, result.CompletedHistory.TerminalState);
        Assert.IsTrue(result.CompletedHistory.Entries.All(static entry =>
            entry.State == FileDeleteOperationActionEntryState.Committed));
        Assert.AreEqual(1, historyStore.CompleteCount);
        Assert.IsFalse(historyStore.LastCompleteTokenCanBeCanceled);
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, finalProvider.AcquiredOrdinals.ToArray());
        CollectionAssert.AreEqual(new[] { 1, 1, 1 }, finalProvider.MutationCounts);
        Assert.IsTrue(finalProvider.MutationTokenCanBeCanceled.All(static value => !value));
    }

    [TestMethod]
    public async Task ExistingCommittedAndFailedEntriesAreNeverReplayed()
    {
        var authorization = CreateAuthorization(3);
        var history = CreateHistory(authorization);
        history = WithEntryState(history, 0, FileDeleteOperationActionEntryState.Committed);
        history = WithEntryState(
            history,
            2,
            FileDeleteOperationActionEntryState.Failed,
            new FileOperationFailure(
                "SyntheticSafeFailure",
                "Synthetic pre-mutation failure.",
                authorization.Items[2].CanonicalPath,
                Retryable: false));
        await using var historyStore = new FakeHistoryStore(history);
        var readOnlyProvider = new FakeReadOnlyProvider();
        var finalProvider = new FakeFinalProvider(authorization.Items.Count);

        var result = await FileDeleteOperationOrchestrator.ExecuteAsync(
            authorization,
            readOnlyProvider,
            finalProvider,
            historyStore);

        Assert.AreEqual(1, result.MutatedEntryCount);
        Assert.AreEqual(2, result.PreviouslyTerminalEntryCount);
        Assert.AreEqual(FileDeleteOperationActionTerminalState.Failed, result.CompletedHistory.TerminalState);
        CollectionAssert.AreEqual(new[] { 1 }, finalProvider.AcquiredOrdinals.ToArray());
        CollectionAssert.AreEqual(new[] { 0, 1, 0 }, finalProvider.MutationCounts);
        Assert.AreEqual(1, historyStore.CompleteCount);
    }

    [TestMethod]
    public async Task RecoverySensitiveHistoryStopsBeforeAnyNewProviderAcquisition()
    {
        var authorization = CreateAuthorization(3);
        var history = CreateHistory(authorization);
        history = WithEntryState(history, 0, FileDeleteOperationActionEntryState.Committed);
        history = WithEntryState(
            history,
            1,
            FileDeleteOperationActionEntryState.RecoveryRequired,
            new FileOperationFailure(
                "SyntheticRecovery",
                "Synthetic recovery-sensitive state.",
                authorization.Items[1].CanonicalPath,
                Retryable: false));
        await using var historyStore = new FakeHistoryStore(history);
        var readOnlyProvider = new FakeReadOnlyProvider();
        var finalProvider = new FakeFinalProvider(authorization.Items.Count);

        var exception = await CaptureThrowsAsync<FileDeleteOperationOrchestrationRecoveryRequiredException>(async () =>
            await FileDeleteOperationOrchestrator.ExecuteAsync(
                authorization,
                readOnlyProvider,
                finalProvider,
                historyStore));

        Assert.AreSame(historyStore.Current, exception.History);
        Assert.IsFalse(exception.DeleteMutationAuthorized);
        Assert.AreEqual(0, readOnlyProvider.AcquireCount);
        Assert.AreEqual(0, finalProvider.AcquiredOrdinals.Count);
        Assert.AreEqual(0, historyStore.CompleteCount);
        Assert.AreEqual(FileDeleteOperationActionEntryState.Pending, historyStore.Current.Entries[2].State);
    }

    [TestMethod]
    public async Task CancellationAfterFirstCommitStopsBeforeNextOrdinal()
    {
        var authorization = CreateAuthorization(2);
        await using var historyStore = new FakeHistoryStore(CreateHistory(authorization));
        var readOnlyProvider = new FakeReadOnlyProvider();
        using var cancellation = new CancellationTokenSource();
        var finalProvider = new FakeFinalProvider(
            authorization.Items.Count,
            afterMutation: ordinal =>
            {
                if (ordinal == 0)
                {
                    cancellation.Cancel();
                }
            });

        await CaptureThrowsAsync<OperationCanceledException>(async () =>
            await FileDeleteOperationOrchestrator.ExecuteAsync(
                authorization,
                readOnlyProvider,
                finalProvider,
                historyStore,
                cancellation.Token));

        CollectionAssert.AreEqual(new[] { 1, 0 }, finalProvider.MutationCounts);
        Assert.AreEqual(FileDeleteOperationActionEntryState.Committed, historyStore.Current.Entries[0].State);
        Assert.AreEqual(FileDeleteOperationActionEntryState.Pending, historyStore.Current.Entries[1].State);
        Assert.AreEqual(0, historyStore.CompleteCount);
    }

    [TestMethod]
    public async Task CompletionThrowAfterPersistenceIsInspectedWithoutMutationReplay()
    {
        var authorization = CreateAuthorization(2);
        await using var historyStore = new FakeHistoryStore(
            CreateHistory(authorization),
            completeBehavior: CompleteBehavior.ThrowAfterPersist);
        var finalProvider = new FakeFinalProvider(authorization.Items.Count);

        var result = await FileDeleteOperationOrchestrator.ExecuteAsync(
            authorization,
            new FakeReadOnlyProvider(),
            finalProvider,
            historyStore);

        Assert.AreEqual(FileDeleteOperationActionTerminalState.Succeeded, result.CompletedHistory.TerminalState);
        Assert.IsTrue(result.CompletionObservedFromExistingHistory);
        Assert.AreEqual(1, historyStore.CompleteCount);
        CollectionAssert.AreEqual(new[] { 1, 1 }, finalProvider.MutationCounts);
    }

    [TestMethod]
    public async Task PostMutationReleaseFailureStopsBeforeNextOrdinalAndRetainsCleanupOwner()
    {
        var authorization = CreateAuthorization(2);
        await using var historyStore = new FakeHistoryStore(CreateHistory(authorization));
        var finalProvider = new FakeFinalProvider(
            authorization.Items.Count,
            releaseFailureOrdinal: 0);

        var exception = await CaptureThrowsAsync<FileDeleteOperationFinalLeaseReleaseException>(async () =>
            await FileDeleteOperationOrchestrator.ExecuteAsync(
                authorization,
                new FakeReadOnlyProvider(),
                finalProvider,
                historyStore));

        Assert.IsTrue(exception.FinalLeaseReleasePending);
        Assert.IsFalse(exception.DeleteMutationAuthorized);
        CollectionAssert.AreEqual(new[] { 1, 0 }, finalProvider.MutationCounts);
        CollectionAssert.AreEqual(new[] { 0 }, finalProvider.AcquiredOrdinals.ToArray());
        Assert.AreEqual(FileDeleteOperationActionEntryState.RecoveryRequired, historyStore.Current.Entries[0].State);
        Assert.AreEqual(FileDeleteOperationActionEntryState.Pending, historyStore.Current.Entries[1].State);
        Assert.AreEqual(0, historyStore.CompleteCount);

        await exception.RetryFinalLeaseReleaseAsync();
        Assert.IsFalse(exception.FinalLeaseReleasePending);
    }

    private static FileDeleteOperationUserAuthorizationReceipt CreateAuthorization(int count)
    {
        var root = @"C:\Users\Alice\Temp";
        var entries = new FileOperationEntry[count];
        var validationItems = new FileDeleteOperationExecutionValidationItem[count];
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            var path = $@"{root}\orchestration-{ordinal}-{Guid.NewGuid():N}.tmp";
            var entry = new FileOperationEntry(path, Path.GetFileName(path), IsDirectory: false);
            entries[ordinal] = entry;
            validationItems[ordinal] = new FileDeleteOperationExecutionValidationItem(
                entry,
                new FileOperationCanonicalPath(
                    path,
                    path,
                    FileOperationCanonicalPathState.File,
                    IsLeafReparsePoint: false,
                    new FileIdentity(73, checked((ulong)(7301 + ordinal)))),
                FileDeleteOperationExecutionValidationDecision.ReadyForAuthorizationReview,
                "ready");
        }

        var intent = new FileDeleteOperationIntent(
            "left",
            Guid.NewGuid(),
            root,
            entries);
        var plan = new FileDeleteOperationPlan(Guid.NewGuid(), DateTimeOffset.UtcNow, intent);
        var validation = new FileDeleteOperationExecutionValidationResult(
            plan,
            new FileOperationCanonicalPath(
                root,
                root,
                FileOperationCanonicalPathState.Directory,
                IsLeafReparsePoint: false,
                new FileIdentity(73, 7300)),
            validationItems,
            FileDeleteOperationExecutionValidationStatus.ReadyForAuthorizationReview,
            DateTimeOffset.UtcNow,
            "ready");
        return new FileDeleteOperationUserAuthorizationIssuer()
            .IssueAfterExplicitUserConfirmation(validation);
    }

    private static FileDeleteOperationActionHistory CreateHistory(
        FileDeleteOperationUserAuthorizationReceipt authorization)
    {
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
            authorization.Items.Select((item, ordinal) =>
                new FileDeleteOperationActionEntry(
                    ordinal,
                    item.Entry,
                    item.CanonicalPath,
                    item.Identity,
                    FileDeleteOperationActionEntryState.Pending,
                    MutationStartedAtUtc: null,
                    CompletedAtUtc: null,
                    Failure: null)));
    }

    private static FileDeleteOperationActionHistory WithEntryState(
        FileDeleteOperationActionHistory history,
        int ordinal,
        FileDeleteOperationActionEntryState state,
        FileOperationFailure? failure = null)
    {
        var entries = history.Entries.ToArray();
        var previous = entries[ordinal];
        DateTimeOffset? mutationStarted = state is FileDeleteOperationActionEntryState.MutationStarted or
            FileDeleteOperationActionEntryState.Committed or
            FileDeleteOperationActionEntryState.RecoveryRequired
                ? previous.MutationStartedAtUtc ?? history.StartedAtUtc.AddMilliseconds(ordinal + 1)
                : null;
        DateTimeOffset? completed = state is FileDeleteOperationActionEntryState.Committed or
            FileDeleteOperationActionEntryState.Failed or
            FileDeleteOperationActionEntryState.RecoveryRequired
                ? (mutationStarted ?? history.StartedAtUtc).AddMilliseconds(10 + ordinal)
                : null;
        entries[ordinal] = new FileDeleteOperationActionEntry(
            previous.Ordinal,
            previous.Entry,
            previous.CanonicalSourcePath,
            previous.SourceIdentity,
            state,
            mutationStarted,
            completed,
            failure);
        return CopyHistory(history, entries, history.TerminalState, history.CompletedAtUtc);
    }

    private static FileDeleteOperationActionHistory CompleteHistory(
        FileDeleteOperationActionHistory history)
    {
        var terminalState = history.Entries.Any(static entry =>
                entry.State is FileDeleteOperationActionEntryState.MutationStarted or
                    FileDeleteOperationActionEntryState.RecoveryRequired)
            ? FileDeleteOperationActionTerminalState.RecoveryRequired
            : history.Entries.Any(static entry => entry.State == FileDeleteOperationActionEntryState.Failed)
                ? FileDeleteOperationActionTerminalState.Failed
                : history.Entries.All(static entry => entry.State == FileDeleteOperationActionEntryState.Committed)
                    ? FileDeleteOperationActionTerminalState.Succeeded
                    : throw new InvalidOperationException("cannot complete pending history");
        return CopyHistory(
            history,
            history.Entries,
            terminalState,
            history.StartedAtUtc.AddMinutes(1));
    }

    private static FileDeleteOperationActionHistory CopyHistory(
        FileDeleteOperationActionHistory history,
        IEnumerable<FileDeleteOperationActionEntry> entries,
        FileDeleteOperationActionTerminalState? terminalState,
        DateTimeOffset? completedAtUtc) =>
        new(
            history.OperationId,
            history.AuthorizationId,
            history.QueuedAtUtc,
            history.ValidatedAtUtc,
            history.AuthorizedAtUtc,
            history.StartedAtUtc,
            completedAtUtc,
            history.SourcePaneId,
            history.SourceTabId,
            history.CanonicalSourceDirectoryPath,
            history.SourceDirectoryIdentity,
            terminalState,
            entries);

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

    private enum CompleteBehavior
    {
        Success,
        ThrowBeforePersist,
        ThrowAfterPersist,
    }

    private sealed class FakeHistoryStore : IFileDeleteOperationActionHistoryStore
    {
        private readonly CompleteBehavior _completeBehavior;

        public FakeHistoryStore(
            FileDeleteOperationActionHistory history,
            CompleteBehavior completeBehavior = CompleteBehavior.Success)
        {
            Current = history;
            _completeBehavior = completeBehavior;
        }

        public FileDeleteOperationActionHistory Current { get; private set; }

        public int CompleteCount { get; private set; }

        public bool LastCompleteTokenCanBeCanceled { get; private set; }

        public ValueTask<FileDeleteOperationActionHistory> BeginAsync(
            FileDeleteOperationUserAuthorizationReceipt authorization,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<FileDeleteOperationActionHistory> MarkMutationStartedAsync(
            Guid operationId,
            int ordinal,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireOperation(operationId);
            if (Current.Entries[ordinal].State != FileDeleteOperationActionEntryState.Pending)
            {
                throw new InvalidOperationException("barrier requires Pending");
            }
            Current = WithEntryState(Current, ordinal, FileDeleteOperationActionEntryState.MutationStarted);
            return new ValueTask<FileDeleteOperationActionHistory>(Current);
        }

        public ValueTask<FileDeleteOperationActionHistory> CommitDeletedAsync(
            Guid operationId,
            int ordinal,
            FileIdentity deletedSourceIdentity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireOperation(operationId);
            if (Current.Entries[ordinal].State != FileDeleteOperationActionEntryState.MutationStarted ||
                Current.Entries[ordinal].SourceIdentity != deletedSourceIdentity)
            {
                throw new InvalidOperationException("commit requires exact MutationStarted identity");
            }
            Current = WithEntryState(Current, ordinal, FileDeleteOperationActionEntryState.Committed);
            return new ValueTask<FileDeleteOperationActionHistory>(Current);
        }

        public ValueTask<FileDeleteOperationActionHistory> MarkFailedAsync(
            Guid operationId,
            int ordinal,
            FileOperationFailure failure,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireOperation(operationId);
            if (Current.Entries[ordinal].State != FileDeleteOperationActionEntryState.Pending)
            {
                throw new InvalidOperationException("failure requires Pending");
            }
            Current = WithEntryState(Current, ordinal, FileDeleteOperationActionEntryState.Failed, failure);
            return new ValueTask<FileDeleteOperationActionHistory>(Current);
        }

        public ValueTask<FileDeleteOperationActionHistory> MarkMutationRecoveryRequiredAsync(
            Guid operationId,
            int ordinal,
            FileOperationFailure failure,
            CancellationToken cancellationToken = default)
        {
            RequireOperation(operationId);
            if (Current.Entries[ordinal].State != FileDeleteOperationActionEntryState.MutationStarted)
            {
                throw new InvalidOperationException("recovery requires MutationStarted");
            }
            Current = WithEntryState(Current, ordinal, FileDeleteOperationActionEntryState.RecoveryRequired, failure);
            return new ValueTask<FileDeleteOperationActionHistory>(Current);
        }

        public ValueTask<FileDeleteOperationActionHistory> CompleteAsync(
            Guid operationId,
            CancellationToken cancellationToken = default)
        {
            RequireOperation(operationId);
            CompleteCount++;
            LastCompleteTokenCanBeCanceled = cancellationToken.CanBeCanceled;
            if (_completeBehavior == CompleteBehavior.ThrowBeforePersist)
            {
                throw new InvalidOperationException("synthetic completion failure before persistence");
            }

            Current = CompleteHistory(Current);
            if (_completeBehavior == CompleteBehavior.ThrowAfterPersist)
            {
                throw new InvalidOperationException("synthetic completion failure after persistence");
            }
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

    private sealed class FakeReadOnlyProvider : IFileDeleteOperationStabilityLeaseProvider
    {
        public int AcquireCount { get; private set; }

        public ValueTask<IFileDeleteOperationStabilityLease> AcquireAsync(
            FileDeleteOperationStabilityLeaseRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcquireCount++;
            var item = request.AuthorizedItem;
            IFileDeleteOperationStabilityLease lease = new FakeReadOnlyLease(
                new FileDeleteOperationStabilityLeaseEvidence(
                    request,
                    request.Authorization.CanonicalSourceDirectoryPath,
                    request.Authorization.SourceDirectoryIdentity,
                    item.CanonicalPath,
                    item.Identity));
            return new ValueTask<IFileDeleteOperationStabilityLease>(lease);
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
        private readonly Action<int>? _afterMutation;
        private readonly int? _releaseFailureOrdinal;

        public FakeFinalProvider(
            int entryCount,
            Action<int>? afterMutation = null,
            int? releaseFailureOrdinal = null)
        {
            MutationCounts = new int[entryCount];
            MutationTokenCanBeCanceled = new bool[entryCount];
            _afterMutation = afterMutation;
            _releaseFailureOrdinal = releaseFailureOrdinal;
        }

        public List<int> AcquiredOrdinals { get; } = new();

        public int[] MutationCounts { get; }

        public bool[] MutationTokenCanBeCanceled { get; }

        public ValueTask<IFileDeleteOperationFinalMutationLease> AcquireAsync(
            FileDeleteOperationFinalMutationLeaseRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcquiredOrdinals.Add(request.Ordinal);
            var item = request.AuthorizedItem;
            var evidence = new FileDeleteOperationFinalMutationLeaseEvidence(
                request,
                request.Authorization.CanonicalSourceDirectoryPath,
                request.Authorization.SourceDirectoryIdentity,
                item.CanonicalPath,
                item.Identity);
            IFileDeleteOperationFinalMutationLease lease = new FakeFinalLease(
                evidence,
                request.Ordinal,
                this,
                _afterMutation,
                _releaseFailureOrdinal == request.Ordinal ? 1 : 0);
            return new ValueTask<IFileDeleteOperationFinalMutationLease>(lease);
        }
    }

    private sealed class FakeFinalLease :
        IFileDeleteOperationFinalMutationLease,
        IFileDeleteOperationSameLeaseMutation
    {
        private readonly int _ordinal;
        private readonly FakeFinalProvider _owner;
        private readonly Action<int>? _afterMutation;
        private int _remainingReleaseFailures;
        private bool _held = true;

        public FakeFinalLease(
            FileDeleteOperationFinalMutationLeaseEvidence evidence,
            int ordinal,
            FakeFinalProvider owner,
            Action<int>? afterMutation,
            int releaseFailures)
        {
            Evidence = evidence;
            _ordinal = ordinal;
            _owner = owner;
            _afterMutation = afterMutation;
            _remainingReleaseFailures = releaseFailures;
        }

        public FileDeleteOperationFinalMutationLeaseEvidence Evidence { get; }

        public bool DeleteAccessCapabilityHeld => _held;

        public bool DeleteMutationAuthorized => false;

        public ValueTask MarkDeletePendingAsync(
            FileDeleteOperationMutationAuthorization authorization,
            CancellationToken cancellationToken = default)
        {
            if (!authorization.IsBoundTo(Evidence))
            {
                throw new UnauthorizedAccessException("mutation authorization mismatch");
            }
            _owner.MutationCounts[_ordinal]++;
            _owner.MutationTokenCanBeCanceled[_ordinal] = cancellationToken.CanBeCanceled;
            if (_owner.MutationCounts[_ordinal] != 1)
            {
                throw new InvalidOperationException("mutation replay");
            }
            _afterMutation?.Invoke(_ordinal);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (_remainingReleaseFailures > 0)
            {
                _remainingReleaseFailures--;
                throw new InvalidOperationException("synthetic final lease release failure");
            }
            _held = false;
            return ValueTask.CompletedTask;
        }
    }
}
