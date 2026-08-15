using System;
using System.IO;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileCrossVolumeMovePersistedCorruptionTests
{
    [TestMethod]
    public async Task LoaderRejectsSafeFailureThatHidesUnresolvedCopyBarrier()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation();
        using (var store = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.DatabasePath))
        {
            await store.BeginAsync(validation, DateTimeOffset.UtcNow);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fixture.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE file_cross_volume_move_entries
                SET state = 6,
                    copy_mutation_started_utc_ticks = @ticks,
                    completed_utc_ticks = @ticks,
                    failure_code = 'CorruptSafeFailure',
                    failure_message = 'Fixture deliberately hides a durable Copy barrier.',
                    failure_path = canonical_source_path,
                    failure_retryable = 0
                WHERE operation_id = @operation_id
                  AND ordinal = 0;
                """;
            command.Parameters.AddWithValue(
                "@ticks",
                DateTimeOffset.UtcNow.UtcTicks);
            command.Parameters.AddWithValue(
                "@operation_id",
                validation.Plan.Id.ToString("N"));
            Assert.AreEqual(1, command.ExecuteNonQuery());
        }

        using var reopened = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.DatabasePath);
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await reopened.GetAsync(validation.Plan.Id));
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
            new[]
            {
                new FileOperationExecutionValidationItem(
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
                    "ready"),
            },
            FileOperationExecutionValidationStatus.Ready,
            new DateTimeOffset(2026, 8, 15, 0, 1, 0, TimeSpan.Zero),
            "ready");
    }

    private sealed class HistoryFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "FileOp.CrossVolumeMoveCorruption.Tests",
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
