using BenchmarkDotNet.Attributes;
using FileOp.Core.Models;
using FileOp.Core.Search;

namespace FileOp.Benchmarks;

[MemoryDiagnoser]
public sealed class SearchBenchmarks
{
    private SqliteFileIndex? _index;
    private string? _databasePath;
    private readonly FileSearchQuery _rareNameQuery = FileSearchQuery.Parse("needle-0000000", 200);
    private readonly FileSearchQuery _commonPathQuery = FileSearchQuery.Parse("project-0042", 200);
    private readonly FileSearchQuery _extensionSizeQuery = FileSearchQuery.Parse("ext:bin size:>1gb", 200);

    [Params(100_000, 1_000_000)]
    public int RecordCount { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"fileop-benchmark-{Guid.NewGuid():N}.db");
        _index = new SqliteFileIndex(_databasePath);

        const int batchSize = 10_000;
        for (var offset = 0; offset < RecordCount; offset += batchSize)
        {
            var count = Math.Min(batchSize, RecordCount - offset);
            var records = new FileRecord[count];

            for (var index = 0; index < count; index++)
            {
                var ordinal = offset + index;
                records[index] = CreateRecord(ordinal);
            }

            await _index.AddBatchAsync(records);
        }
    }

    [Benchmark(Baseline = true)]
    public async Task<int> RareNameContainsAsync()
    {
        var result = await Index.SearchAsync(_rareNameQuery);
        return result.Count;
    }

    [Benchmark]
    public async Task<int> CommonPathContainsAsync()
    {
        var result = await Index.SearchAsync(_commonPathQuery);
        return result.Count;
    }

    [Benchmark]
    public async Task<int> ExtensionAndSizeFilterAsync()
    {
        var result = await Index.SearchAsync(_extensionSizeQuery);
        return result.Count;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _index?.Dispose();
        _index = null;

        if (_databasePath is null)
        {
            return;
        }

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                File.Delete(_databasePath + suffix);
            }
            catch (IOException)
            {
                // SQLite pooling can keep a benchmark database alive briefly after cleanup.
            }
        }
    }

    private SqliteFileIndex Index =>
        _index ?? throw new InvalidOperationException("The benchmark index has not been initialized.");

    private static FileRecord CreateRecord(int ordinal)
    {
        var extension = ordinal % 5 switch
        {
            0 => ".bin",
            1 => ".log",
            2 => ".pdf",
            3 => ".zip",
            _ => ".txt",
        };
        var name = ordinal % 50_000 == 0
            ? $"needle-{ordinal:D7}{extension}"
            : $"file-{ordinal:D7}{extension}";
        var parentPath = $@"C:\synthetic\project-{ordinal % 1000:D4}\bucket-{ordinal % 64:D2}";
        var path = $@"{parentPath}\{name}";
        var length = (long)((ordinal * 7_919L) % (5L * 1024 * 1024 * 1024));

        return new FileRecord(
            path,
            name,
            parentPath,
            extension,
            length,
            false,
            DateTimeOffset.UnixEpoch.AddSeconds(ordinal % 31_536_000),
            FileAttributes.Normal);
    }
}
