using System;
using System.IO;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileOperationActionHistoryNamedDataStreamTopologyEvidenceTests
{
    private static readonly FileContentFingerprint Fingerprint = new(
        FileContentFingerprintAlgorithm.Sha256,
        "abababababababababababababababababababababababababababababababab");
    private static readonly FileCopyDestinationCommitNamedDataStreamTopologyEvidence Evidence = new(
        new FileCopyDestinationCommitSecurityDescriptorEvidence(
            new FileCopyDestinationCommitBasicMetadataEvidence(
                2,
                new FileBasicMetadataEvidence(100, 200, 300, 0x2021)),
            new FileSecurityDescriptorEvidence(
                FileSecurityDescriptorEvidence.QueriedSecurityInformationMask,
                "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd")),
        new FileNamedDataStreamTopologyEvidence(1, 2,
            "efefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefef"));

    [TestMethod]
    public async Task StrongCommitPersistsAllEvidenceAcrossReopen()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation();
        using (var store = new SqliteFileOperationActionHistoryNamedDataStreamTopologyEvidenceStore(fixture.DatabasePath))
        {
            await store.BeginAsync(validation, DateTimeOffset.UtcNow);
            await store.MarkMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);
            var committed = await store.CommitCopyWithNamedDataStreamTopologyEvidenceAsync(
                validation.Plan.Id,
                0,
                new FileIdentity(7, 70),
                Fingerprint,
                Evidence,
                DateTimeOffset.UtcNow);

            Assert.AreEqual(FileOperationActionEntryState.Committed, committed.Entries[0].State);
            Assert.AreEqual(2u, committed.Entries[0].DestinationHardLinkCount);
        }

        using var reopened = new SqliteFileOperationActionHistoryNamedDataStreamTopologyEvidenceStore(fixture.DatabasePath);
        Assert.AreEqual(Evidence.NamedDataStreamTopology,
            await reopened.GetDestinationNamedDataStreamTopologyEvidenceAsync(validation.Plan.Id, 0));
        Assert.AreEqual(Evidence.SecurityDescriptor,
            await reopened.GetDestinationSecurityDescriptorEvidenceAsync(validation.Plan.Id, 0));
        Assert.AreEqual(Evidence.BasicMetadata,
            await reopened.GetDestinationBasicMetadataEvidenceAsync(validation.Plan.Id, 0));
    }

    [TestMethod]
    public async Task StrongRecoveryPersistsTopologyWithoutUndoAuthority()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation();
        using var store = new SqliteFileOperationActionHistoryNamedDataStreamTopologyEvidenceStore(fixture.DatabasePath);
        await store.BeginAsync(validation, DateTimeOffset.UtcNow);
        await store.MarkMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);

        var recovery = await store.MarkMutationRecoveryRequiredWithNamedDataStreamTopologyEvidenceAsync(
            validation.Plan.Id,
            0,
            new FileOperationFailure("CommitFailed", "fixture", validation.Items[0].Destination.CanonicalPath, false),
            DateTimeOffset.UtcNow,
            new FileIdentity(8, 80),
            Fingerprint,
            Evidence);

        Assert.AreEqual(FileOperationActionEntryState.RecoveryRequired, recovery.Entries[0].State);
        Assert.AreEqual(FileOperationUndoKind.None, recovery.Entries[0].UndoKind);
        Assert.IsFalse(recovery.Entries[0].IsUndoCandidate);
        Assert.AreEqual(Evidence.NamedDataStreamTopology,
            await store.GetDestinationNamedDataStreamTopologyEvidenceAsync(validation.Plan.Id, 0));
    }

    [TestMethod]
    public async Task SecurityOnlyCommitLoadsWithNoNamedStreamTopologyEvidence()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation();
        using var store = new SqliteFileOperationActionHistoryNamedDataStreamTopologyEvidenceStore(fixture.DatabasePath);
        await store.BeginAsync(validation, DateTimeOffset.UtcNow);
        await store.MarkMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);
        await store.CommitCopyWithSecurityDescriptorEvidenceAsync(
            validation.Plan.Id,
            0,
            new FileIdentity(9, 90),
            Fingerprint,
            Evidence.SecurityDescriptorEvidence,
            DateTimeOffset.UtcNow);

        Assert.IsNull(await store.GetDestinationNamedDataStreamTopologyEvidenceAsync(validation.Plan.Id, 0));
    }

    [TestMethod]
    public async Task TopologyInsertFailureRollsBackStateAndAllEarlierEvidenceRows()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation();
        using var store = new SqliteFileOperationActionHistoryNamedDataStreamTopologyEvidenceStore(fixture.DatabasePath);
        await store.BeginAsync(validation, DateTimeOffset.UtcNow);
        await store.MarkMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);

        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection($"Data Source={fixture.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var trigger = connection.CreateCommand();
            trigger.CommandText = """
                CREATE TRIGGER fail_named_stream_topology
                BEFORE INSERT ON file_operation_action_entry_named_data_stream_topology_evidence
                BEGIN
                    SELECT RAISE(ABORT, 'fixture named-stream persistence failure');
                END;
                """;
            await trigger.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await store.CommitCopyWithNamedDataStreamTopologyEvidenceAsync(
                validation.Plan.Id,
                0,
                new FileIdentity(10, 100),
                Fingerprint,
                Evidence,
                DateTimeOffset.UtcNow));

        var history = await store.GetAsync(validation.Plan.Id);
        Assert.IsNotNull(history);
        Assert.AreEqual(FileOperationActionEntryState.MutationStarted, history.Entries[0].State);
        Assert.IsNull(history.Entries[0].DestinationIdentity);
        Assert.IsNull(history.Entries[0].DestinationContentFingerprint);
        Assert.IsNull(history.Entries[0].DestinationHardLinkCount);
        Assert.IsNull(await store.GetDestinationBasicMetadataEvidenceAsync(validation.Plan.Id, 0));
        Assert.IsNull(await store.GetDestinationSecurityDescriptorEvidenceAsync(validation.Plan.Id, 0));
        Assert.IsNull(await store.GetDestinationNamedDataStreamTopologyEvidenceAsync(validation.Plan.Id, 0));

        await using var verify = new SqliteConnection($"Data Source={fixture.DatabasePath}");
        await verify.OpenAsync();
        foreach (var table in new[]
        {
            "file_operation_action_entry_content_fingerprints",
            "file_operation_action_entry_hard_link_evidence",
            "file_operation_action_entry_basic_metadata_evidence",
            "file_operation_action_entry_security_descriptor_evidence",
            "file_operation_action_entry_named_data_stream_topology_evidence",
        })
        {
            await using var command = verify.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE operation_id = $operation";
            command.Parameters.AddWithValue("$operation", validation.Plan.Id.ToString("D"));
            Assert.AreEqual(0L, (long)(await command.ExecuteScalarAsync())!);
        }
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
            new FileOperationIntent(
                "Left", Guid.NewGuid(), sourceDirectory, new[] { entry },
                "Right", Guid.NewGuid(), destinationDirectory));
        return new FileOperationExecutionValidationResult(
            plan,
            new FileOperationCanonicalPath(
                sourceDirectory, canonicalSourceDirectory,
                FileOperationCanonicalPathState.Directory, false, new FileIdentity(4, 40)),
            new FileOperationCanonicalPath(
                destinationDirectory, canonicalDestinationDirectory,
                FileOperationCanonicalPathState.Directory, false, new FileIdentity(5, 50)),
            new[]
            {
                new FileOperationExecutionValidationItem(
                    entry,
                    new FileOperationCanonicalPath(
                        entry.Path, Path.Combine(canonicalSourceDirectory, entry.Name),
                        FileOperationCanonicalPathState.File, false, new FileIdentity(3, 30)),
                    new FileOperationCanonicalPath(
                        Path.Combine(destinationDirectory, entry.Name),
                        Path.Combine(canonicalDestinationDirectory, entry.Name),
                        FileOperationCanonicalPathState.Missing, false),
                    FileOperationExecutionValidationDecision.Ready,
                    "ready"),
            },
            FileOperationExecutionValidationStatus.Ready,
            DateTimeOffset.UtcNow,
            "ready");
    }

    private sealed class HistoryFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "FileOp.ActionHistory.NamedStreams.Tests",
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
            try { Directory.Delete(_directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
