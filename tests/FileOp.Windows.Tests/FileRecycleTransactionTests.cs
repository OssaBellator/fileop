using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileRecycleTransactionTests
{
    [TestMethod]
    public async Task SqliteMutationBarrierRejectsSkippedOrdinal()
    {
        using var fixture = new HistoryFixture();
        var (authorization, fresh) = CreateAuthorization(entryCount: 2);
        await using var store = fixture.CreateStore();
        await store.BeginAsync(authorization, fresh);

        await AssertThrowsAsync<InvalidOperationException>(async () =>
            await store.MarkMutationStartedAsync(authorization.PlanId, 1));

        var history = await store.GetAsync(authorization.PlanId);
        Assert.IsNotNull(history);
        Assert.AreEqual(FileRecycleActionEntryState.Pending, history.Entries[0].State);
        Assert.AreEqual(FileRecycleActionEntryState.Pending, history.Entries[1].State);
        Assert.IsFalse(history.IsRecoverySensitive);
    }

    [TestMethod]
    public async Task SqlitePersistsRecycledPrefixAndCompletesSucceeded()
    {
        using var fixture = new HistoryFixture();
        var (authorization, fresh) = CreateAuthorization(entryCount: 2);
        await using (var store = fixture.CreateStore())
        {
            await store.BeginAsync(authorization, fresh);
            await store.MarkMutationStartedAsync(authorization.PlanId, 0);
            await store.CommitRecycledAsync(
                authorization.PlanId,
                0,
                authorization.Review.Items[0].Identity,
                "test-provider",
                "opaque:0");
            await store.MarkMutationStartedAsync(authorization.PlanId, 1);
            await store.CommitRecycledAsync(
                authorization.PlanId,
                1,
                authorization.Review.Items[1].Identity,
                "test-provider",
                "opaque:1");
            var completed = await store.CompleteAsync(authorization.PlanId, FileRecycleActionTerminalState.Succeeded);

            Assert.AreEqual(FileRecycleActionTerminalState.Succeeded, completed.TerminalState);
            Assert.IsTrue(completed.Entries[0].RestoreAuthorized is false);
            Assert.IsFalse(completed.RestoreAuthorized);
        }

        await using var reopenedStore = fixture.CreateStore();
        var reopened = await reopenedStore.GetAsync(authorization.PlanId);
        Assert.IsNotNull(reopened);
        Assert.AreEqual(FileRecycleActionTerminalState.Succeeded, reopened.TerminalState);
        Assert.AreEqual(FileRecycleActionEntryState.Recycled, reopened.Entries[0].State);
        Assert.AreEqual(FileRecycleActionEntryState.Recycled, reopened.Entries[1].State);
        Assert.AreEqual("opaque:0", reopened.Entries[0].RecycleLocator);
        Assert.AreEqual("opaque:1", reopened.Entries[1].RecycleLocator);
    }

    [TestMethod]
    public async Task ExecutorObservesDurableBarrierBeforeProviderInvocation()
    {
        using var fixture = new HistoryFixture();
        var (authorization, fresh) = CreateAuthorization(entryCount: 1);
        await using var store = fixture.CreateStore();
        var provider = new BarrierInspectingProvider(store);
        var executor = new FileRecycleOperationExecutor(
            new FixedFreshValidator(fresh),
            store,
            provider);

        var result = await executor.ExecuteAsync(authorization);

        Assert.AreEqual(FileRecycleExecutionStatus.Succeeded, result.Status);
        Assert.IsTrue(provider.ObservedMutationBarrier);
        Assert.IsNotNull(result.History);
        Assert.AreEqual(FileRecycleActionTerminalState.Succeeded, result.History.TerminalState);
        Assert.AreEqual(provider.ProviderName, result.History.Entries[0].ProviderName);
    }

    [TestMethod]
    public async Task ExecutorCapturesProviderNameExactlyOnceAtComposition()
    {
        using var fixture = new HistoryFixture();
        var (authorization, fresh) = CreateAuthorization(entryCount: 1);
        await using var store = fixture.CreateStore();
        var provider = new ChangingNameProvider();
        var executor = new FileRecycleOperationExecutor(
            new FixedFreshValidator(fresh),
            store,
            provider);

        var result = await executor.ExecuteAsync(authorization);

        Assert.AreEqual(FileRecycleExecutionStatus.Succeeded, result.Status);
        Assert.AreEqual(1, provider.ProviderNameReadCount);
        Assert.AreEqual("captured-provider", result.History?.Entries[0].ProviderName);
    }

    [TestMethod]
    public async Task ExecutorRejectsForgedProviderNameAsRecoveryRequired()
    {
        using var fixture = new HistoryFixture();
        var (authorization, fresh) = CreateAuthorization(entryCount: 1);
        await using var store = fixture.CreateStore();
        var provider = new ForgedNameProvider();
        var executor = new FileRecycleOperationExecutor(
            new FixedFreshValidator(fresh),
            store,
            provider);

        var result = await executor.ExecuteAsync(authorization);

        Assert.AreEqual(FileRecycleExecutionStatus.RecoveryRequired, result.Status);
        Assert.IsNotNull(result.History);
        Assert.AreEqual(FileRecycleActionTerminalState.RecoveryRequired, result.History.TerminalState);
        Assert.AreEqual(FileRecycleActionEntryState.RecoveryRequired, result.History.Entries[0].State);
        Assert.AreEqual(provider.ProviderName, result.History.Entries[0].ProviderName);
        Assert.AreEqual("RecycleProviderNameMismatch", result.Failure?.Code);
        Assert.IsFalse(result.History.RestoreAuthorized);
    }

    [TestMethod]
    public async Task ProviderThrowAfterBarrierUsesBoundProviderNameAndRequiresRecovery()
    {
        using var fixture = new HistoryFixture();
        var (authorization, fresh) = CreateAuthorization(entryCount: 1);
        await using var store = fixture.CreateStore();
        var provider = new ThrowingProvider();
        var executor = new FileRecycleOperationExecutor(
            new FixedFreshValidator(fresh),
            store,
            provider);

        var result = await executor.ExecuteAsync(authorization);

        Assert.AreEqual(FileRecycleExecutionStatus.RecoveryRequired, result.Status);
        Assert.IsNotNull(result.History);
        Assert.AreEqual(FileRecycleActionEntryState.RecoveryRequired, result.History.Entries[0].State);
        Assert.AreEqual(provider.ProviderName, result.History.Entries[0].ProviderName);
        Assert.AreEqual("RecycleProviderThrewAfterMutationBarrier", result.Failure?.Code);
    }

    [TestMethod]
    public async Task NullProviderResultAfterBarrierRequiresRecovery()
    {
        using var fixture = new HistoryFixture();
        var (authorization, fresh) = CreateAuthorization(entryCount: 1);
        await using var store = fixture.CreateStore();
        var executor = new FileRecycleOperationExecutor(
            new FixedFreshValidator(fresh),
            store,
            new NullResultProvider());

        var result = await executor.ExecuteAsync(authorization);

        Assert.AreEqual(FileRecycleExecutionStatus.RecoveryRequired, result.Status);
        Assert.IsNotNull(result.History);
        Assert.AreEqual(FileRecycleActionTerminalState.RecoveryRequired, result.History.TerminalState);
        Assert.AreEqual(FileRecycleActionEntryState.RecoveryRequired, result.History.Entries[0].State);
        Assert.AreEqual("RecycleProviderThrewAfterMutationBarrier", result.Failure?.Code);
    }

    [TestMethod]
    public void HistoryRejectsPendingBeforeLaterRecycledEvidence()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new[]
        {
            new FileRecycleActionEntry(
                0,
                new FileOperationEntry(@"C:\Users\Alice\Temp\a.tmp", "a.tmp", IsDirectory: false),
                @"C:\Users\Alice\Temp\a.tmp",
                new FileIdentity(7, 701),
                FileRecycleActionEntryState.Pending,
                MutationStartedAtUtc: null,
                CompletedAtUtc: null,
                ProviderName: null,
                RecycleLocator: null,
                Failure: null),
            new FileRecycleActionEntry(
                1,
                new FileOperationEntry(@"C:\Users\Alice\Temp\b.tmp", "b.tmp", IsDirectory: false),
                @"C:\Users\Alice\Temp\b.tmp",
                new FileIdentity(7, 702),
                FileRecycleActionEntryState.Recycled,
                now,
                now,
                "test-provider",
                "opaque:1",
                Failure: null),
        };

        Assert.Throws<ArgumentException>(() => new FileRecycleActionHistory(
            Guid.NewGuid(),
            Guid.NewGuid(),
            now,
            now,
            now,
            now,
            now,
            completedAtUtc: null,
            "left",
            Guid.NewGuid(),
            @"C:\Users\Alice\Temp",
            new FileIdentity(7, 700),
            terminalState: null,
            entries));
    }

    private static (FileRecycleOperationUserAuthorizationReceipt Authorization, FileRecycleFreshIdentityResult Fresh) CreateAuthorization(int entryCount)
    {
        var entries = new FileOperationEntry[entryCount];
        var reviewItems = new FileRecycleOperationReviewItem[entryCount];
        for (var ordinal = 0; ordinal < entryCount; ordinal++)
        {
            var path = $@"C:\Users\Alice\Temp\recycle-{ordinal}.tmp";
            var entry = new FileOperationEntry(path, $"recycle-{ordinal}.tmp", IsDirectory: false);
            entries[ordinal] = entry;
            reviewItems[ordinal] = new FileRecycleOperationReviewItem(
                entry,
                path,
                new FileIdentity(7, checked((ulong)(701 + ordinal))));
        }

        var intent = new FileRecycleOperationIntent(
            "left",
            Guid.NewGuid(),
            @"C:\Users\Alice\Temp",
            entries);
        var plan = new FileRecycleOperationPlan(Guid.NewGuid(), DateTimeOffset.UtcNow, intent);
        var review = new FileRecycleOperationReview(
            plan,
            @"C:\Users\Alice\Temp",
            new FileIdentity(7, 700),
            reviewItems,
            DateTimeOffset.UtcNow);
        var authorization = new FileRecycleOperationUserAuthorizationIssuer()
            .IssueAfterExplicitUserConfirmation(review);
        var fresh = FileRecycleFreshIdentityResult.Ready(
            authorization,
            review.CanonicalSourceDirectoryPath,
            review.SourceDirectoryIdentity,
            review.Items,
            DateTimeOffset.UtcNow);
        return (authorization, fresh);
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

    private sealed class FixedFreshValidator : IFileRecycleFreshIdentityValidator
    {
        private readonly FileRecycleFreshIdentityResult _fresh;

        public FixedFreshValidator(FileRecycleFreshIdentityResult fresh)
        {
            _fresh = fresh;
        }

        public ValueTask<FileRecycleFreshIdentityResult> ValidateAsync(
            FileRecycleOperationUserAuthorizationReceipt authorization,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_fresh.IsBoundTo(authorization))
            {
                throw new InvalidOperationException("Test freshness evidence is bound to another authorization.");
            }
            return new ValueTask<FileRecycleFreshIdentityResult>(_fresh);
        }
    }

    private sealed class BarrierInspectingProvider : IFileRecycleMutationProvider
    {
        private readonly IFileRecycleActionHistoryStore _store;

        public BarrierInspectingProvider(IFileRecycleActionHistoryStore store)
        {
            _store = store;
        }

        public string ProviderName => "barrier-inspecting-provider";
        public bool ObservedMutationBarrier { get; private set; }

        public async ValueTask<FileRecycleMutationResult> RecycleAsync(FileRecycleMutationRequest request)
        {
            var history = await _store.GetAsync(request.OperationId);
            ObservedMutationBarrier = history is not null &&
                history.Entries[request.Ordinal].State == FileRecycleActionEntryState.MutationStarted;
            return FileRecycleMutationResult.Recycled(
                request.SourceIdentity,
                ProviderName,
                $"opaque:{request.Ordinal}");
        }
    }

    private sealed class ChangingNameProvider : IFileRecycleMutationProvider
    {
        private int _providerNameReadCount;

        public int ProviderNameReadCount => _providerNameReadCount;

        public string ProviderName =>
            Interlocked.Increment(ref _providerNameReadCount) == 1
                ? "captured-provider"
                : "changed-provider";

        public ValueTask<FileRecycleMutationResult> RecycleAsync(FileRecycleMutationRequest request) =>
            new(FileRecycleMutationResult.Recycled(
                request.SourceIdentity,
                "captured-provider",
                "opaque:captured"));
    }

    private sealed class ForgedNameProvider : IFileRecycleMutationProvider
    {
        public string ProviderName => "trusted-provider";

        public ValueTask<FileRecycleMutationResult> RecycleAsync(FileRecycleMutationRequest request) =>
            new(FileRecycleMutationResult.Recycled(
                request.SourceIdentity,
                "forged-provider",
                "opaque:forged"));
    }

    private sealed class ThrowingProvider : IFileRecycleMutationProvider
    {
        public string ProviderName => "throwing-provider";

        public ValueTask<FileRecycleMutationResult> RecycleAsync(FileRecycleMutationRequest request) =>
            throw new InvalidOperationException("synthetic provider failure");
    }

    private sealed class NullResultProvider : IFileRecycleMutationProvider
    {
        public string ProviderName => "null-result-provider";

        public ValueTask<FileRecycleMutationResult> RecycleAsync(FileRecycleMutationRequest request) =>
            ValueTask.FromResult<FileRecycleMutationResult>(null!);
    }

    private sealed class HistoryFixture : IDisposable
    {
        public HistoryFixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "FileOp.RecycleHistory.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            DatabasePath = Path.Combine(DirectoryPath, "history.sqlite");
        }

        public string DirectoryPath { get; }
        public string DatabasePath { get; }

        public SqliteFileRecycleActionHistoryStore CreateStore() => new(DatabasePath);

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
