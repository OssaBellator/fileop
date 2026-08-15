using System;
using System.IO;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileCrossVolumeMovePersistedRecoveryCorruptionTests
{
    [TestMethod]
    public async Task LoaderRejectsSourceDeleteRecoveryWithoutCommittedDestinationEvidence()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation();
        using (var store = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.DatabasePath))
        {
            await store.BeginAsync(validation, DateTimeOffset.UtcNow);
            await store.MarkCopyMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);
        }

        using (var connection = OpenWritable(fixture.DatabasePath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE file_cross_volume_move_entries
                SET state = 7,
                    source_delete_started_utc_ticks = @ticks,
                    completed_utc_ticks = @ticks,
                    failure_code = 'CorruptSourceDeleteRecovery',
                    failure_message = 'Fixture adds a source-delete barrier without destination commit evidence.',
                    failure_path = canonical_source_path,
                    failure_retryable = 0
                WHERE operation_id = @operation_id
                  AND ordinal = 0;
                """;
            command.Parameters.AddWithValue("@ticks", DateTimeOffset.UtcNow.UtcTicks);
            command.Parameters.AddWithValue("@operation_id", validation.Plan.Id.ToString("N"));
            Assert.AreEqual(1, command.ExecuteNonQuery());
        }

        using var reopened = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.DatabasePath);
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await reopened.GetAsync(validation.Plan.Id));
    }

    [TestMethod]
    public async Task LoaderRejectsRecoveryDestinationEvidenceOnWrongRootVolume()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation();
        using (var store = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.DatabasePath))
        {
            await store.BeginAsync(validation, DateTimeOffset.UtcNow);
            await store.MarkCopyMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);
            await store.MarkRecoveryRequiredAsync(
                validation.Plan.Id,
                0,
                new FileOperationFailure(
                    "ObservedDestination",
                    "fixture recovery observation",
                    validation.Items[0].Destination.CanonicalPath,
                    Retryable: false),
                DateTimeOffset.UtcNow,
                destinationIdentity: new FileIdentity(2, 500),
                destinationContentFingerprint: Fingerprint());
        }

        using (var connection = OpenWritable(fixture.DatabasePath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE file_cross_volume_move_entries
                SET destination_volume_serial = 999
                WHERE operation_id = @operation_id
                  AND ordinal = 0;
                """;
            command.Parameters.AddWithValue("@operation_id", validation.Plan.Id.ToString("N"));
            Assert.AreEqual(1, command.ExecuteNonQuery());
        }

        using var reopened = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.DatabasePath);
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await reopened.GetAsync(validation.Plan.Id));
    }

    private static SqliteConnection OpenWritable(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Shared,
            }.ToString());
        connection.Open();
        return connection;
    }

    private static FileContentFingerprint Fingerprint() =>
        new(
            FileContentFingerprintAlgorithm.Sha256,
            new string('a', FileContentFingerprint.Sha256HexLength));

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
            "FileOp.CrossVolumeMoveRecoveryCorruption.Tests",
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
