using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileCrossVolumeMoveActionHistoryTests
{
    [TestMethod]
    public async Task HappyPathPersistsSeparateCopyAndSourceDeleteBarriers()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation(includeSkippedEntry: true);
        var destinationIdentity = new FileIdentity(2, 900);
        var fingerprint = CreateFingerprint('a');

        using (var store = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.DatabasePath))
        {
            var begun = await store.BeginAsync(validation, DateTimeOffset.UtcNow);
            Assert.AreEqual(FileCrossVolumeMoveEntryState.Pending, begun.Entries[0].State);
            Assert.AreEqual(FileCrossVolumeMoveEntryState.Skipped, begun.Entries[1].State);
            Assert.IsNull(begun.Entries[1].DestinationIdentity);
            Assert.IsNull(begun.Entries[1].DestinationContentFingerprint);

            var copyStarted = await store.MarkCopyMutationStartedAsync(
                validation.Plan.Id,
                0,
                DateTimeOffset.UtcNow);
            Assert.IsTrue(copyStarted.RequiresRecovery);

            var destinationCommitted = await store.CommitDestinationAsync(
                validation.Plan.Id,
                0,
                destinationIdentity,
                fingerprint,
                DateTimeOffset.UtcNow);
            Assert.AreEqual(
                FileCrossVolumeMoveEntryState.DestinationCommitted,
                destinationCommitted.Entries[0].State);
            Assert.IsFalse(destinationCommitted.RequiresRecovery);
            Assert.IsTrue(destinationCommitted.HasRetainedSourceDuplicates);
            Assert.AreEqual(destinationIdentity, destinationCommitted.Entries[0].DestinationIdentity);
            Assert.AreEqual(fingerprint, destinationCommitted.Entries[0].DestinationContentFingerprint);

            var deleteStarted = await store.MarkSourceDeleteStartedAsync(
                validation.Plan.Id,
                0,
                DateTimeOffset.UtcNow);
            Assert.AreEqual(
                FileCrossVolumeMoveEntryState.SourceDeleteStarted,
                deleteStarted.Entries[0].State);
            Assert.IsTrue(deleteStarted.RequiresRecovery);

            var moved = await store.CommitSourceDeletedAsync(
                validation.Plan.Id,
                0,
                validation.Items[0].Source.Identity!.Value,
                DateTimeOffset.UtcNow);
            Assert.AreEqual(FileCrossVolumeMoveEntryState.Moved, moved.Entries[0].State);
            Assert.IsFalse(moved.RequiresRecovery);

            var completed = await store.CompleteAsync(
                validation.Plan.Id,
                FileCrossVolumeMoveTerminalState.Succeeded,
                DateTimeOffset.UtcNow);
            Assert.AreEqual(FileCrossVolumeMoveTerminalState.Succeeded, completed.TerminalState);
        }

        using var reopened = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.DatabasePath);
        var persisted = await reopened.GetAsync(validation.Plan.Id);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(FileCrossVolumeMoveTerminalState.Succeeded, persisted.TerminalState);
        Assert.AreEqual(FileCrossVolumeMoveEntryState.Moved, persisted.Entries[0].State);
        Assert.AreEqual(FileCrossVolumeMoveEntryState.Skipped, persisted.Entries[1].State);
        Assert.IsNull(persisted.Entries[1].DestinationIdentity);
        Assert.IsNull(persisted.Entries[1].DestinationContentFingerprint);
    }

    [TestMethod]
    public async Task DestinationCommittedCanSettleCancelledWithSourceRetained()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation(includeSkippedEntry: false);
        var destinationIdentity = new FileIdentity(2, 901);
        var fingerprint = CreateFingerprint('b');

        using var store = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.DatabasePath);
        await store.BeginAsync(validation, DateTimeOffset.UtcNow);
        await store.MarkCopyMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);
        await store.CommitDestinationAsync(
            validation.Plan.Id,
            0,
            destinationIdentity,
            fingerprint,
            DateTimeOffset.UtcNow);

        var cancelled = await store.CompleteAsync(
            validation.Plan.Id,
            FileCrossVolumeMoveTerminalState.Cancelled,
            DateTimeOffset.UtcNow);

        Assert.AreEqual(FileCrossVolumeMoveTerminalState.Cancelled, cancelled.TerminalState);
        Assert.AreEqual(FileCrossVolumeMoveEntryState.DestinationCommitted, cancelled.Entries[0].State);
        Assert.IsTrue(cancelled.HasRetainedSourceDuplicates);
        Assert.IsFalse(cancelled.RequiresRecovery);
    }

    [TestMethod]
    public async Task SafeFailureAfterDestinationCommitRetainsCommittedReplacementEvidence()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation(includeSkippedEntry: false);
        var destinationIdentity = new FileIdentity(2, 902);
        var fingerprint = CreateFingerprint('c');
        var failure = new FileOperationFailure(
            "SourceDeletePreparationFailed",
            "The original source could not be safely reacquired.",
            validation.Items[0].Source.CanonicalPath,
            Retryable: true);

        using var store = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.DatabasePath);
        await store.BeginAsync(validation, DateTimeOffset.UtcNow);
        await store.MarkCopyMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);
        await store.CommitDestinationAsync(
            validation.Plan.Id,
            0,
            destinationIdentity,
            fingerprint,
            DateTimeOffset.UtcNow);

        var failed = await store.MarkEntryFailedAsync(
            validation.Plan.Id,
            0,
            failure,
            DateTimeOffset.UtcNow);
        var terminal = await store.CompleteAsync(
            validation.Plan.Id,
            FileCrossVolumeMoveTerminalState.Failed,
            DateTimeOffset.UtcNow);

        Assert.AreEqual(FileCrossVolumeMoveTerminalState.Failed, terminal.TerminalState);
        Assert.AreEqual(FileCrossVolumeMoveEntryState.Failed, failed.Entries[0].State);
        Assert.AreEqual(destinationIdentity, failed.Entries[0].DestinationIdentity);
        Assert.AreEqual(fingerprint, failed.Entries[0].DestinationContentFingerprint);
        Assert.IsTrue(failed.HasRetainedSourceDuplicates);
        Assert.IsFalse(failed.RequiresRecovery);
    }

    [TestMethod]
    public async Task SourceDeleteBarrierCannotDowngradeToSafeFailure()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation(includeSkippedEntry: false);
        var destinationIdentity = new FileIdentity(2, 903);
        var fingerprint = CreateFingerprint('d');
        var failure = new FileOperationFailure(
            "SourceDeleteFailed",
            "Deletion failed after the durable source-delete barrier.",
            validation.Items[0].Source.CanonicalPath,
            Retryable: false);

        using var store = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.DatabasePath);
        await store.BeginAsync(validation, DateTimeOffset.UtcNow);
        await store.MarkCopyMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);
        await store.CommitDestinationAsync(
            validation.Plan.Id,
            0,
            destinationIdentity,
            fingerprint,
            DateTimeOffset.UtcNow);
        await store.MarkSourceDeleteStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await store.MarkEntryFailedAsync(
                validation.Plan.Id,
                0,
                failure,
                DateTimeOffset.UtcNow));

        var recovery = await store.MarkRecoveryRequiredAsync(
            validation.Plan.Id,
            0,
            failure,
            DateTimeOffset.UtcNow);
        var terminal = await store.CompleteAsync(
            validation.Plan.Id,
            FileCrossVolumeMoveTerminalState.RecoveryRequired,
            DateTimeOffset.UtcNow);

        Assert.AreEqual(FileCrossVolumeMoveEntryState.RecoveryRequired, recovery.Entries[0].State);
        Assert.AreEqual(destinationIdentity, recovery.Entries[0].DestinationIdentity);
        Assert.AreEqual(fingerprint, recovery.Entries[0].DestinationContentFingerprint);
        Assert.AreEqual(FileCrossVolumeMoveTerminalState.RecoveryRequired, terminal.TerminalState);
        Assert.IsTrue(terminal.RequiresRecovery);
    }

    [TestMethod]
    public async Task RecoveryCannotReplaceAlreadyCommittedDestinationEvidence()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation(includeSkippedEntry: false);
        var destinationIdentity = new FileIdentity(2, 904);
        var fingerprint = CreateFingerprint('e');
        var failure = new FileOperationFailure(
            "SourceDeleteFailed",
            "fixture",
            validation.Items[0].Source.CanonicalPath,
            Retryable: false);

        using var store = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.DatabasePath);
        await store.BeginAsync(validation, DateTimeOffset.UtcNow);
        await store.MarkCopyMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);
        await store.CommitDestinationAsync(
            validation.Plan.Id,
            0,
            destinationIdentity,
            fingerprint,
            DateTimeOffset.UtcNow);
        await store.MarkSourceDeleteStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await store.MarkRecoveryRequiredAsync(
                validation.Plan.Id,
                0,
                failure,
                DateTimeOffset.UtcNow,
                destinationIdentity: new FileIdentity(2, 999),
                destinationContentFingerprint: CreateFingerprint('f')));

        var stillStarted = await store.GetAsync(validation.Plan.Id);
        Assert.IsNotNull(stillStarted);
        Assert.AreEqual(
            FileCrossVolumeMoveEntryState.SourceDeleteStarted,
            stillStarted.Entries[0].State);
        Assert.AreEqual(destinationIdentity, stillStarted.Entries[0].DestinationIdentity);
        Assert.AreEqual(fingerprint, stillStarted.Entries[0].DestinationContentFingerprint);
    }

    [TestMethod]
    public async Task BeginRejectsSameVolumeRootsAndDuplicateOperationId()
    {
        using var fixture = new HistoryFixture();
        using var store = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.DatabasePath);

        var sameVolume = CreateValidation(includeSkippedEntry: false, destinationVolume: 1);
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await store.BeginAsync(sameVolume, DateTimeOffset.UtcNow));
        Assert.IsNull(await store.GetAsync(sameVolume.Plan.Id));

        var valid = CreateValidation(includeSkippedEntry: false);
        await store.BeginAsync(valid, DateTimeOffset.UtcNow);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await store.BeginAsync(valid, DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public async Task AllSkippedCrossVolumeMoveCanSucceedWithoutMutationEvidence()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation(includeSkippedEntry: false, firstDecisionSkip: true);
        using var store = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.DatabasePath);

        var begun = await store.BeginAsync(validation, DateTimeOffset.UtcNow);
        Assert.AreEqual(FileCrossVolumeMoveEntryState.Skipped, begun.Entries[0].State);
        Assert.IsNull(begun.Entries[0].DestinationIdentity);
        Assert.IsNull(begun.Entries[0].DestinationContentFingerprint);

        var completed = await store.CompleteAsync(
            validation.Plan.Id,
            FileCrossVolumeMoveTerminalState.Succeeded,
            DateTimeOffset.UtcNow);
        Assert.AreEqual(FileCrossVolumeMoveTerminalState.Succeeded, completed.TerminalState);
        Assert.IsFalse(completed.RequiresRecovery);
        Assert.IsFalse(completed.HasRetainedSourceDuplicates);
    }

    private static FileContentFingerprint CreateFingerprint(char character) =>
        new(FileContentFingerprintAlgorithm.Sha256, new string(character, 64));

    private static FileOperationExecutionValidationResult CreateValidation(
        bool includeSkippedEntry,
        ulong destinationVolume = 2,
        bool firstDecisionSkip = false)
    {
        var sourceDirectory = Path.GetFullPath(@"C:\Source");
        var destinationDirectory = Path.GetFullPath(@"D:\Destination");
        var canonicalSourceDirectory = Path.GetFullPath(@"C:\Real\Source");
        var canonicalDestinationDirectory = Path.GetFullPath(@"D:\Real\Destination");
        var first = new FileOperationEntry(
            Path.Combine(sourceDirectory, "a.txt"),
            "a.txt",
            IsDirectory: false);
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
            Guid.NewGuid(),
            new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero),
            FileOperationKind.Move,
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
                var skip = ordinal > 0 || firstDecisionSkip;
                return new FileOperationExecutionValidationItem(
                    entry,
                    new FileOperationCanonicalPath(
                        entry.Path,
                        Path.Combine(canonicalSourceDirectory, entry.Name),
                        FileOperationCanonicalPathState.File,
                        IsLeafReparsePoint: false,
                        Identity: new FileIdentity(1, (ulong)(100 + ordinal))),
                    skip
                        ? new FileOperationCanonicalPath(
                            Path.Combine(destinationDirectory, entry.Name),
                            Path.Combine(canonicalDestinationDirectory, entry.Name),
                            FileOperationCanonicalPathState.File,
                            IsLeafReparsePoint: false,
                            Identity: new FileIdentity(destinationVolume, (ulong)(200 + ordinal)))
                        : new FileOperationCanonicalPath(
                            Path.Combine(destinationDirectory, entry.Name),
                            Path.Combine(canonicalDestinationDirectory, entry.Name),
                            FileOperationCanonicalPathState.Missing,
                            IsLeafReparsePoint: false),
                    skip
                        ? FileOperationExecutionValidationDecision.Skip
                        : FileOperationExecutionValidationDecision.Ready,
                    skip ? "skip existing" : "ready");
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
                Identity: new FileIdentity(destinationVolume, 20)),
            items,
            FileOperationExecutionValidationStatus.Ready,
            new DateTimeOffset(2026, 8, 14, 0, 1, 0, TimeSpan.Zero),
            "ready");
    }

    private sealed class HistoryFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "FileOp.CrossVolumeMoveHistory.Tests",
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
