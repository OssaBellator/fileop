using System;
using System.IO;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileSameVolumeMoveActionHistoryTests
{
    [TestMethod]
    public async Task SameVolumeMoveCommitPreservesSourceIdentityWithoutCopyEvidence()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateSameVolumeMoveValidation();
        var identity = validation.Items[0].Source.Identity!.Value;

        using (var store = new SqliteFileOperationActionHistoryStore(fixture.DatabasePath))
        {
            var begun = await store.BeginAsync(validation, DateTimeOffset.UtcNow);
            Assert.AreEqual(FileOperationKind.Move, begun.Kind);
            Assert.AreEqual(FileOperationActionEntryState.Pending, begun.Entries[0].State);

            await store.MarkMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);
            var committed = await store.CommitSameVolumeMoveAsync(
                validation.Plan.Id,
                0,
                identity,
                DateTimeOffset.UtcNow);

            Assert.AreEqual(FileOperationActionEntryState.Committed, committed.Entries[0].State);
            Assert.AreEqual(identity, committed.Entries[0].SourceIdentity);
            Assert.AreEqual(identity, committed.Entries[0].DestinationIdentity);
            Assert.AreEqual(FileOperationUndoKind.None, committed.Entries[0].UndoKind);
            Assert.IsNull(committed.Entries[0].DestinationContentFingerprint);
            Assert.IsNull(committed.Entries[0].DestinationHardLinkCount);
            Assert.IsFalse(committed.Entries[0].IsUndoCandidate);

            await store.CompleteAsync(
                validation.Plan.Id,
                FileOperationActionTerminalState.Succeeded,
                DateTimeOffset.UtcNow);
        }

        using var reopened = new SqliteFileOperationActionHistoryStore(fixture.DatabasePath);
        var persisted = await reopened.GetAsync(validation.Plan.Id);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(FileOperationKind.Move, persisted.Kind);
        Assert.AreEqual(FileOperationActionTerminalState.Succeeded, persisted.TerminalState);
        Assert.AreEqual(identity, persisted.Entries[0].DestinationIdentity);
        Assert.AreEqual(0, persisted.UndoCandidateEntries.Count);
    }

    [TestMethod]
    public async Task SameVolumeMoveCommitRejectsChangedIdentity()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateSameVolumeMoveValidation();
        using var store = new SqliteFileOperationActionHistoryStore(fixture.DatabasePath);
        await store.BeginAsync(validation, DateTimeOffset.UtcNow);
        await store.MarkMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);

        var source = validation.Items[0].Source.Identity!.Value;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await store.CommitSameVolumeMoveAsync(
                validation.Plan.Id,
                0,
                new FileIdentity(source.VolumeSerialNumber, source.FileReferenceNumber + 1),
                DateTimeOffset.UtcNow));

        var stillStarted = await store.GetAsync(validation.Plan.Id);
        Assert.IsNotNull(stillStarted);
        Assert.AreEqual(FileOperationActionEntryState.MutationStarted, stillStarted.Entries[0].State);
        Assert.IsTrue(stillStarted.RequiresRecovery);
    }

    [TestMethod]
    public async Task SameVolumeMoveRecoveryAllowsOnlyOriginalIdentityEvidence()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateSameVolumeMoveValidation();
        var source = validation.Items[0].Source.Identity!.Value;
        var failure = new FileOperationFailure(
            "MoveCommitBarrierFailed",
            "Rename completed but durable commit could not be finalized.",
            validation.Items[0].Destination.CanonicalPath,
            Retryable: false);

        using var store = new SqliteFileOperationActionHistoryStore(fixture.DatabasePath);
        await store.BeginAsync(validation, DateTimeOffset.UtcNow);
        await store.MarkMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await store.MarkSameVolumeMoveRecoveryRequiredAsync(
                validation.Plan.Id,
                0,
                failure,
                DateTimeOffset.UtcNow,
                new FileIdentity(source.VolumeSerialNumber, source.FileReferenceNumber + 7)));

        var recovered = await store.MarkSameVolumeMoveRecoveryRequiredAsync(
            validation.Plan.Id,
            0,
            failure,
            DateTimeOffset.UtcNow,
            source);
        Assert.AreEqual(FileOperationActionEntryState.RecoveryRequired, recovered.Entries[0].State);
        Assert.AreEqual(source, recovered.Entries[0].DestinationIdentity);
        Assert.AreEqual(FileOperationUndoKind.None, recovered.Entries[0].UndoKind);

        var terminal = await store.CompleteAsync(
            validation.Plan.Id,
            FileOperationActionTerminalState.RecoveryRequired,
            DateTimeOffset.UtcNow);
        Assert.IsTrue(terminal.RequiresRecovery);
    }

    private static FileOperationExecutionValidationResult CreateSameVolumeMoveValidation()
    {
        var sourceDirectory = Path.GetFullPath(@"C:\Source");
        var destinationDirectory = Path.GetFullPath(@"C:\Destination");
        var canonicalSourceDirectory = Path.GetFullPath(@"C:\Real\Source");
        var canonicalDestinationDirectory = Path.GetFullPath(@"C:\Real\Destination");
        var entry = new FileOperationEntry(
            Path.Combine(sourceDirectory, "a.txt"),
            "a.txt",
            IsDirectory: false);
        var plan = new FileOperationPlan(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero),
            FileOperationKind.Move,
            FileOperationCollisionPolicy.Stop,
            new FileOperationIntent(
                "Left",
                Guid.NewGuid(),
                sourceDirectory,
                new[] { entry },
                "Right",
                Guid.NewGuid(),
                destinationDirectory));
        var sourceIdentity = new FileIdentity(11, 101);
        var item = new FileOperationExecutionValidationItem(
            entry,
            new FileOperationCanonicalPath(
                entry.Path,
                Path.Combine(canonicalSourceDirectory, entry.Name),
                FileOperationCanonicalPathState.File,
                IsLeafReparsePoint: false,
                Identity: sourceIdentity),
            new FileOperationCanonicalPath(
                Path.Combine(destinationDirectory, entry.Name),
                Path.Combine(canonicalDestinationDirectory, entry.Name),
                FileOperationCanonicalPathState.Missing,
                IsLeafReparsePoint: false),
            FileOperationExecutionValidationDecision.Ready,
            "ready");

        return new FileOperationExecutionValidationResult(
            plan,
            new FileOperationCanonicalPath(
                sourceDirectory,
                canonicalSourceDirectory,
                FileOperationCanonicalPathState.Directory,
                IsLeafReparsePoint: false,
                Identity: new FileIdentity(11, 10)),
            new FileOperationCanonicalPath(
                destinationDirectory,
                canonicalDestinationDirectory,
                FileOperationCanonicalPathState.Directory,
                IsLeafReparsePoint: false,
                Identity: new FileIdentity(11, 20)),
            new[] { item },
            FileOperationExecutionValidationStatus.Ready,
            new DateTimeOffset(2026, 8, 14, 0, 1, 0, TimeSpan.Zero),
            "ready");
    }

    private sealed class HistoryFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "FileOp.MoveHistory.Tests",
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
