using System;
using System.IO;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileOperationActionHistoryBasicMetadataEvidenceTests
{
    private static readonly FileContentFingerprint Fingerprint = new(
        FileContentFingerprintAlgorithm.Sha256,
        "abababababababababababababababababababababababababababababababab");

    [TestMethod]
    public async Task CombinedCommitPersistsMetadataAndHardLinkEvidenceAcrossReopen()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation();
        var metadata = new FileBasicMetadataEvidence(
            ulong.MaxValue - 11,
            ulong.MaxValue - 13,
            ulong.MaxValue - 17,
            0x00002027u | 0x00001000u);
        var evidence = new FileCopyDestinationCommitBasicMetadataEvidence(3, metadata);

        using (var store = new SqliteFileOperationActionHistoryBasicMetadataEvidenceStore(fixture.DatabasePath))
        {
            await store.BeginAsync(validation, DateTimeOffset.UtcNow);
            await store.MarkMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);
            var committed = await store.CommitCopyWithBasicMetadataEvidenceAsync(
                validation.Plan.Id,
                0,
                new FileIdentity(7, ulong.MaxValue - 3),
                Fingerprint,
                evidence,
                DateTimeOffset.UtcNow);

            Assert.AreEqual(FileOperationActionEntryState.Committed, committed.Entries[0].State);
            Assert.AreEqual(3u, committed.Entries[0].DestinationHardLinkCount);
        }

        using var reopened = new SqliteFileOperationActionHistoryBasicMetadataEvidenceStore(fixture.DatabasePath);
        var history = await reopened.GetAsync(validation.Plan.Id);
        var persisted = await reopened.GetDestinationBasicMetadataEvidenceAsync(validation.Plan.Id, 0);
        Assert.IsNotNull(history);
        Assert.AreEqual(3u, history.Entries[0].DestinationHardLinkCount);
        Assert.AreEqual(metadata, persisted);
    }

    [TestMethod]
    public async Task CombinedRecoveryPersistsMetadataWithoutUndoAuthority()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation();
        var metadata = new FileBasicMetadataEvidence(100, 200, 300, 0x00000022u);
        using var store = new SqliteFileOperationActionHistoryBasicMetadataEvidenceStore(fixture.DatabasePath);
        await store.BeginAsync(validation, DateTimeOffset.UtcNow);
        await store.MarkMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);

        var recovery = await store.MarkMutationRecoveryRequiredWithBasicMetadataEvidenceAsync(
            validation.Plan.Id,
            0,
            new FileOperationFailure("CopyCommitBarrierFailed", "fixture", validation.Items[0].Destination.CanonicalPath, false),
            DateTimeOffset.UtcNow,
            new FileIdentity(8, 80),
            Fingerprint,
            new FileCopyDestinationCommitBasicMetadataEvidence(2, metadata));

        Assert.AreEqual(FileOperationActionEntryState.RecoveryRequired, recovery.Entries[0].State);
        Assert.AreEqual(FileOperationUndoKind.None, recovery.Entries[0].UndoKind);
        Assert.IsFalse(recovery.Entries[0].IsUndoCandidate);
        Assert.AreEqual(2u, recovery.Entries[0].DestinationHardLinkCount);
        Assert.AreEqual(metadata, await store.GetDestinationBasicMetadataEvidenceAsync(validation.Plan.Id, 0));
    }

    [TestMethod]
    public async Task HardLinkOnlyCommitLoadsWithNoBasicMetadataEvidence()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation();
        using var store = new SqliteFileOperationActionHistoryBasicMetadataEvidenceStore(fixture.DatabasePath);
        await store.BeginAsync(validation, DateTimeOffset.UtcNow);
        await store.MarkMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);
        await store.CommitCopyWithHardLinkEvidenceAsync(
            validation.Plan.Id,
            0,
            new FileIdentity(9, 90),
            Fingerprint,
            1,
            DateTimeOffset.UtcNow);

        Assert.IsNull(await store.GetDestinationBasicMetadataEvidenceAsync(validation.Plan.Id, 0));
    }

    [TestMethod]
    public async Task MetadataInsertFailureRollsBackStateFingerprintAndHardLinkEvidence()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation();
        using var store = new SqliteFileOperationActionHistoryBasicMetadataEvidenceStore(fixture.DatabasePath);
        await store.BeginAsync(validation, DateTimeOffset.UtcNow);
        await store.MarkMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);

        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection($"Data Source={fixture.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var trigger = connection.CreateCommand();
            trigger.CommandText = """
                CREATE TRIGGER fail_basic_metadata_evidence
                BEFORE INSERT ON file_operation_action_entry_basic_metadata_evidence
                BEGIN
                    SELECT RAISE(ABORT, 'fixture metadata persistence failure');
                END;
                """;
            await trigger.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await store.CommitCopyWithBasicMetadataEvidenceAsync(
                validation.Plan.Id,
                0,
                new FileIdentity(10, 100),
                Fingerprint,
                new FileCopyDestinationCommitBasicMetadataEvidence(
                    1,
                    new FileBasicMetadataEvidence(100, 200, 300, 0x20)),
                DateTimeOffset.UtcNow));

        var history = await store.GetAsync(validation.Plan.Id);
        Assert.IsNotNull(history);
        Assert.AreEqual(FileOperationActionEntryState.MutationStarted, history.Entries[0].State);
        Assert.IsNull(history.Entries[0].DestinationIdentity);
        Assert.IsNull(history.Entries[0].DestinationContentFingerprint);
        Assert.IsNull(history.Entries[0].DestinationHardLinkCount);
        Assert.IsNull(await store.GetDestinationBasicMetadataEvidenceAsync(validation.Plan.Id, 0));
    }

    [TestMethod]
    public void CombinedEvidenceRejectsZeroHardLinkCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FileCopyDestinationCommitBasicMetadataEvidence(
                0,
                new FileBasicMetadataEvidence(1, 2, 3, 4)));
    }

    private static FileOperationExecutionValidationResult CreateValidation()
    {
        var sourceDirectory = Path.GetFullPath(@"C:\Source");
        var destinationDirectory = Path.GetFullPath(@"D:\Destination");
        var canonicalSourceDirectory = Path.GetFullPath(@"C:\Real\Source");
        var canonicalDestinationDirectory = Path.GetFullPath(@"D:\Real\Destination");
        var entry = new FileOperationEntry(Path.Combine(sourceDirectory, "a.txt"), "a.txt", false);
        var plan = new FileOperationPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            FileOperationKind.Copy,
            FileOperationCollisionPolicy.Stop,
            new FileOperationIntent("Left", Guid.NewGuid(), sourceDirectory, new[] { entry }, "Right", Guid.NewGuid(), destinationDirectory));
        return new FileOperationExecutionValidationResult(
            plan,
            new FileOperationCanonicalPath(sourceDirectory, canonicalSourceDirectory, FileOperationCanonicalPathState.Directory, false, new FileIdentity(4, 40)),
            new FileOperationCanonicalPath(destinationDirectory, canonicalDestinationDirectory, FileOperationCanonicalPathState.Directory, false, new FileIdentity(5, 50)),
            new[]
            {
                new FileOperationExecutionValidationItem(
                    entry,
                    new FileOperationCanonicalPath(entry.Path, Path.Combine(canonicalSourceDirectory, entry.Name), FileOperationCanonicalPathState.File, false, new FileIdentity(3, 30)),
                    new FileOperationCanonicalPath(Path.Combine(destinationDirectory, entry.Name), Path.Combine(canonicalDestinationDirectory, entry.Name), FileOperationCanonicalPathState.Missing, false),
                    FileOperationExecutionValidationDecision.Ready,
                    "ready"),
            },
            FileOperationExecutionValidationStatus.Ready,
            DateTimeOffset.UtcNow,
            "ready");
    }

    private sealed class HistoryFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "FileOp.ActionHistory.BasicMetadata.Tests", Guid.NewGuid().ToString("N"));
        public HistoryFixture() { Directory.CreateDirectory(_directory); DatabasePath = Path.Combine(_directory, "actions.sqlite"); }
        public string DatabasePath { get; }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
