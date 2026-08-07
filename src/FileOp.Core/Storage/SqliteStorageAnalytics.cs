using FileOp.Core.Search;
using Microsoft.Data.Sqlite;

namespace FileOp.Core.Storage;

public sealed class SqliteStorageAnalytics : IStorageAnalytics, IDisposable
{
    private const int MaximumEntryLimit = 4_096;
    private readonly string _connectionString;
    private bool _disposed;

    public SqliteStorageAnalytics(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Storage analytics shares the same durable metadata database as search.
        // Bootstrapping through SqliteFileIndex guarantees the schema exists before
        // the additive parent-path index is created.
        using (var bootstrap = new SqliteFileIndex(fullPath))
        {
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 5,
        }.ToString();

        EnsureAnalyticsIndexes();
    }

    public async ValueTask<StorageDirectoryAnalysis> AnalyzeDirectoryAsync(
        string rootPath,
        int maxEntries = 256,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ThrowIfDisposed();

        if (maxEntries <= 0 || maxEntries > MaximumEntryLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxEntries),
                maxEntries,
                $"Storage analysis entry limits must be between 1 and {MaximumEntryLimit:N0}.");
        }

        var normalizedRoot = NormalizeIndexedPath(rootPath);
        using var connection = OpenConnection(queryOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE tree(
                entry_path,
                path,
                is_directory,
                length,
                allocated_length
            ) AS (
                SELECT
                    path,
                    path,
                    is_directory,
                    length,
                    allocated_length
                FROM files
                WHERE parent_path = @root COLLATE NOCASE

                UNION ALL

                SELECT
                    tree.entry_path,
                    child.path,
                    child.is_directory,
                    child.length,
                    child.allocated_length
                FROM files AS child
                JOIN tree ON child.parent_path = tree.path COLLATE NOCASE
            ),
            aggregates AS (
                SELECT
                    entry_path,
                    SUM(CASE WHEN is_directory = 0 THEN length ELSE 0 END) AS logical_bytes,
                    SUM(CASE WHEN is_directory = 0 THEN COALESCE(allocated_length, 0) ELSE 0 END) AS allocated_known_bytes,
                    SUM(CASE WHEN is_directory = 0 AND allocated_length IS NULL THEN 1 ELSE 0 END) AS unknown_allocated_files,
                    SUM(CASE WHEN is_directory = 0 THEN 1 ELSE 0 END) AS file_count,
                    SUM(CASE WHEN is_directory = 1 THEN 1 ELSE 0 END) AS directory_count
                FROM tree
                GROUP BY entry_path
            ),
            ranked AS (
                SELECT
                    entry.path,
                    entry.name,
                    entry.is_directory,
                    aggregates.logical_bytes,
                    aggregates.allocated_known_bytes,
                    aggregates.unknown_allocated_files,
                    aggregates.file_count,
                    aggregates.directory_count,
                    SUM(aggregates.logical_bytes) OVER () AS total_logical_bytes,
                    SUM(aggregates.allocated_known_bytes) OVER () AS total_allocated_known_bytes,
                    SUM(aggregates.unknown_allocated_files) OVER () AS total_unknown_allocated_files,
                    SUM(aggregates.file_count) OVER () AS total_file_count,
                    SUM(aggregates.directory_count) OVER () AS total_directory_count,
                    COUNT(*) OVER () AS direct_entry_count
                FROM aggregates
                JOIN files AS entry ON entry.path = aggregates.entry_path COLLATE NOCASE
            )
            SELECT
                path,
                name,
                is_directory,
                logical_bytes,
                allocated_known_bytes,
                unknown_allocated_files,
                file_count,
                directory_count,
                total_logical_bytes,
                total_allocated_known_bytes,
                total_unknown_allocated_files,
                total_file_count,
                total_directory_count,
                direct_entry_count
            FROM ranked
            ORDER BY
                CASE
                    WHEN unknown_allocated_files = 0 THEN allocated_known_bytes
                    ELSE logical_bytes
                END DESC,
                logical_bytes DESC,
                name COLLATE NOCASE,
                path COLLATE NOCASE
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@root", normalizedRoot);
        command.Parameters.AddWithValue("@limit", maxEntries);

        var entries = new List<StorageDirectoryEntry>(Math.Min(maxEntries, 256));
        long totalLogicalBytes = 0;
        long totalAllocatedKnownBytes = 0;
        long totalUnknownAllocatedFiles = 0;
        long totalFileCount = 0;
        long totalDirectoryCount = 0;
        long directEntryCount = 0;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            totalLogicalBytes = reader.GetInt64(8);
            totalAllocatedKnownBytes = reader.GetInt64(9);
            totalUnknownAllocatedFiles = reader.GetInt64(10);
            totalFileCount = reader.GetInt64(11);
            totalDirectoryCount = reader.GetInt64(12);
            directEntryCount = reader.GetInt64(13);

            var unknownAllocatedFiles = reader.GetInt64(5);
            entries.Add(new StorageDirectoryEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2) != 0,
                reader.GetInt64(3),
                unknownAllocatedFiles == 0 ? reader.GetInt64(4) : null,
                CheckedCount(reader.GetInt64(6)),
                CheckedCount(reader.GetInt64(7))));
        }

        return new StorageDirectoryAnalysis(
            normalizedRoot,
            totalLogicalBytes,
            totalUnknownAllocatedFiles == 0 ? totalAllocatedKnownBytes : null,
            CheckedCount(totalFileCount),
            CheckedCount(totalDirectoryCount),
            CheckedCount(directEntryCount),
            entries);
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private void EnsureAnalyticsIndexes()
    {
        using var connection = OpenConnection(queryOnly: false);
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE INDEX IF NOT EXISTS ix_files_parent_path ON files(parent_path COLLATE NOCASE);";
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection(bool queryOnly)
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = queryOnly
            ? "PRAGMA busy_timeout = 5000; PRAGMA query_only = ON;"
            : "PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static string NormalizeIndexedPath(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.Length == 3 &&
            char.IsLetter(trimmed[0]) &&
            trimmed[1] == ':' &&
            (trimmed[2] == '\\' || trimmed[2] == '/'))
        {
            return $"{char.ToUpperInvariant(trimmed[0])}:\\";
        }

        trimmed = trimmed.TrimEnd('\\', '/');
        return string.IsNullOrEmpty(trimmed) ? path : trimmed;
    }

    private static int CheckedCount(long value) => checked((int)value);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
