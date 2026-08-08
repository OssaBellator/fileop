using BenchmarkDotNet.Attributes;
using FileOp.Core.Models;
using FileOp.Core.Search;
using FileOp.Core.Storage;

namespace FileOp.Benchmarks;

[MemoryDiagnoser]
public sealed class StorageAnalyticsBenchmarks : IDisposable
{
    private const int ProjectCount = 1_000;
    private const int BucketsPerProject = 16;
    private SqliteFileIndex? _index;
    private SqliteStorageAnalytics? _analytics;
    private string? _databasePath;

    [Params(100_000, 1_000_000)]
    public int FileCount { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"fileop-storage-benchmark-{Guid.NewGuid():N}.db");
        _index = new SqliteFileIndex(_databasePath);

        var directories = new List<FileRecord>(ProjectCount * (BucketsPerProject + 1));
        for (var project = 0; project < ProjectCount; project++)
        {
            var projectPath = $@"C:\synthetic\project-{project:D4}";
            directories.Add(Directory(projectPath, @"C:\synthetic", $"project-{project:D4}"));
            for (var bucket = 0; bucket < BucketsPerProject; bucket++)
            {
                directories.Add(Directory(
                    $@"{projectPath}\bucket-{bucket:D2}",
                    projectPath,
                    $"bucket-{bucket:D2}"));
            }
        }

        await _index.AddBatchAsync(directories);

        const int batchSize = 10_000;
        for (var offset = 0; offset < FileCount; offset += batchSize)
        {
            var count = Math.Min(batchSize, FileCount - offset);
            var records = new FileRecord[count];
            for (var item = 0; item < count; item++)
            {
                var ordinal = offset + item;
                var project = ordinal % ProjectCount;
                var bucket = (ordinal / ProjectCount) % BucketsPerProject;
                var parentPath = $@"C:\synthetic\project-{project:D4}\bucket-{bucket:D2}";
                var length = 1_024L + ((ordinal * 7_919L) % (64L * 1024 * 1024));
                records[item] = new FileRecord(
                    $@"{parentPath}\file-{ordinal:D7}.bin",
                    $"file-{ordinal:D7}.bin",
                    parentPath,
                    ".bin",
                    length,
                    false,
                    DateTimeOffset.UnixEpoch,
                    FileAttributes.Normal,
                    AllocatedLength: Align4K(length));
            }

            await _index.AddBatchAsync(records);
        }

        _analytics = new SqliteStorageAnalytics(_databasePath);
    }

    [Benchmark(Baseline = true)]
    public async Task<long> AnalyzeRootAsync()
    {
        var analysis = await Analytics.AnalyzeDirectoryAsync(@"C:\synthetic", maxEntries: 1_000);
        return analysis.LogicalBytes;
    }

    [Benchmark]
    public async Task<long> AnalyzeProjectAsync()
    {
        var analysis = await Analytics.AnalyzeDirectoryAsync(@"C:\synthetic\project-0042", maxEntries: 64);
        return analysis.LogicalBytes;
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _analytics?.Dispose();
        _analytics = null;
        _index?.Dispose();
        _index = null;

        if (_databasePath is null)
        {
            return;
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                File.Delete(_databasePath + suffix);
            }
            catch (IOException)
            {
            }
        }

        _databasePath = null;
    }

    private SqliteStorageAnalytics Analytics =>
        _analytics ?? throw new InvalidOperationException("The benchmark analytics engine has not been initialized.");

    private static FileRecord Directory(string path, string parentPath, string name) =>
        new(
            path,
            name,
            parentPath,
            string.Empty,
            0,
            true,
            DateTimeOffset.UnixEpoch,
            FileAttributes.Directory);

    private static long Align4K(long length) => (length + 4095L) & ~4095L;
}
