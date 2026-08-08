using FileOp.Core.Search;
using Microsoft.Data.Sqlite;

namespace FileOp.Core.Storage;

public sealed class SqliteStorageAnalytics : IStorageAnalytics, IDisposable
{
    private const int MaximumEntryLimit = 4_096;
    private const string FileTypeAnalysisSql = """
        WITH RECURSIVE tree(
            path,
            path_norm,
            extension_norm,
            is_directory,
            length,
            allocated_length,
            volume_serial,
            file_reference
        ) AS (
            SELECT
                path,
                path_norm,
                extension_norm,
                is_directory,
                length,
                allocated_length,
                volume_serial,
                file_reference
            FROM files
            WHERE parent_path = @root COLLATE NOCASE

            UNION ALL

            SELECT
                child.path,
                child.path_norm,
                child.extension_norm,
                child.is_directory,
                child.length,
                child.allocated_length,
                child.volume_serial,
                child.file_reference
            FROM files AS child
            JOIN tree ON child.parent_path = tree.path COLLATE NOCASE
        ),
        physical_rows AS (
            SELECT
                tree.*,
                CASE
                    WHEN is_directory = 1 THEN 1
                    ELSE ROW_NUMBER() OVER (
                        PARTITION BY
                            volume_serial,
                            file_reference,
                            CASE
                                WHEN volume_serial IS NULL OR file_reference IS NULL THEN path_norm
                                ELSE ''
                            END
                        ORDER BY path_norm
                    )
                END AS physical_rank
            FROM tree
        ),
        aggregates AS (
            SELECT
                extension_norm,
                SUM(length) AS logical_bytes,
                SUM(CASE
                    WHEN physical_rank = 1 THEN COALESCE(allocated_length, 0)
                    ELSE 0
                END) AS allocated_known_bytes,
                SUM(CASE
                    WHEN physical_rank = 1 AND allocated_length IS NULL THEN 1
                    ELSE 0
                END) AS unknown_allocated_files,
                COUNT(*) AS file_count,
                SUM(CASE WHEN physical_rank > 1 THEN 1 ELSE 0 END) AS hard_link_alias_count
            FROM physical_rows
            WHERE is_directory = 0
            GROUP BY extension_norm
        ),
        ranked AS (
            SELECT
                extension_norm,
                logical_bytes,
                allocated_known_bytes,
                unknown_allocated_files,
                file_count,
                hard_link_alias_count,
                SUM(logical_bytes) OVER () AS total_logical_bytes,
                SUM(allocated_known_bytes) OVER () AS total_allocated_known_bytes,
                SUM(unknown_allocated_files) OVER () AS total_unknown_allocated_files,
                SUM(file_count) OVER () AS total_file_count,
                SUM(hard_link_alias_count) OVER () AS total_hard_link_alias_count,
                COUNT(*) OVER () AS type_count
            FROM aggregates
        )
        SELECT
            extension_norm,
            logical_bytes,
            allocated_known_bytes,
            unknown_allocated_files,
            file_count,
            hard_link_alias_count,
            total_logical_bytes,
            total_allocated_known_bytes,
            total_unknown_allocated_files,
            total_file_count,
            total_hard_link_alias_count,
            type_count
        FROM ranked
        ORDER BY
            CASE
                WHEN total_unknown_allocated_files = 0 THEN allocated_known_bytes
                ELSE logical_bytes
            END DESC,
            logical_bytes DESC,
            extension_norm COLLATE NOCASE
        LIMIT @limit;
        """;

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
        ValidateLimit(maxEntries, nameof(maxEntries), "Storage analysis entry");

