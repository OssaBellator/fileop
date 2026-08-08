using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileOperationActionHistoryTests
{
    [TestMethod]
    public async Task CopyCommitBecomesDurableUndoCandidate()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation(includeSkippedEntry: true);
        var committedIdentity = new FileIdentity(ulong.MaxValue - 17, ulong.MaxValue - 31);

        FileOperationActionHistory completed;
        using (var store = new SqliteFileOperationActionHistoryStore(fixture.DatabasePath))
        {
            var begun = await store.BeginAsync(validation, DateTimeOffset.UtcNow);
            Assert.AreEqual(FileOperationActionEntryState.Pending, begun.Entries[0].State);
            Assert.AreEqual(FileOperationActionEntryState.Skipped, begun.Entries[1].State);

            var started = await store.MarkMutationStartedAsync(
                validation.Plan.Id,
                ordinal: 0,
                DateTimeOffset.UtcNow);
            Assert.IsTrue(started.RequiresRecovery);
            Assert.AreEqual(FileOperationActionEntryState.MutationStarted, started.Entries[0].State);

            var committed = await store.CommitCopyAsync(
                validation.Plan.Id,
                ordinal: 0,
                committedIdentity,
                DateTimeOffset.UtcNow);
            Assert.IsFalse(committed.RequiresRecovery);
            Assert.AreEqual(FileOperationActionEntryState.Committed, committed.Entries[0].State);
            Assert.AreEqual(FileOperationUndoKind.DeleteCreatedDestination, committed.Entries[0].UndoKind);
            Assert.AreEqual(committedIdentity, committed.Entries[0].DestinationIdentity);
            Assert.IsTrue(committed.Entries[0].IsUndoCandidate);

            completed = await store.CompleteAsync(
                validation.Plan.Id,
                FileOperationActionTerminalState.Succeeded,
                DateTimeOffset.UtcNow);
        }

        using var reopened = new SqliteFileOperationActionHistoryStore(fixture.DatabasePath);
        var persisted = await reopened.GetAsync(validation.Plan.Id);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(FileOperationActionTerminalState.Succeeded, persisted.TerminalState);
        Assert.AreEqual(1, persisted.UndoCandidateEntries.Count);
        Assert.AreEqual(committedIdentity, persisted.UndoCandidateEntries[0].DestinationIdentity);
        Assert.AreEqual(completed.CanonicalDestinationDirectoryPath, persisted.CanonicalDestinationDirectoryPath);
    }

    [TestMethod]
    public async Task MutationStartedSurvivesAsRecoverySignal()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation(includeSkippedEntry: false);

        using (var store = new SqliteFileOperationActionHistoryStore(fixture.DatabasePath))
        {
            await store.BeginAsync(validation, DateTimeOffset.UtcNow);
            await store.MarkMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);
        }

        using var reopened = new SqliteFileOperationActionHistoryStore(fixture.DatabasePath);
        var persisted = await reopened.GetAsync(validation.Plan.Id);
        Assert.IsNotNull(persisted);
        Assert.IsTrue(persisted.RequiresRecovery);
        Assert.IsNull(persisted.TerminalState);
        Assert.AreEqual(FileOperationActionEntryState.MutationStarted, persisted.Entries[0].State);
    }

    [TestMethod]
    public async Task MutationFailureRequiresRecoveryTerminal()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation(includeSkippedEntry: false);
        var failure = new FileOperationFailure(
            "CopyFailed",
            "Copy may have changed the destination before failing.",
            validation.Items[0].Destination.CanonicalPath,
            Retryable: false);

        using var store = new SqliteFileOperationActionHistoryStore(fixture.DatabasePath);
        await store.BeginAsync(validation, DateTimeOffset.UtcNow);
        await store.MarkMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);
        var recovery = await store.MarkMutationRecoveryRequiredAsync(
            validation.Plan.Id,
            0,
            failure,
            DateTimeOffset.UtcNow);

        Assert.AreEqual(FileOperationActionEntryState.RecoveryRequired, recovery.Entries[0].State);
        Assert.AreEqual(failure, recovery.Entries[0].Failure);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await store.CompleteAsync(
                validation.Plan.Id,
                FileOperationActionTerminalState.Failed,
                DateTimeOffset.UtcNow));

        var terminal = await store.CompleteAsync(
            validation.Plan.Id,
            FileOperationActionTerminalState.RecoveryRequired,
            DateTimeOffset.UtcNow);
        Assert.AreEqual(FileOperationActionTerminalState.RecoveryRequired, terminal.TerminalState);
        Assert.IsTrue(terminal.RequiresRecovery);
    }

    [TestMethod]
    public async Task PreMutationFailureCanEndFailedWithoutRecovery()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation(includeSkippedEntry: false);
        var failure = new FileOperationFailure(
            "OpenSourceFailed",
            "Source could not be opened before mutation began.",
            validation.Items[0].Source.CanonicalPath,
            Retryable: true);

        using var store = new SqliteFileOperationActionHistoryStore(fixture.DatabasePath);
        await store.BeginAsync(validation, DateTimeOffset.UtcNow);
        var failed = await store.MarkEntryFailedBeforeMutationAsync(
            validation.Plan.Id,
            0,
            failure,
            DateTimeOffset.UtcNow);
        Assert.IsFalse(failed.RequiresRecovery);
        Assert.AreEqual(FileOperationActionEntryState.Failed, failed.Entries[0].State);

        var terminal = await store.CompleteAsync(
            validation.Plan.Id,
            FileOperationActionTerminalState.Failed,
            DateTimeOffset.UtcNow);
        Assert.AreEqual(FileOperationActionTerminalState.Failed, terminal.TerminalState);
    }

    [TestMethod]
    public async Task SuccessIsRejectedBeforeCommitBarrier()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation(includeSkippedEntry: false);

        using var store = new SqliteFileOperationActionHistoryStore(fixture.DatabasePath);
        await store.BeginAsync(validation, DateTimeOffset.UtcNow);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await store.CompleteAsync(
                validation.Plan.Id,
                FileOperationActionTerminalState.Succeeded,
                DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public async Task BeginRejectsUnresolvedValidation()
    {
        using var fixture = new HistoryFixture();
        var ready = CreateValidation(includeSkippedEntry: false);
        var unresolved = new FileOperationExecutionValidationResult(
            ready.Plan,
            ready.SourceDirectory,
            ready.DestinationDirectory,
            new[]
            {
                ready.Items[0] with
                {
                    Decision = FileOperationExecutionValidationDecision.NeedsDecision,
                },
            },
            FileOperationExecutionValidationStatus.NeedsDecision,
            ready.ValidatedAtUtc,
            "collision needs a decision");

        using var store = new SqliteFileOperationActionHistoryStore(fixture.DatabasePath);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await store.BeginAsync(unresolved, DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public async Task BeginRejectsReadyDirectoryMutation()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation(
            includeSkippedEntry: false,
            firstIsDirectory: true);

        using var store = new SqliteFileOperationActionHistoryStore(fixture.DatabasePath);
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await store.BeginAsync(validation, DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public void ActionHistoryDefensivelySnapshotsEntries()
    {
        var entry = new FileOperationActionEntry(
            0,
            new FileOperationEntry(@"C:\Source\a.txt", "a.txt", IsDirectory: false),
            @"C:\Real\Source\a.txt",
            @"D:\Real\Destination\a.txt",
            FileOperationActionEntryState.Pending,
            MutationStartedAtUtc: null,
            CompletedAtUtc: null,
            SourceIdentity: new FileIdentity(1, 10),
            DestinationIdentity: null,
            FileOperationUndoKind.None,
            Failure: null);
        var entries = new List<FileOperationActionEntry> { entry };
        var history = new FileOperationActionHistory(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            completedAtUtc: null,
            FileOperationKind.Copy,
            FileOperationCollisionPolicy.Skip,
            @"C:\Source",
            @"D:\Destination",
            @"C:\Real\Source",
            @"D:\Real\Destination",
            terminalState: null,
            entries);

        entries.Clear();

        Assert.AreEqual(1, history.Entries.Count);
        Assert.AreEqual(entry, history.Entries[0]);
    }

    [TestMethod]
    public async Task RecentHistoryIsNewestFirstAndBounded()
    {
        using var fixture = new HistoryFixture();
        using var store = new SqliteFileOperationActionHistoryStore(fixture.DatabasePath);
        var first = CreateValidation(includeSkippedEntry: true, operationId: Guid.NewGuid());
        var second = CreateValidation(includeSkippedEntry: true, operationId: Guid.NewGuid());

        await store.BeginAsync(first, new DateTimeOffset(2026, 8, 8, 1, 0, 0, TimeSpan.Zero));
        await store.BeginAsync(second, new DateTimeOffset(2026, 8, 8, 2, 0, 0, TimeSpan.Zero));

        var recent = await store.GetRecentAsync(1);
        Assert.AreEqual(1, recent.Count);
        Assert.AreEqual(second.Plan.Id, recent[0].OperationId);
    }

    private static FileOperationExecutionValidationResult CreateValidation(
        bool includeSkippedEntry,
        Guid? operationId = null,
        bool firstIsDirectory = false)
    {
        var sourceDirectory = Path.GetFullPath(@"C:\Source");
        var destinationDirectory = Path.GetFullPath(@"D:\Destination");
        var canonicalSourceDirectory = Path.GetFullPath(@"C:\Real\Source");
        var canonicalDestinationDirectory = Path.GetFullPath(@"D:\Real\Destination");
        var firstName = firstIsDirectory ? "Folder" : "a.txt";
        var first = new FileOperationEntry(
            Path.Combine(sourceDirectory, firstName),
            firstName,
            IsDirectory: firstIsDirectory);
        var entries = includeSkippedEntry
            ? new[]
            {
                first,
                new FileOperationEntry(
                    Path.Combine(sourceDirectory, "existing.txt"),
                    "existing.txt",
                    IsDirectory: false),
            }
            : new[] { first };
        var plan = new FileOperationPlan(
            operationId ?? Guid.NewGuid(),
            new DateTimeOffset(2026, 8, 8, 0, 0, 0, TimeSpan.Zero),
            FileOperationKind.Copy,
            FileOperationCollisionPolicy.Skip,
            new FileOperationIntent(
                "Left",
                Guid.NewGuid(),
                sourceDirectory,
                entries,
                "Right",
                Guid.NewGuid(),
                destinationDirectory));

        var items = entries
            .Select((entry, ordinal) =>
            {
                var destination = Path.Combine(canonicalDestinationDirectory, entry.Name);
                return new FileOperationExecutionValidationItem(
                    entry,
                    new FileOperationCanonicalPath(
                        entry.Path,
                        Path.Combine(canonicalSourceDirectory, entry.Name),
                        entry.IsDirectory
                            ? FileOperationCanonicalPathState.Directory
                            : FileOperationCanonicalPathState.File,
                        IsLeafReparsePoint: false,
                        Identity: new FileIdentity(1, (ulong)(100 + ordinal))),
                    ordinal == 0
                        ? new FileOperationCanonicalPath(
                            Path.Combine(destinationDirectory, entry.Name),
                            destination,
                            FileOperationCanonicalPathState.Missing,
                            IsLeafReparsePoint: false)
                        : new FileOperationCanonicalPath(
                            Path.Combine(destinationDirectory, entry.Name),
                            destination,
                            FileOperationCanonicalPathState.File,
                            IsLeafReparsePoint: false,
                            Identity: new FileIdentity(2, (ulong)(200 + ordinal))),
                    ordinal == 0
                        ? FileOperationExecutionValidationDecision.Ready
                        : FileOperationExecutionValidationDecision.Skip,
                    ordinal == 0 ? "ready" : "skip existing");
            })
            .ToArray();

        return new FileOperationExecutionValidationResult(
            plan,
            new FileOperationCanonicalPath(
                sourceDirectory,
                canonicalSourceDirectory,
                FileOperationCanonicalPathState.Directory,
                IsLeafReparsePoint: false,
                Identity: new FileIdentity(1, 10)),
            new FileOperationCanonicalPath(
                destinationDirectory,
                canonicalDestinationDirectory,
                FileOperationCanonicalPathState.Directory,
                IsLeafReparsePoint: false,
                Identity: new FileIdentity(2, 20)),
            items,
            FileOperationExecutionValidationStatus.Ready,
            new DateTimeOffset(2026, 8, 8, 0, 1, 0, TimeSpan.Zero),
            "ready");
    }

    private sealed class HistoryFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "FileOp.ActionHistory.Tests",
            Guid.NewGuid().ToString("N"));

        public HistoryFixture()
        {
            Directory.CreateDirectory(_directory);
            DatabasePath = Path.Combine(_directory, "actions.sqlite");
        }

        public string DatabasePath { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                try
                {
                    File.Delete(DatabasePath + suffix);
                }
                catch (IOException)
                {
                }
            }

            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
