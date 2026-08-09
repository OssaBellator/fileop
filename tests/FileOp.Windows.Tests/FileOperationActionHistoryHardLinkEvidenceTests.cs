using System;
using System.IO;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileOperationActionHistoryHardLinkEvidenceTests
{
    private static readonly FileContentFingerprint Fingerprint = new(
        FileContentFingerprintAlgorithm.Sha256,
        "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd");

    [TestMethod]
    public async Task TopologyAwareCommitPersistsHardLinkCountAcrossReopen()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation();
        using (var store = new SqliteFileOperationActionHistoryHardLinkEvidenceStore(fixture.DatabasePath))
        {
            await store.BeginAsync(validation, DateTimeOffset.UtcNow);
            await store.MarkMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);
            var committed = await store.CommitCopyWithHardLinkEvidenceAsync(
                validation.Plan.Id,
                0,
                new FileIdentity(7, ulong.MaxValue - 3),
                Fingerprint,
                destinationHardLinkCount: 3,
                DateTimeOffset.UtcNow);

            Assert.AreEqual(FileOperationActionEntryState.Committed, committed.Entries[0].State);
            Assert.AreEqual(3u, committed.Entries[0].DestinationHardLinkCount);
            Assert.AreEqual(Fingerprint, committed.Entries[0].DestinationContentFingerprint);
            Assert.AreEqual(FileOperationUndoKind.DeleteCreatedDestination, committed.Entries[0].UndoKind);
        }

        using var reopened = new SqliteFileOperationActionHistoryHardLinkEvidenceStore(fixture.DatabasePath);
        var persisted = await reopened.GetAsync(validation.Plan.Id);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(3u, persisted.Entries[0].DestinationHardLinkCount);
        Assert.AreEqual(Fingerprint, persisted.Entries[0].DestinationContentFingerprint);
    }

    [TestMethod]
    public async Task TopologyAwareRecoveryPersistsHardLinkCountWithoutUndoCandidate()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation();
        using var store = new SqliteFileOperationActionHistoryHardLinkEvidenceStore(fixture.DatabasePath);
        await store.BeginAsync(validation, DateTimeOffset.UtcNow);
        await store.MarkMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);
        var failure = new FileOperationFailure(
            "CopyCommitBarrierFailed",
            "fixture",
            validation.Items[0].Destination.CanonicalPath,
            Retryable: false);

        var recovery = await store.MarkMutationRecoveryRequiredWithHardLinkEvidenceAsync(
            validation.Plan.Id,
            0,
            failure,
            DateTimeOffset.UtcNow,
            new FileIdentity(8, ulong.MaxValue - 5),
            Fingerprint,
            destinationHardLinkCount: 2);

        Assert.AreEqual(FileOperationActionEntryState.RecoveryRequired, recovery.Entries[0].State);
        Assert.AreEqual(2u, recovery.Entries[0].DestinationHardLinkCount);
        Assert.AreEqual(FileOperationUndoKind.None, recovery.Entries[0].UndoKind);
        Assert.IsFalse(recovery.Entries[0].IsUndoCandidate);
        Assert.AreEqual(failure, recovery.Entries[0].Failure);
    }

    [TestMethod]
    public async Task LegacyCommittedRowLoadsWithNoHardLinkEvidence()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation();
        using (var legacy = new SqliteFileOperationActionHistoryStore(fixture.DatabasePath))
        {
            await legacy.BeginAsync(validation, DateTimeOffset.UtcNow);
            await legacy.MarkMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);
            await legacy.CommitCopyAsync(
                validation.Plan.Id,
                0,
                new FileIdentity(9, 90),
                Fingerprint,
                DateTimeOffset.UtcNow);
        }

        using var topology = new SqliteFileOperationActionHistoryHardLinkEvidenceStore(fixture.DatabasePath);
        var persisted = await topology.GetAsync(validation.Plan.Id);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(FileOperationActionEntryState.Committed, persisted.Entries[0].State);
        Assert.IsNull(persisted.Entries[0].DestinationHardLinkCount);
    }

    [TestMethod]
    public async Task HardLinkEvidenceInsertFailureRollsBackCommitAndFingerprint()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation();
        using var store = new SqliteFileOperationActionHistoryHardLinkEvidenceStore(fixture.DatabasePath);
        await store.BeginAsync(validation, DateTimeOffset.UtcNow);
        await store.MarkMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);

        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection($"Data Source={fixture.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var trigger = connection.CreateCommand();
            trigger.CommandText = """
                CREATE TRIGGER fail_hard_link_evidence
                BEFORE INSERT ON file_operation_action_entry_hard_link_evidence
                BEGIN
                    SELECT RAISE(ABORT, 'fixture hard-link persistence failure');
                END;
                """;
            await trigger.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsExceptionAsync<SqliteException>(async () =>
            await store.CommitCopyWithHardLinkEvidenceAsync(
                validation.Plan.Id,
                0,
                new FileIdentity(10, 100),
                Fingerprint,
                destinationHardLinkCount: 1,
                DateTimeOffset.UtcNow));

        var persisted = await store.GetAsync(validation.Plan.Id);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(FileOperationActionEntryState.MutationStarted, persisted.Entries[0].State);
        Assert.IsNull(persisted.Entries[0].DestinationIdentity);
        Assert.IsNull(persisted.Entries[0].DestinationContentFingerprint);
        Assert.IsNull(persisted.Entries[0].DestinationHardLinkCount);
        Assert.AreEqual(FileOperationUndoKind.None, persisted.Entries[0].UndoKind);
    }

    [TestMethod]
    public async Task ZeroHardLinkCountIsRejectedBeforeTransition()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation();
        using var store = new SqliteFileOperationActionHistoryHardLinkEvidenceStore(fixture.DatabasePath);
        await store.BeginAsync(validation, DateTimeOffset.UtcNow);
        await store.MarkMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);

        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(async () =>
            await store.CommitCopyWithHardLinkEvidenceAsync(
                validation.Plan.Id,
                0,
                new FileIdentity(11, 110),
                Fingerprint,
                destinationHardLinkCount: 0,
                DateTimeOffset.UtcNow));

        var persisted = await store.GetAsync(validation.Plan.Id);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(FileOperationActionEntryState.MutationStarted, persisted.Entries[0].State);
    }

    private static FileOperationExecutionValidationResult CreateValidation()
    {
        var sourceDirectory = Path.GetFullPath(@"C:\Source");
        var destinationDirectory = Path.GetFullPath(@"D:\Destination");
        var canonicalSourceDirectory = Path.GetFullPath(@"C:\Real\Source");
        var canonicalDestinationDirectory = Path.GetFullPath(@"D:\Real\Destination");
        var entry = new FileOperationEntry(
            Path.Combine(sourceDirectory, "a.txt"),
            "a.txt",
            IsDirectory: false);
        var plan = new FileOperationPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            FileOperationKind.Copy,
            FileOperationCollisionPolicy.Stop,
            new FileOperationIntent(
                "Left",
                Guid.NewGuid(),
                sourceDirectory,
                new[] { entry },
                "Right",
                Guid.NewGuid(),
                destinationDirectory));
        var item = new FileOperationExecutionValidationItem(
            entry,
            new FileOperationCanonicalPath(
                entry.Path,
                Path.Combine(canonicalSourceDirectory, entry.Name),
                FileOperationCanonicalPathState.File,
                IsLeafReparsePoint: false,
                Identity: new FileIdentity(3, 30)),
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
                Identity: new FileIdentity(4, 40)),
            new FileOperationCanonicalPath(
                destinationDirectory,
                canonicalDestinationDirectory,
                FileOperationCanonicalPathState.Directory,
                IsLeafReparsePoint: false,
                Identity: new FileIdentity(5, 50)),
            new[] { item },
            FileOperationExecutionValidationStatus.Ready,
            DateTimeOffset.UtcNow,
            "ready");
    }

    private sealed class HistoryFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "FileOp.ActionHistory.HardLink.Tests",
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
            try
            {
                Directory.Delete(_directory, recursive: true);
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