        var normalizedRoot = NormalizeIndexedPath(rootPath);
        using var connection = OpenConnection(queryOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE tree(
                entry_path,
                path,
                path_norm,
                is_directory,
                length,
                allocated_length,
                volume_serial,
                file_reference
            ) AS (
                SELECT
                    path,
                    path,
                    path_norm,
                    is_directory,
                    length,
                    allocated_length,
                    volume_serial,
                    file_reference
                FROM files
                WHERE parent_path = @root COLLATE NOCASE

                UNION ALL

                SELECT
                    tree.entry_path,
                    child.path,
                    child.path_norm,
                    child.is_directory,
                    child.length,
                    child.allocated_length,
                    child.volume_serial,
                    child.file_reference
                FROM files AS child
                JOIN tree ON child.parent_path = tree.path COLLATE NOCASE
            ),
            physical_rows AS (
                SELECT
                    tree.*,
                    CASE
                        WHEN is_directory = 1 THEN 1
                        ELSE ROW_NUMBER() OVER (
                            PARTITION BY
                                volume_serial,
                                file_reference,
                                CASE
                                    WHEN volume_serial IS NULL OR file_reference IS NULL THEN path_norm
                                    ELSE ''
                                END
                            ORDER BY path_norm
                        )
                    END AS physical_rank
                FROM tree
            ),
            aggregates AS (
                SELECT
                    entry_path,
                    SUM(CASE WHEN is_directory = 0 THEN length ELSE 0 END) AS logical_bytes,
                    SUM(CASE
                        WHEN is_directory = 0 AND physical_rank = 1 THEN COALESCE(allocated_length, 0)
                        ELSE 0
                    END) AS allocated_known_bytes,
                    SUM(CASE
                        WHEN is_directory = 0 AND physical_rank = 1 AND allocated_length IS NULL THEN 1
                        ELSE 0
                    END) AS unknown_allocated_files,
                    SUM(CASE WHEN is_directory = 0 THEN 1 ELSE 0 END) AS file_count,
                    SUM(CASE WHEN is_directory = 1 THEN 1 ELSE 0 END) AS directory_count,
                    SUM(CASE WHEN is_directory = 0 AND physical_rank > 1 THEN 1 ELSE 0 END) AS hard_link_alias_count
                FROM physical_rows
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
                    aggregates.hard_link_alias_count,
                    SUM(aggregates.logical_bytes) OVER () AS total_logical_bytes,
                    SUM(aggregates.allocated_known_bytes) OVER () AS total_allocated_known_bytes,
                    SUM(aggregates.unknown_allocated_files) OVER () AS total_unknown_allocated_files,
                    SUM(aggregates.file_count) OVER () AS total_file_count,
                    SUM(aggregates.directory_count) OVER () AS total_directory_count,
                    SUM(aggregates.hard_link_alias_count) OVER () AS total_hard_link_alias_count,
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
                hard_link_alias_count,
                total_logical_bytes,
                total_allocated_known_bytes,
                total_unknown_allocated_files,
                total_file_count,
                total_directory_count,
                total_hard_link_alias_count,
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
        long totalHardLinkAliasCount = 0;
        long directEntryCount = 0;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            totalLogicalBytes = reader.GetInt64(9);
            totalAllocatedKnownBytes = reader.GetInt64(10);
            totalUnknownAllocatedFiles = reader.GetInt64(11);
            totalFileCount = reader.GetInt64(12);
            totalDirectoryCount = reader.GetInt64(13);
            totalHardLinkAliasCount = reader.GetInt64(14);
            directEntryCount = reader.GetInt64(15);

            var unknownAllocatedFiles = reader.GetInt64(5);
            entries.Add(new StorageDirectoryEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2) != 0,
                reader.GetInt64(3),
                unknownAllocatedFiles == 0 ? reader.GetInt64(4) : null,
                CheckedCount(reader.GetInt64(6)),
                CheckedCount(reader.GetInt64(7)),
                CheckedCount(reader.GetInt64(8))));
        }

        return new StorageDirectoryAnalysis(
            normalizedRoot,
            totalLogicalBytes,
            totalUnknownAllocatedFiles == 0 ? totalAllocatedKnownBytes : null,
            CheckedCount(totalFileCount),
            CheckedCount(totalDirectoryCount),
            CheckedCount(totalHardLinkAliasCount),
            CheckedCount(directEntryCount),
            entries);
    }

    public async ValueTask<StorageFileTypeAnalysis> AnalyzeFileTypesAsync(
        string rootPath,
        int maxTypes = 128,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ThrowIfDisposed();
        ValidateLimit(maxTypes, nameof(maxTypes), "Storage file-type");

        var normalizedRoot = NormalizeIndexedPath(rootPath);
        using var connection = OpenConnection(queryOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = FileTypeAnalysisSql;
        command.Parameters.AddWithValue("@root", normalizedRoot);
        command.Parameters.AddWithValue("@limit", maxTypes);

        var types = new List<StorageFileTypeEntry>(Math.Min(maxTypes, 128));
        long totalLogicalBytes = 0;
        long totalAllocatedKnownBytes = 0;
        long totalUnknownAllocatedFiles = 0;
        long totalFileCount = 0;
        long totalHardLinkAliasCount = 0;
        long typeCount = 0;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            totalLogicalBytes = reader.GetInt64(6);
            totalAllocatedKnownBytes = reader.GetInt64(7);
            totalUnknownAllocatedFiles = reader.GetInt64(8);
            totalFileCount = reader.GetInt64(9);
            totalHardLinkAliasCount = reader.GetInt64(10);
            typeCount = reader.GetInt64(11);

            var extension = reader.GetString(0);
            var unknownAllocatedFiles = reader.GetInt64(3);
            types.Add(new StorageFileTypeEntry(
                extension,
                StorageFileCategoryClassifier.Classify(extension),
                reader.GetInt64(1),
                unknownAllocatedFiles == 0 ? reader.GetInt64(2) : null,
                CheckedCount(reader.GetInt64(4)),
                CheckedCount(reader.GetInt64(5))));
        }

        return new StorageFileTypeAnalysis(
            normalizedRoot,
            totalLogicalBytes,
            totalUnknownAllocatedFiles == 0 ? totalAllocatedKnownBytes : null,
            CheckedCount(totalFileCount),
            CheckedCount(totalHardLinkAliasCount),
            CheckedCount(typeCount),
            types);
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

    private static void ValidateLimit(int limit, string parameterName, string description)
    {
        if (limit <= 0 || limit > MaximumEntryLimit)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                limit,
                $"{description} limits must be between 1 and {MaximumEntryLimit:N0}.");
        }
    }

    private static int CheckedCount(long value) => checked((int)value);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
