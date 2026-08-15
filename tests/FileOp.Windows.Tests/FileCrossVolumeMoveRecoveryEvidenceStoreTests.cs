using System;
using System.IO;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileCrossVolumeMoveRecoveryEvidenceStoreTests
{
    [TestMethod]
    public async Task CopyBarrierRecoveryDestinationObservationDoesNotBecomeSafeCommit()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation();
        var destinationIdentity = new FileIdentity(2, 900);
        var fingerprint = Fingerprint('a');
        var failure = Failure(validation, "CopyCommitAmbiguous");

        using (var store = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.DatabasePath))
        {
            await store.BeginAsync(validation, DateTimeOffset.UtcNow);
            await store.MarkCopyMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);
            var recovery = await store.MarkRecoveryRequiredAsync(
                validation.Plan.Id,
                0,
                failure,
                DateTimeOffset.UtcNow,
                destinationIdentity,
                fingerprint);
            await store.CompleteAsync(
                validation.Plan.Id,
                FileCrossVolumeMoveTerminalState.RecoveryRequired,
                DateTimeOffset.UtcNow);

            Assert.AreEqual(FileCrossVolumeMoveEntryState.RecoveryRequired, recovery.Entries[0].State);
            Assert.IsTrue(recovery.Entries[0].HasDestinationRecoveryEvidence);
            Assert.IsFalse(recovery.Entries[0].DestinationIsDurablyCommitted);
            Assert.IsFalse(recovery.Entries[0].SourceDeleteBarrierMayBeUnresolved);
            Assert.IsFalse(recovery.HasRetainedSourceDuplicates);
        }

        using var reopened = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.DatabasePath);
        var persisted = await reopened.GetAsync(validation.Plan.Id);
        Assert.IsNotNull(persisted);
        Assert.IsTrue(persisted.RequiresRecovery);
        Assert.IsTrue(persisted.Entries[0].HasDestinationRecoveryEvidence);
        Assert.IsFalse(persisted.Entries[0].DestinationIsDurablyCommitted);
        Assert.IsFalse(persisted.Entries[0].SourceDeleteBarrierMayBeUnresolved);
        Assert.AreEqual(destinationIdentity, persisted.Entries[0].DestinationIdentity);
        Assert.AreEqual(fingerprint, persisted.Entries[0].DestinationContentFingerprint);
        Assert.IsFalse(persisted.HasRetainedSourceDuplicates);
    }

    [TestMethod]
    public async Task SourceDeleteRecoveryStillProvesEarlierSafeDestinationCommit()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation();
        var destinationIdentity = new FileIdentity(2, 901);
        var fingerprint = Fingerprint('b');
        var failure = Failure(validation, "SourceDeleteAmbiguous");

        using (var store = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.DatabasePath))
        {
            await store.BeginAsync(validation, DateTimeOffset.UtcNow);
            await store.MarkCopyMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);
            await store.CommitDestinationAsync(
                validation.Plan.Id,
                0,
                destinationIdentity,
                fingerprint,
                DateTimeOffset.UtcNow);
            await store.MarkSourceDeleteStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);
            var recovery = await store.MarkRecoveryRequiredAsync(
                validation.Plan.Id,
                0,
                failure,
                DateTimeOffset.UtcNow);
            await store.CompleteAsync(
                validation.Plan.Id,
                FileCrossVolumeMoveTerminalState.RecoveryRequired,
                DateTimeOffset.UtcNow);

            Assert.IsTrue(recovery.Entries[0].HasDestinationRecoveryEvidence);
            Assert.IsTrue(recovery.Entries[0].DestinationIsDurablyCommitted);
            Assert.IsTrue(recovery.Entries[0].SourceDeleteBarrierMayBeUnresolved);
            Assert.IsFalse(recovery.HasRetainedSourceDuplicates);
        }

        using var reopened = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.DatabasePath);
        var persisted = await reopened.GetAsync(validation.Plan.Id);
        Assert.IsNotNull(persisted);
        Assert.IsTrue(persisted.RequiresRecovery);
        Assert.IsTrue(persisted.Entries[0].HasDestinationRecoveryEvidence);
        Assert.IsTrue(persisted.Entries[0].DestinationIsDurablyCommitted);
        Assert.IsTrue(persisted.Entries[0].SourceDeleteBarrierMayBeUnresolved);
        Assert.IsNotNull(persisted.Entries[0].SourceDeleteStartedAtUtc);
        Assert.IsFalse(persisted.HasRetainedSourceDuplicates);
    }

    private static FileOperationFailure Failure(
        FileOperationExecutionValidationResult validation,
        string code) =>
        new(
            code,
            "recovery evidence fixture",
            validation.Items[0].Source.CanonicalPath,
            Retryable: false);

    private static FileContentFingerprint Fingerprint(char digit) =>
        new(
            FileContentFingerprintAlgorithm.Sha256,
            new string(digit, FileContentFingerprint.Sha256HexLength));

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
            new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero),
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
        var item = new FileOperationExecutionValidationItem(
            entry,
            new FileOperationCanonicalPath(
                entry.Path,
                Path.Combine(canonicalSourceDirectory, entry.Name),
                FileOperationCanonicalPathState.File,
                IsLeafReparsePoint: false,
                Identity: new FileIdentity(1, 100)),
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
                Identity: new FileIdentity(1, 10)),
            new FileOperationCanonicalPath(
                destinationDirectory,
                canonicalDestinationDirectory,
                FileOperationCanonicalPathState.Directory,
                IsLeafReparsePoint: false,
                Identity: new FileIdentity(2, 20)),
            new[] { item },
            FileOperationExecutionValidationStatus.Ready,
            new DateTimeOffset(2026, 8, 15, 0, 1, 0, TimeSpan.Zero),
            "ready");
    }

    private sealed class HistoryFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "FileOp.CrossVolumeMoveRecoveryEvidence.Tests",
            Guid.NewGuid().ToString("N"));

        public HistoryFixture()
        {
            Directory.CreateDirectory(_directory);
            DatabasePath = Path.Combine(_directory, "history.db");
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
