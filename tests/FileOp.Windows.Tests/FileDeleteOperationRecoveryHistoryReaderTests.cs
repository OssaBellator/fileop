using System;
using System.IO;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileDeleteOperationRecoveryHistoryReaderTests
{
    [TestMethod]
    public async Task RestartReaderReturnsOnlyRecoverySensitiveHistoriesNewestFirst()
    {
        using var fixture = new HistoryFixture();
        var time = new TestTimeProvider(new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero));

        var pending = CreateAuthorization("pending", new FileIdentity(10, 1000), new FileIdentity(10, 1001));
        var failed = CreateAuthorization("failed", new FileIdentity(11, 1100), new FileIdentity(11, 1101));
        var succeeded = CreateAuthorization("succeeded", new FileIdentity(12, 1200), new FileIdentity(12, 1201));
        var mutationStarted = CreateAuthorization("started", new FileIdentity(13, 1300), new FileIdentity(13, 1301));
        var recoveryEntry = CreateAuthorization("recovery-entry", new FileIdentity(14, 1400), new FileIdentity(14, 1401));
        var terminalRecovery = CreateAuthorization(
            "terminal-recovery",
            new FileIdentity(0xF123456789ABCDEFUL, 0xFEDCBA9876543210UL),
            new FileIdentity(0x8123456789ABCDEFUL, 0x923456789ABCDEF0UL));

        await using (var writer = new SqliteFileDeleteOperationActionHistoryStore(fixture.DatabasePath, time))
        {
            await writer.BeginAsync(pending);

            time.Advance(TimeSpan.FromSeconds(1));
            await writer.BeginAsync(failed);
            await writer.MarkFailedAsync(
                failed.PlanId,
                0,
                new FileOperationFailure("pre-barrier", "blocked before mutation", failed.Items[0].CanonicalPath, Retryable: false));
            await writer.CompleteAsync(failed.PlanId);

            time.Advance(TimeSpan.FromSeconds(1));
            await writer.BeginAsync(succeeded);
            await writer.MarkMutationStartedAsync(succeeded.PlanId, 0);
            await writer.CommitDeletedAsync(succeeded.PlanId, 0, succeeded.Items[0].Identity);
            await writer.CompleteAsync(succeeded.PlanId);

            time.Advance(TimeSpan.FromSeconds(1));
            await writer.BeginAsync(mutationStarted);
            await writer.MarkMutationStartedAsync(mutationStarted.PlanId, 0);

            time.Advance(TimeSpan.FromSeconds(1));
            await writer.BeginAsync(recoveryEntry);
            await writer.MarkMutationStartedAsync(recoveryEntry.PlanId, 0);
            await writer.MarkMutationRecoveryRequiredAsync(
                recoveryEntry.PlanId,
                0,
                new FileOperationFailure("ambiguous", "mutation outcome unknown", recoveryEntry.Items[0].CanonicalPath, Retryable: false));

            time.Advance(TimeSpan.FromSeconds(1));
            await writer.BeginAsync(terminalRecovery);
            await writer.MarkMutationStartedAsync(terminalRecovery.PlanId, 0);
            await writer.CompleteAsync(terminalRecovery.PlanId);
        }

        await using var reader = new SqliteFileDeleteOperationRecoveryHistoryReader(fixture.DatabasePath);
        var candidates = await reader.GetRecoveryCandidatesAsync();

        Assert.AreEqual(3, candidates.Count);
        Assert.AreEqual(terminalRecovery.PlanId, candidates[0].OperationId);
        Assert.AreEqual(recoveryEntry.PlanId, candidates[1].OperationId);
        Assert.AreEqual(mutationStarted.PlanId, candidates[2].OperationId);

        Assert.AreEqual(FileDeleteOperationActionTerminalState.RecoveryRequired, candidates[0].TerminalState);
        Assert.AreEqual(FileDeleteOperationActionEntryState.MutationStarted, candidates[0].Entries[0].State);
        Assert.IsNull(candidates[1].TerminalState);
        Assert.AreEqual(FileDeleteOperationActionEntryState.RecoveryRequired, candidates[1].Entries[0].State);
        Assert.IsNull(candidates[2].TerminalState);
        Assert.AreEqual(FileDeleteOperationActionEntryState.MutationStarted, candidates[2].Entries[0].State);

        foreach (var candidate in candidates)
        {
            Assert.IsTrue(candidate.IsRecoverySensitive);
            Assert.IsFalse(candidate.DeleteMutationAuthorized);
        }

        Assert.AreEqual(terminalRecovery.SourceDirectoryIdentity, candidates[0].SourceDirectoryIdentity);
        Assert.AreEqual(terminalRecovery.Items[0].Identity, candidates[0].Entries[0].SourceIdentity);
        Assert.IsFalse(ContainsOperation(candidates, pending.PlanId));
        Assert.IsFalse(ContainsOperation(candidates, failed.PlanId));
        Assert.IsFalse(ContainsOperation(candidates, succeeded.PlanId));
    }

    [TestMethod]
    public async Task RecoveryDiscoveryLimitIsBoundedAndPreservesNewestOrdering()
    {
        using var fixture = new HistoryFixture();
        var time = new TestTimeProvider(new DateTimeOffset(2026, 8, 12, 1, 0, 0, TimeSpan.Zero));
        var oldest = CreateAuthorization("oldest", new FileIdentity(20, 2000), new FileIdentity(20, 2001));
        var middle = CreateAuthorization("middle", new FileIdentity(21, 2100), new FileIdentity(21, 2101));
        var newest = CreateAuthorization("newest", new FileIdentity(22, 2200), new FileIdentity(22, 2201));

        await using (var writer = new SqliteFileDeleteOperationActionHistoryStore(fixture.DatabasePath, time))
        {
            await writer.BeginAsync(oldest);
            await writer.MarkMutationStartedAsync(oldest.PlanId, 0);

            time.Advance(TimeSpan.FromSeconds(1));
            await writer.BeginAsync(middle);
            await writer.MarkMutationStartedAsync(middle.PlanId, 0);

            time.Advance(TimeSpan.FromSeconds(1));
            await writer.BeginAsync(newest);
            await writer.MarkMutationStartedAsync(newest.PlanId, 0);
        }

        await using var reader = new SqliteFileDeleteOperationRecoveryHistoryReader(fixture.DatabasePath);
        var limited = await reader.GetRecoveryCandidatesAsync(limit: 2);

        Assert.AreEqual(2, limited.Count);
        Assert.AreEqual(newest.PlanId, limited[0].OperationId);
        Assert.AreEqual(middle.PlanId, limited[1].OperationId);

        await AssertThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await reader.GetRecoveryCandidatesAsync(limit: 0));
        await AssertThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await reader.GetRecoveryCandidatesAsync(limit: 4_097));
    }

    [TestMethod]
    public async Task MissingDatabaseIsNotCreatedByRecoveryReader()
    {
        using var fixture = new HistoryFixture();
        Assert.IsFalse(File.Exists(fixture.DatabasePath));
        await using var reader = new SqliteFileDeleteOperationRecoveryHistoryReader(fixture.DatabasePath);

        await AssertThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(async () =>
            await reader.GetRecoveryCandidatesAsync());

        Assert.IsFalse(File.Exists(fixture.DatabasePath));
    }

    [TestMethod]
    public async Task DisposedRecoveryReaderFailsClosed()
    {
        using var fixture = new HistoryFixture();
        var authorization = CreateAuthorization("disposed", new FileIdentity(30, 3000), new FileIdentity(30, 3001));
        await using (var writer = new SqliteFileDeleteOperationActionHistoryStore(fixture.DatabasePath))
        {
            await writer.BeginAsync(authorization);
            await writer.MarkMutationStartedAsync(authorization.PlanId, 0);
        }

        var reader = new SqliteFileDeleteOperationRecoveryHistoryReader(fixture.DatabasePath);
        await reader.DisposeAsync();

        await AssertThrowsAsync<ObjectDisposedException>(async () =>
            await reader.GetRecoveryCandidatesAsync());
    }

    private static bool ContainsOperation(
        System.Collections.Generic.IReadOnlyList<FileDeleteOperationActionHistory> histories,
        Guid operationId)
    {
        foreach (var history in histories)
        {
            if (history.OperationId == operationId)
            {
                return true;
            }
        }

        return false;
    }

    private static FileDeleteOperationUserAuthorizationReceipt CreateAuthorization(
        string suffix,
        FileIdentity rootIdentity,
        FileIdentity fileIdentity)
    {
        var path = $@"C:\Users\Alice\Temp\recover-{suffix}.tmp";
        var entry = new FileOperationEntry(path, $"recover-{suffix}.tmp", IsDirectory: false);
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
                rootIdentity),
            new[]
            {
                new FileDeleteOperationExecutionValidationItem(
                    entry,
                    new FileOperationCanonicalPath(
                        path,
                        path,
                        FileOperationCanonicalPathState.File,
                        IsLeafReparsePoint: false,
                        fileIdentity),
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

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public TestTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount)
        {
            _utcNow = _utcNow.Add(amount);
        }
    }

    private sealed class HistoryFixture : IDisposable
    {
        public HistoryFixture()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "FileOp.DeleteRecoveryHistory.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            DatabasePath = Path.Combine(DirectoryPath, "history.sqlite");
        }

        public string DirectoryPath { get; }

        public string DatabasePath { get; }

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
