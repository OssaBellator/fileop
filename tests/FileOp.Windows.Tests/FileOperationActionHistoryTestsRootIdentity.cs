using System;
using System.IO;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileOperationActionHistoryTestsRootIdentity
{
    [TestMethod]
    public async Task BeginPersistsHighBitRootIdentitiesAcrossReopen()
    {
        using var fixture = new HistoryFixture();
        var sourceIdentity = new FileIdentity(ulong.MaxValue - 101, ulong.MaxValue - 103);
        var destinationIdentity = new FileIdentity(ulong.MaxValue - 107, ulong.MaxValue - 109);
        var validation = CreateValidation(sourceIdentity, destinationIdentity);

        using (var store = new SqliteFileOperationActionHistoryStore(fixture.DatabasePath))
        {
            var begun = await store.BeginAsync(validation, DateTimeOffset.UtcNow);
            Assert.AreEqual(sourceIdentity, begun.SourceDirectoryIdentity);
            Assert.AreEqual(destinationIdentity, begun.DestinationDirectoryIdentity);
            Assert.IsTrue(begun.HasVerifiedRootIdentities);
        }

        using var reopened = new SqliteFileOperationActionHistoryStore(fixture.DatabasePath);
        var persisted = await reopened.GetAsync(validation.Plan.Id);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(sourceIdentity, persisted.SourceDirectoryIdentity);
        Assert.AreEqual(destinationIdentity, persisted.DestinationDirectoryIdentity);
        Assert.IsTrue(persisted.HasVerifiedRootIdentities);
    }

    [TestMethod]
    public async Task LegacyHistoryWithoutRootSideRowLoadsAsUnverified()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation(new FileIdentity(1, 10), new FileIdentity(2, 20));

        using (var store = new SqliteFileOperationActionHistoryStore(fixture.DatabasePath))
        {
            await store.BeginAsync(validation, DateTimeOffset.UtcNow);
        }

        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection($"Data Source={fixture.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM file_operation_action_root_identities
                WHERE operation_id = @operation_id;
                """;
            command.Parameters.AddWithValue("@operation_id", validation.Plan.Id.ToString("D"));
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync());
        }

        using var reopened = new SqliteFileOperationActionHistoryStore(fixture.DatabasePath);
        var persisted = await reopened.GetAsync(validation.Plan.Id);
        Assert.IsNotNull(persisted);
        Assert.IsNull(persisted.SourceDirectoryIdentity);
        Assert.IsNull(persisted.DestinationDirectoryIdentity);
        Assert.IsFalse(persisted.HasVerifiedRootIdentities);
    }

    [TestMethod]
    public async Task BeginRejectsMissingRootIdentityBeforeWritingHistory()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation(
            new FileIdentity(1, 10),
            destinationIdentity: null);
        using var store = new SqliteFileOperationActionHistoryStore(fixture.DatabasePath);

        await Assert.ThrowsExceptionAsync<ArgumentException>(async () =>
            await store.BeginAsync(validation, DateTimeOffset.UtcNow));
        Assert.IsNull(await store.GetAsync(validation.Plan.Id));
    }

    [TestMethod]
    public void HistoryRejectsHalfRootIdentityEvidence()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = new FileOperationActionEntry(
            0,
            new FileOperationEntry(@"C:\Source\a.txt", "a.txt", IsDirectory: false),
            @"C:\Real\Source\a.txt",
            @"D:\Real\Destination\a.txt",
            FileOperationActionEntryState.Pending,
            null,
            null,
            new FileIdentity(1, 100),
            null,
            FileOperationUndoKind.None,
            null);

        Assert.ThrowsException<ArgumentException>(() =>
            new FileOperationActionHistory(
                Guid.NewGuid(),
                now,
                now,
                now,
                null,
                FileOperationKind.Copy,
                FileOperationCollisionPolicy.Stop,
                @"C:\Source",
                @"D:\Destination",
                @"C:\Real\Source",
                @"D:\Real\Destination",
                null,
                new[] { entry },
                SourceDirectoryIdentity: new FileIdentity(1, 1),
                DestinationDirectoryIdentity: null));
    }

    private static FileOperationExecutionValidationResult CreateValidation(
        FileIdentity sourceIdentity,
        FileIdentity? destinationIdentity)
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
                Identity: sourceIdentity),
            new FileOperationCanonicalPath(
                destinationDirectory,
                canonicalDestinationDirectory,
                FileOperationCanonicalPathState.Directory,
                IsLeafReparsePoint: false,
                Identity: destinationIdentity),
            new[] { item },
            FileOperationExecutionValidationStatus.Ready,
            DateTimeOffset.UtcNow,
            "ready");
    }

    private sealed class HistoryFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "FileOp.ActionHistory.RootIdentity.Tests",
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
