using FileOp.Core.Models;
using FileOp.Core.Search;
using FileOp.Core.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class StorageOptimizationAnalyticsTests
{
    [TestMethod]
    public async Task AnalyzerRanksMeasuredSpaceAndCollapsesHardLinkAliases()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"fileop-optimization-{Guid.NewGuid():N}.sqlite");
        var asOf = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        try
        {
            using (var index = new SqliteFileIndex(databasePath))
            {
                await index.AddBatchAsync(
                [
                    Directory(@"C:\Scope", @"C:\", 1),
                    File(@"C:\Scope\recent.iso", 2_000, 1_500, asOf.AddDays(-10), 10),
                    File(@"C:\Scope\old.bin", 1_200, 1_100, asOf.AddDays(-300), 11),
                    File(@"C:\Scope\same-a.zip", 800, 800, asOf.AddDays(-20), 12),
                    File(@"C:\Scope\same-a-alias.zip", 800, 800, asOf.AddDays(-20), 12),
                    File(@"C:\Scope\same-b.zip", 800, 800, asOf.AddDays(-30), 13),
                    File(@"C:\outside.zip", 800, 800, asOf.AddDays(-40), 14),
                ]);
            }

            var policy = new StorageOptimizationPolicy(
                LargeFileMinimumBytes: 1_000,
                SameSizeMinimumBytes: 500,
                StaleAgeDays: 180,
                MaxLargeFiles: 10,
                MaxStaleLargeFiles: 10,
                MaxSameSizeGroups: 10,
                MaxFilesPerSameSizeGroup: 10);
            var analyzer = new SqliteStorageOptimizationAnalytics(databasePath);
            var analysis = await analyzer.AnalyzeOptimizationAsync(@"C:\Scope", policy, asOf);

            CollectionAssert.AreEqual(
                new[] { "recent.iso", "old.bin" },
                analysis.LargestFiles.Select(static file => file.Name).ToArray());
            Assert.AreEqual(1, analysis.StaleLargeFiles.Count);
            Assert.AreEqual("old.bin", analysis.StaleLargeFiles[0].Name);

            Assert.AreEqual(1, analysis.SameSizeCandidateGroups.Count);
            var sameSize = analysis.SameSizeCandidateGroups[0];
            Assert.AreEqual(800L, sameSize.LogicalBytesPerFile);
            Assert.AreEqual(2, sameSize.CandidateFileCount);
            Assert.AreEqual(800L, sameSize.PotentialLogicalSavingsUpperBound);
            Assert.AreEqual(2, sameSize.SampleFiles.Count);
            Assert.IsFalse(sameSize.SampleFiles.Any(static file => file.Name == "outside.zip"));

            var hardLinkNames = sameSize.SampleFiles.Count(static file =>
                file.Name is "same-a.zip" or "same-a-alias.zip");
            Assert.AreEqual(1, hardLinkNames);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }

            var wal = databasePath + "-wal";
            var shm = databasePath + "-shm";
            if (File.Exists(wal))
            {
                File.Delete(wal);
            }

            if (File.Exists(shm))
            {
                File.Delete(shm);
            }
        }
    }

    private static FileRecord Directory(string path, string parentPath, ulong reference) =>
        new(
            path,
            Path.GetFileName(path.TrimEnd('\\')),
            parentPath,
            string.Empty,
            0,
            IsDirectory: true,
            DateTimeOffset.UnixEpoch,
            FileAttributes.Directory,
            new FileIdentity(1, reference));

    private static FileRecord File(
        string path,
        long logicalBytes,
        long allocatedBytes,
        DateTimeOffset lastWrite,
        ulong reference) =>
        new(
            path,
            Path.GetFileName(path),
            Path.GetDirectoryName(path)!,
            Path.GetExtension(path),
            logicalBytes,
            IsDirectory: false,
            lastWrite,
            FileAttributes.Normal,
            new FileIdentity(1, reference),
            AllocatedLength: allocatedBytes);
}
