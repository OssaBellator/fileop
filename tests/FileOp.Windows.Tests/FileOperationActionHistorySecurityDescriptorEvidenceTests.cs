using System;
using System.IO;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileOperationActionHistorySecurityDescriptorEvidenceTests
{
    private static readonly FileContentFingerprint Fingerprint = new(
        FileContentFingerprintAlgorithm.Sha256,
        "abababababababababababababababababababababababababababababababab");
    private static readonly FileSecurityDescriptorEvidence Security = new(
        FileSecurityDescriptorEvidence.QueriedSecurityInformationMask,
        "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd");

    [TestMethod]
    public async Task SecurityAwareCommitPersistsAllEvidenceAcrossReopen()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation();
        var metadata = new FileBasicMetadataEvidence(
            ulong.MaxValue - 11,
            ulong.MaxValue - 13,
            ulong.MaxValue - 17,
            0x00002027u);
        var evidence = Combined(3, metadata);

        using (var store = new SqliteFileOperationActionHistorySecurityDescriptorEvidenceStore(fixture.DatabasePath))
        {
            await store.BeginAsync(validation, DateTimeOffset.UtcNow);
            await store.MarkMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);
            var committed = await store.CommitCopyWithSecurityDescriptorEvidenceAsync(
                validation.Plan.Id,
                0,
                new FileIdentity(7, ulong.MaxValue - 3),
                Fingerprint,
                evidence,
                DateTimeOffset.UtcNow);

            Assert.AreEqual(FileOperationActionEntryState.Committed, committed.Entries[0].State);
            Assert.AreEqual(3u, committed.Entries[0].DestinationHardLinkCount);
        }

        using var reopened = new SqliteFileOperationActionHistorySecurityDescriptorEvidenceStore(fixture.DatabasePath);
        var history = await reopened.GetAsync(validation.Plan.Id);
        Assert.IsNotNull(history);
        Assert.AreEqual(3u, history.Entries[0].DestinationHardLinkCount);
        Assert.AreEqual(metadata, await reopened.GetDestinationBasicMetadataEvidenceAsync(validation.Plan.Id, 0));
        Assert.AreEqual(Security, await reopened.GetDestinationSecurityDescriptorEvidenceAsync(validation.Plan.Id, 0));
    }

    [TestMethod]
    public async Task SecurityAwareRecoveryPersistsEvidenceWithoutUndoAuthority()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation();
        using var store = new SqliteFileOperationActionHistorySecurityDescriptorEvidenceStore(fixture.DatabasePath);
        await store.BeginAsync(validation, DateTimeOffset.UtcNow);
        await store.MarkMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);

        var recovery = await store.MarkMutationRecoveryRequiredWithSecurityDescriptorEvidenceAsync(
            validation.Plan.Id,
            0,
            new FileOperationFailure("CopyCommitBarrierFailed", "fixture", validation.Items[0].Destination.CanonicalPath, false),
            DateTimeOffset.UtcNow,
            new FileIdentity(8, 80),
            Fingerprint,
            Combined(2, new FileBasicMetadataEvidence(100, 200, 300, 0x22)));

        Assert.AreEqual(FileOperationActionEntryState.RecoveryRequired, recovery.Entries[0].State);
        Assert.AreEqual(FileOperationUndoKind.None, recovery.Entries[0].UndoKind);
        Assert.IsFalse(recovery.Entries[0].IsUndoCandidate);
        Assert.AreEqual(Security, await store.GetDestinationSecurityDescriptorEvidenceAsync(validation.Plan.Id, 0));
    }

    [TestMethod]
    public async Task BasicMetadataOnlyCommitLoadsWithNoSecurityEvidence()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation();
        using var store = new SqliteFileOperationActionHistorySecurityDescriptorEvidenceStore(fixture.DatabasePath);
        await store.BeginAsync(validation, DateTimeOffset.UtcNow);
        await store.MarkMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);
        await store.CommitCopyWithBasicMetadataEvidenceAsync(
            validation.Plan.Id,
            0,
            new FileIdentity(9, 90),
            Fingerprint,
            new FileCopyDestinationCommitBasicMetadataEvidence(
                1,
                new FileBasicMetadataEvidence(100, 200, 300, 0x20)),
            DateTimeOffset.UtcNow);

        Assert.IsNull(await store.GetDestinationSecurityDescriptorEvidenceAsync(validation.Plan.Id, 0));
    }

    [TestMethod]
    public async Task SecurityInsertFailureRollsBackStateAndAllEarlierEvidenceRows()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation();
        using var store = new SqliteFileOperationActionHistorySecurityDescriptorEvidenceStore(fixture.DatabasePath);
        await store.BeginAsync(validation, DateTimeOffset.UtcNow);
        await store.MarkMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);

        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection($"Data Source={fixture.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var trigger = connection.CreateCommand();
            trigger.CommandText = """
                CREATE TRIGGER fail_security_descriptor_evidence
                BEFORE INSERT ON file_operation_action_entry_security_descriptor_evidence
                BEGIN
                    SELECT RAISE(ABORT, 'fixture security persistence failure');
                END;
                """;
            await trigger.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await store.CommitCopyWithSecurityDescriptorEvidenceAsync(
                validation.Plan.Id,
                0,
                new FileIdentity(10, 100),
                Fingerprint,
                Combined(1, new FileBasicMetadataEvidence(100, 200, 300, 0x20)),
                DateTimeOffset.UtcNow));

        var history = await store.GetAsync(validation.Plan.Id);
        Assert.IsNotNull(history);
        Assert.AreEqual(FileOperationActionEntryState.MutationStarted, history.Entries[0].State);
        Assert.IsNull(history.Entries[0].DestinationIdentity);
        Assert.IsNull(history.Entries[0].DestinationContentFingerprint);
        Assert.IsNull(history.Entries[0].DestinationHardLinkCount);
        Assert.IsNull(await store.GetDestinationBasicMetadataEvidenceAsync(validation.Plan.Id, 0));
        Assert.IsNull(await store.GetDestinationSecurityDescriptorEvidenceAsync(validation.Plan.Id, 0));
    }

    [TestMethod]
    public void SecurityEvidenceRejectsSaclInclusiveMask()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FileSecurityDescriptorEvidence(
                0x0000000fu,
                "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd"));
    }

    private static FileCopyDestinationCommitSecurityDescriptorEvidence Combined(
        uint hardLinkCount,
        FileBasicMetadataEvidence metadata) =>
        new(
            new FileCopyDestinationCommitBasicMetadataEvidence(hardLinkCount, metadata),
            Security);

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
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "FileOp.ActionHistory.SecurityDescriptor.Tests", Guid.NewGuid().ToString("N"));
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
