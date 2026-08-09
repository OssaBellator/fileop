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
    public async Task CopyCommitPersistsUndoCandidateAndHighBitIdentity()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation(includeSkippedEntry: true);
        var destinationIdentity = new FileIdentity(ulong.MaxValue - 17, ulong.MaxValue - 31);

        using (var store = new SqliteFileOperationActionHistoryStore(fixture.DatabasePath))
        {
            var begun = await store.BeginAsync(validation, DateTimeOffset.UtcNow);
            Assert.AreEqual(FileOperationActionEntryState.Pending, begun.Entries[0].State);
            Assert.AreEqual(FileOperationActionEntryState.Skipped, begun.Entries[1].State);

            var started = await store.MarkMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);
            Assert.IsTrue(started.RequiresRecovery);

            var committed = await store.CommitCopyAsync(
                validation.Plan.Id,
                0,
                destinationIdentity,
                DateTimeOffset.UtcNow);
            Assert.IsFalse(committed.RequiresRecovery);
            Assert.AreEqual(FileOperationActionEntryState.Committed, committed.Entries[0].State);
            Assert.AreEqual(destinationIdentity, committed.Entries[0].DestinationIdentity);
            Assert.IsTrue(committed.Entries[0].IsUndoCandidate);

            await store.CompleteAsync(
                validation.Plan.Id,
                FileOperationActionTerminalState.Succeeded,
                DateTimeOffset.UtcNow);
        }

        using var reopened = new SqliteFileOperationActionHistoryStore(fixture.DatabasePath);
        var persisted = await reopened.GetAsync(validation.Plan.Id);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(FileOperationActionTerminalState.Succeeded, persisted.TerminalState);
        Assert.AreEqual(1, persisted.UndoCandidateEntries.Count);
        Assert.AreEqual(destinationIdentity, persisted.UndoCandidateEntries[0].DestinationIdentity);
    }

    [TestMethod]
    public async Task RecoveryRequiredPersistsVerifiedDestinationIdentityWithoutUndoCandidate()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation(includeSkippedEntry: false);
        var destinationIdentity = new FileIdentity(ulong.MaxValue - 41, ulong.MaxValue - 59);
        var failure = new FileOperationFailure(
            "CopyCommitBarrierFailed",
            "Copy completed but durable commit persistence failed.",
            validation.Items[0].Destination.CanonicalPath,
            Retryable: false);

        using (var store = new SqliteFileOperationActionHistoryStore(fixture.DatabasePath))
        {
            await store.BeginAsync(validation, DateTimeOffset.UtcNow);
            await store.MarkMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);
            var recovered = await store.MarkMutationRecoveryRequiredAsync(
                validation.Plan.Id,
                0,
                failure,
                DateTimeOffset.UtcNow,
                destinationIdentity: destinationIdentity);

            Assert.AreEqual(FileOperationActionEntryState.RecoveryRequired, recovered.Entries[0].State);
            Assert.AreEqual(destinationIdentity, recovered.Entries[0].DestinationIdentity);
            Assert.AreEqual(FileOperationUndoKind.None, recovered.Entries[0].UndoKind);
            Assert.IsFalse(recovered.Entries[0].IsUndoCandidate);
            Assert.AreEqual(failure, recovered.Entries[0].Failure);

            await store.CompleteAsync(
                validation.Plan.Id,
                FileOperationActionTerminalState.RecoveryRequired,
                DateTimeOffset.UtcNow);
        }

        using var reopened = new SqliteFileOperationActionHistoryStore(fixture.DatabasePath);
        var persisted = await reopened.GetAsync(validation.Plan.Id);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(FileOperationActionTerminalState.RecoveryRequired, persisted.TerminalState);
        Assert.AreEqual(destinationIdentity, persisted.Entries[0].DestinationIdentity);
        Assert.AreEqual(FileOperationUndoKind.None, persisted.Entries[0].UndoKind);
        Assert.AreEqual(0, persisted.UndoCandidateEntries.Count);
    }

    [TestMethod]
    public async Task RecoveryRequiredWithoutVerifiedIdentityRemainsIdentityless()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation(includeSkippedEntry: false);
        var failure = new FileOperationFailure(
            "CopyMutationFailed",
            "Copy failed after crossing the mutation barrier.",
            validation.Items[0].Destination.CanonicalPath,
            Retryable: false);

        using var store = new SqliteFileOperationActionHistoryStore(fixture.DatabasePath);
        await store.BeginAsync(validation, DateTimeOffset.UtcNow);
        await store.MarkMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);
        var recovered = await store.MarkMutationRecoveryRequiredAsync(
            validation.Plan.Id,
            0,
            failure,
            DateTimeOffset.UtcNow);

        Assert.AreEqual(FileOperationActionEntryState.RecoveryRequired, recovered.Entries[0].State);
        Assert.IsNull(recovered.Entries[0].DestinationIdentity);
        Assert.AreEqual(FileOperationUndoKind.None, recovered.Entries[0].UndoKind);
        Assert.IsFalse(recovered.Entries[0].IsUndoCandidate);
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

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await reopened.CompleteAsync(
                validation.Plan.Id,
                FileOperationActionTerminalState.Failed,
                DateTimeOffset.UtcNow));

        var recovered = await reopened.CompleteAsync(
            validation.Plan.Id,
            FileOperationActionTerminalState.RecoveryRequired,
            DateTimeOffset.UtcNow);
        Assert.AreEqual(FileOperationActionEntryState.RecoveryRequired, recovered.Entries[0].State);
        Assert.IsTrue(recovered.RequiresRecovery);
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
        Assert.AreEqual(failure, failed.Entries[0].Failure);

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
    public async Task BeginRejectsReadyDirectoryAndMoveWithoutRows()
    {
        using var fixture = new HistoryFixture();
        using var store = new SqliteFileOperationActionHistoryStore(fixture.DatabasePath);

        var directory = CreateValidation(
            includeSkippedEntry: false,
            operationId: Guid.NewGuid(),
            firstIsDirectory: true);
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await store.BeginAsync(directory, DateTimeOffset.UtcNow));
        Assert.IsNull(await store.GetAsync(directory.Plan.Id));

        var move = CreateValidation(
            includeSkippedEntry: false,
            operationId: Guid.NewGuid(),
            kind: FileOperationKind.Move);
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await store.BeginAsync(move, DateTimeOffset.UtcNow));
        Assert.IsNull(await store.GetAsync(move.Plan.Id));
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
            null,
            null,
            new FileIdentity(1, 10),
            null,
            FileOperationUndoKind.None,
            null);
        var entries = new List<FileOperationActionEntry> { entry };
        var now = DateTimeOffset.UtcNow;
        var history = new FileOperationActionHistory(
            Guid.NewGuid(),
            now,
            now,
            now,
            null,
            FileOperationKind.Copy,
            FileOperationCollisionPolicy.Skip,
            @"C:\Source",
            @"D:\Destination",
            @"C:\Real\Source",
            @"D:\Real\Destination",
            null,
            entries);

        entries.Clear();

        Assert.AreEqual(1, history.Entries.Count);
        Assert.AreEqual(entry, history.Entries[0]);
    }

    private static FileOperationExecutionValidationResult CreateValidation(
        bool includeSkippedEntry,
        Guid? operationId = null,
        bool firstIsDirectory = false,
        FileOperationKind kind = FileOperationKind.Copy)
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
            kind,
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
