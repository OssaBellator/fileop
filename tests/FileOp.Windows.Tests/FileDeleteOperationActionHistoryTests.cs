using System;
using System.IO;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileDeleteOperationActionHistoryTests
{
    [TestMethod]
    public async Task BeginPersistsExactSourceOnlyAuthorizationEvidenceIncludingHighBits()
    {
        using var fixture = new HistoryFixture();
        var authorization = CreateAuthorization(
            new FileIdentity(0xF123456789ABCDEFUL, 0xFEDCBA9876543210UL),
            new FileIdentity(0x8123456789ABCDEFUL, 0x923456789ABCDEF0UL),
            entryCount: 2);
        await using var store = fixture.CreateStore();

        var history = await store.BeginAsync(authorization);
        var reopened = await store.GetAsync(authorization.PlanId);

        Assert.IsNotNull(reopened);
        Assert.AreEqual(authorization.PlanId, history.OperationId);
        Assert.AreEqual(authorization.AuthorizationId, history.AuthorizationId);
        Assert.AreEqual(authorization.CanonicalSourceDirectoryPath, history.CanonicalSourceDirectoryPath);
        Assert.AreEqual(authorization.SourceDirectoryIdentity, history.SourceDirectoryIdentity);
        Assert.AreEqual(authorization.Items.Count, history.Entries.Count);
        Assert.IsNull(history.TerminalState);
        Assert.IsFalse(history.DeleteMutationAuthorized);
        Assert.IsFalse(history.IsRecoverySensitive);
        Assert.AreEqual(history.SourceDirectoryIdentity, reopened.SourceDirectoryIdentity);
        Assert.AreEqual(history.Entries[0].SourceIdentity, reopened.Entries[0].SourceIdentity);
        Assert.AreEqual(history.Entries[1].SourceIdentity, reopened.Entries[1].SourceIdentity);
        Assert.AreEqual(FileDeleteOperationActionEntryState.Pending, reopened.Entries[0].State);
        Assert.IsFalse(reopened.DeleteMutationAuthorized);
    }

    [TestMethod]
    public async Task MutationBarrierThenExactIdentityCommitCanCompleteSucceeded()
    {
        using var fixture = new HistoryFixture();
        var authorization = CreateAuthorization(entryCount: 1);
        await using var store = fixture.CreateStore();
        await store.BeginAsync(authorization);

        var started = await store.MarkMutationStartedAsync(authorization.PlanId, 0);
        Assert.AreEqual(FileDeleteOperationActionEntryState.MutationStarted, started.Entries[0].State);
        Assert.IsTrue(started.Entries[0].MutationStartedAtUtc.HasValue);
        Assert.IsTrue(started.IsRecoverySensitive);

        await AssertThrowsAsync<InvalidOperationException>(async () =>
            await store.CommitDeletedAsync(
                authorization.PlanId,
                0,
                new FileIdentity(
                    authorization.Items[0].Identity.VolumeSerialNumber,
                    authorization.Items[0].Identity.FileReferenceNumber + 1)));

        var stillStarted = await store.GetAsync(authorization.PlanId);
        Assert.IsNotNull(stillStarted);
        Assert.AreEqual(FileDeleteOperationActionEntryState.MutationStarted, stillStarted.Entries[0].State);

        var committed = await store.CommitDeletedAsync(
            authorization.PlanId,
            0,
            authorization.Items[0].Identity);
        Assert.AreEqual(FileDeleteOperationActionEntryState.Committed, committed.Entries[0].State);
        Assert.IsFalse(committed.IsRecoverySensitive);

        var completed = await store.CompleteAsync(authorization.PlanId);
        Assert.AreEqual(FileDeleteOperationActionTerminalState.Succeeded, completed.TerminalState);
        Assert.IsTrue(completed.CompletedAtUtc.HasValue);
        Assert.IsTrue(completed.IsTerminal);
        Assert.IsFalse(completed.DeleteMutationAuthorized);
    }

    [TestMethod]
    public async Task PreBarrierFailureCanCompleteFailedWithLaterPendingEntryUntouched()
    {
        using var fixture = new HistoryFixture();
        var authorization = CreateAuthorization(entryCount: 2);
        await using var store = fixture.CreateStore();
        await store.BeginAsync(authorization);

        var failure = new FileOperationFailure("delete.preflight", "blocked before mutation", authorization.Items[0].CanonicalPath, Retryable: false);
        var failed = await store.MarkFailedAsync(authorization.PlanId, 0, failure);
        Assert.AreEqual(FileDeleteOperationActionEntryState.Failed, failed.Entries[0].State);
        Assert.AreEqual(FileDeleteOperationActionEntryState.Pending, failed.Entries[1].State);
        Assert.IsFalse(failed.IsRecoverySensitive);

        var completed = await store.CompleteAsync(authorization.PlanId);
        Assert.AreEqual(FileDeleteOperationActionTerminalState.Failed, completed.TerminalState);
        Assert.AreEqual(FileDeleteOperationActionEntryState.Pending, completed.Entries[1].State);
        Assert.IsFalse(completed.IsRecoverySensitive);
    }

    [TestMethod]
    public async Task PostBarrierFailureMustSetRecoveryRequiredAndDominatesCompletion()
    {
        using var fixture = new HistoryFixture();
        var authorization = CreateAuthorization(entryCount: 2);
        await using var store = fixture.CreateStore();
        await store.BeginAsync(authorization);
        await store.MarkMutationStartedAsync(authorization.PlanId, 0);

        var failure = new FileOperationFailure("delete.ambiguous", "mutation outcome is ambiguous", authorization.Items[0].CanonicalPath, Retryable: false);
        var recovery = await store.MarkMutationRecoveryRequiredAsync(authorization.PlanId, 0, failure);
        Assert.AreEqual(FileDeleteOperationActionEntryState.RecoveryRequired, recovery.Entries[0].State);
        Assert.AreEqual(FileDeleteOperationActionEntryState.Pending, recovery.Entries[1].State);
        Assert.IsTrue(recovery.IsRecoverySensitive);

        var completed = await store.CompleteAsync(authorization.PlanId);
        Assert.AreEqual(FileDeleteOperationActionTerminalState.RecoveryRequired, completed.TerminalState);
        Assert.IsTrue(completed.IsRecoverySensitive);
        Assert.IsFalse(completed.DeleteMutationAuthorized);
    }

    [TestMethod]
    public async Task DuplicateOrPostTerminalTransitionsFailClosed()
    {
        using var fixture = new HistoryFixture();
        var authorization = CreateAuthorization(entryCount: 1);
        await using var store = fixture.CreateStore();
        await store.BeginAsync(authorization);
        await store.MarkMutationStartedAsync(authorization.PlanId, 0);

        await AssertThrowsAsync<InvalidOperationException>(async () =>
            await store.MarkMutationStartedAsync(authorization.PlanId, 0));

        await store.CommitDeletedAsync(authorization.PlanId, 0, authorization.Items[0].Identity);
        await store.CompleteAsync(authorization.PlanId);

        await AssertThrowsAsync<InvalidOperationException>(async () =>
            await store.MarkFailedAsync(
                authorization.PlanId,
                0,
                new FileOperationFailure("late", "late failure", null, Retryable: false)));
        await AssertThrowsAsync<InvalidOperationException>(async () =>
            await store.CompleteAsync(authorization.PlanId));
    }

    private static FileDeleteOperationUserAuthorizationReceipt CreateAuthorization(
        FileIdentity? rootIdentity = null,
        FileIdentity? firstIdentity = null,
        int entryCount = 1)
    {
        var root = rootIdentity ?? new FileIdentity(7, 700);
        var entries = new FileOperationEntry[entryCount];
        var validationItems = new FileDeleteOperationExecutionValidationItem[entryCount];
        for (var ordinal = 0; ordinal < entryCount; ordinal++)
        {
            var path = $@"C:\Users\Alice\Temp\delete-{ordinal}.tmp";
            var entry = new FileOperationEntry(path, $"delete-{ordinal}.tmp", IsDirectory: false);
            entries[ordinal] = entry;
            var identity = ordinal == 0 && firstIdentity.HasValue
                ? firstIdentity.Value
                : new FileIdentity(7, checked((ulong)(701 + ordinal)));
            validationItems[ordinal] = new FileDeleteOperationExecutionValidationItem(
                entry,
                new FileOperationCanonicalPath(
                    path,
                    path,
                    FileOperationCanonicalPathState.File,
                    IsLeafReparsePoint: false,
                    identity),
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
                root),
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

    private sealed class HistoryFixture : IDisposable
    {
        public HistoryFixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "FileOp.DeleteHistory.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            DatabasePath = Path.Combine(DirectoryPath, "history.sqlite");
        }

        public string DirectoryPath { get; }

        public string DatabasePath { get; }

        public SqliteFileDeleteOperationActionHistoryStore CreateStore() => new(DatabasePath);

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
