using System.Globalization;
using Microsoft.Data.Sqlite;

namespace FileOp.Core.Storage;

public sealed class SqliteStorageHistoryStore : IStorageHistoryStore, IDisposable
{
    private const int HistorySchemaVersion = 1;
    private const int MaximumHistoryLimit = 4_096;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _disposed;

    public SqliteStorageHistoryStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 5,
        }.ToString();

        Initialize();
    }

    public async ValueTask<long> SaveSnapshotAsync(
        StorageFileTypeAnalysis analysis,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentException.ThrowIfNullOrWhiteSpace(analysis.RootPath);
        ThrowIfDisposed();
        ValidateAnalysis(analysis);

        var normalizedRoot = NormalizeIndexedPath(analysis.RootPath);
        var capturedTicks = capturedAt.ToUniversalTime().UtcDateTime.Ticks;

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            long snapshotId;
            using (var snapshot = connection.CreateCommand())
            {
                snapshot.Transaction = transaction;
                snapshot.CommandText = """
                    INSERT INTO storage_history_snapshots(
                        root_path, captured_utc_ticks, logical_bytes, allocated_bytes,
                        file_count, hard_link_alias_count, type_count)
                    VALUES (
                        @root_path, @captured_utc_ticks, @logical_bytes, @allocated_bytes,
                        @file_count, @hard_link_alias_count, @type_count)
                    ON CONFLICT(root_path, captured_utc_ticks) DO UPDATE SET
                        logical_bytes = excluded.logical_bytes,
                        allocated_bytes = excluded.allocated_bytes,
                        file_count = excluded.file_count,
                        hard_link_alias_count = excluded.hard_link_alias_count,
                        type_count = excluded.type_count
                    RETURNING id;
                    """;
                snapshot.Parameters.AddWithValue("@root_path", normalizedRoot);
                snapshot.Parameters.AddWithValue("@captured_utc_ticks", capturedTicks);
                snapshot.Parameters.AddWithValue("@logical_bytes", analysis.LogicalBytes);
                snapshot.Parameters.AddWithValue(
                    "@allocated_bytes",
                    analysis.AllocatedBytes is { } allocated ? allocated : DBNull.Value);
                snapshot.Parameters.AddWithValue("@file_count", analysis.FileCount);
                snapshot.Parameters.AddWithValue("@hard_link_alias_count", analysis.HardLinkAliasCount);
                snapshot.Parameters.AddWithValue("@type_count", analysis.TypeCount);
                snapshotId = Convert.ToInt64(
                    await snapshot.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
            }

            using (var deleteCategories = connection.CreateCommand())
            {
                deleteCategories.Transaction = transaction;
                deleteCategories.CommandText =
                    "DELETE FROM storage_history_categories WHERE snapshot_id = @snapshot_id;";
                deleteCategories.Parameters.AddWithValue("@snapshot_id", snapshotId);
                await deleteCategories.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using (var category = connection.CreateCommand())
            {
                category.Transaction = transaction;
                category.CommandText = """
                    INSERT INTO storage_history_categories(
                        snapshot_id, category, logical_bytes, allocated_bytes,
                        file_count, hard_link_alias_count, type_count)
                    VALUES (
                        @snapshot_id, @category, @logical_bytes, @allocated_bytes,
                        @file_count, @hard_link_alias_count, @type_count);
                    """;
                category.Parameters.Add("@snapshot_id", SqliteType.Integer);
                category.Parameters.Add("@category", SqliteType.Integer);
                category.Parameters.Add("@logical_bytes", SqliteType.Integer);
                category.Parameters.Add("@allocated_bytes", SqliteType.Integer);
                category.Parameters.Add("@file_count", SqliteType.Integer);
                category.Parameters.Add("@hard_link_alias_count", SqliteType.Integer);
                category.Parameters.Add("@type_count", SqliteType.Integer);

                foreach (var item in analysis.Categories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    category.Parameters["@snapshot_id"].Value = snapshotId;
                    category.Parameters["@category"].Value = (int)item.Category;
                    category.Parameters["@logical_bytes"].Value = item.LogicalBytes;
                    category.Parameters["@allocated_bytes"].Value = item.AllocatedBytes is { } allocated
                        ? allocated
                        : DBNull.Value;
                    category.Parameters["@file_count"].Value = item.FileCount;
                    category.Parameters["@hard_link_alias_count"].Value = item.HardLinkAliasCount;
                    category.Parameters["@type_count"].Value = item.TypeCount;
                    await category.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            transaction.Commit();
            return snapshotId;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<StorageHistorySnapshot>> GetSnapshotsAsync(
        string rootPath,
        int limit = 90,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ThrowIfDisposed();
        if (limit <= 0 || limit > MaximumHistoryLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                limit,
                $"Storage history limits must be between 1 and {MaximumHistoryLimit:N0}.");
        }

        var normalizedRoot = NormalizeIndexedPath(rootPath);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                snapshot.id,
                snapshot.root_path,
                snapshot.captured_utc_ticks,
                snapshot.logical_bytes,
                snapshot.allocated_bytes,
                snapshot.file_count,
                snapshot.hard_link_alias_count,
                snapshot.type_count,
                category.category,
                category.logical_bytes,
                category.allocated_bytes,
                category.file_count,
                category.hard_link_alias_count,
                category.type_count
            FROM (
                SELECT *
                FROM storage_history_snapshots
                WHERE root_path = @root_path COLLATE NOCASE
                ORDER BY captured_utc_ticks DESC
                LIMIT @limit
            ) AS snapshot
            LEFT JOIN storage_history_categories AS category
                ON category.snapshot_id = snapshot.id
            ORDER BY snapshot.captured_utc_ticks DESC, category.category;
            """;
        command.Parameters.AddWithValue("@root_path", normalizedRoot);
        command.Parameters.AddWithValue("@limit", limit);

        var builders = new List<SnapshotBuilder>();
        SnapshotBuilder? current = null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetInt64(0);
            if (current is null || current.Id != id)
            {
                current = new SnapshotBuilder(
                    id,
                    reader.GetString(1),
                    new DateTimeOffset(new DateTime(reader.GetInt64(2), DateTimeKind.Utc)),
                    reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    checked((int)reader.GetInt64(5)),
                    checked((int)reader.GetInt64(6)),
                    checked((int)reader.GetInt64(7)));
                builders.Add(current);
            }

            if (!reader.IsDBNull(8))
            {
                var categoryValue = checked((int)reader.GetInt64(8));
                var category = (StorageFileCategory)categoryValue;
                if (!Enum.IsDefined(category))
                {
                    throw new InvalidDataException($"Unknown storage-history category value {categoryValue}.");
                }

                current.Categories.Add(new StorageHistoryCategorySnapshot(
                    category,
                    reader.GetInt64(9),
                    reader.IsDBNull(10) ? null : reader.GetInt64(10),
                    checked((int)reader.GetInt64(11)),
                    checked((int)reader.GetInt64(12)),
                    checked((int)reader.GetInt64(13))));
            }
        }

        return builders.Select(static builder => builder.Build()).ToArray();
    }

    public async ValueTask<int> PruneBeforeAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var cutoffTicks = cutoff.ToUniversalTime().UtcDateTime.Ticks;

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                "DELETE FROM storage_history_snapshots WHERE captured_utc_ticks < @cutoff_utc_ticks;";
            command.Parameters.AddWithValue("@cutoff_utc_ticks", cutoffTicks);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writeGate.Dispose();
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using (var versionTable = connection.CreateCommand())
        {
            versionTable.CommandText = $$"""
                PRAGMA journal_mode = WAL;

                CREATE TABLE IF NOT EXISTS storage_history_schema_info(
                    id INTEGER PRIMARY KEY CHECK(id = 1),
                    version INTEGER NOT NULL
                );
                INSERT OR IGNORE INTO storage_history_schema_info(id, version)
                VALUES (1, {{HistorySchemaVersion}});
                """;
            versionTable.ExecuteNonQuery();
        }

        using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.CommandText =
                "SELECT version FROM storage_history_schema_info WHERE id = 1;";
            var version = Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (version != HistorySchemaVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported FileOp storage-history schema version {version}; expected {HistorySchemaVersion}.");
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS storage_history_snapshots(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                root_path TEXT NOT NULL COLLATE NOCASE,
                captured_utc_ticks INTEGER NOT NULL,
                logical_bytes INTEGER NOT NULL,
                allocated_bytes INTEGER NULL,
                file_count INTEGER NOT NULL,
                hard_link_alias_count INTEGER NOT NULL,
                type_count INTEGER NOT NULL,
                UNIQUE(root_path, captured_utc_ticks)
            );

            CREATE INDEX IF NOT EXISTS ix_storage_history_root_captured
                ON storage_history_snapshots(root_path COLLATE NOCASE, captured_utc_ticks DESC);

            CREATE TABLE IF NOT EXISTS storage_history_categories(
                snapshot_id INTEGER NOT NULL,
                category INTEGER NOT NULL,
                logical_bytes INTEGER NOT NULL,
                allocated_bytes INTEGER NULL,
                file_count INTEGER NOT NULL,
                hard_link_alias_count INTEGER NOT NULL,
                type_count INTEGER NOT NULL,
                PRIMARY KEY(snapshot_id, category),
                FOREIGN KEY(snapshot_id) REFERENCES storage_history_snapshots(id) ON DELETE CASCADE
            ) WITHOUT ROWID;
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA busy_timeout = 5000;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            """;
        command.ExecuteNonQuery();
        return connection;
    }

    private static void ValidateAnalysis(StorageFileTypeAnalysis analysis)
    {
        if (analysis.LogicalBytes < 0 ||
            analysis.AllocatedBytes is < 0 ||
            analysis.FileCount < 0 ||
            analysis.HardLinkAliasCount < 0 ||
            analysis.HardLinkAliasCount > analysis.FileCount ||
            analysis.TypeCount < 0)
        {
            throw new ArgumentException("Storage history root totals contain invalid negative or alias counts.", nameof(analysis));
        }

        if (analysis.Categories.Select(static item => item.Category).Distinct().Count() != analysis.Categories.Count)
        {
            throw new ArgumentException("Storage history analysis contains duplicate category rows.", nameof(analysis));
        }

        foreach (var item in analysis.Categories)
        {
            if (!Enum.IsDefined(item.Category) ||
                item.LogicalBytes < 0 ||
                item.AllocatedBytes is < 0 ||
                item.FileCount < 0 ||
                item.HardLinkAliasCount < 0 ||
                item.HardLinkAliasCount > item.FileCount ||
                item.TypeCount <= 0)
            {
                throw new ArgumentException("Storage history analysis contains an invalid category row.", nameof(analysis));
            }
        }

        if (analysis.Categories.Sum(static item => item.LogicalBytes) != analysis.LogicalBytes ||
            analysis.Categories.Sum(static item => item.FileCount) != analysis.FileCount ||
            analysis.Categories.Sum(static item => item.HardLinkAliasCount) != analysis.HardLinkAliasCount ||
            analysis.Categories.Sum(static item => item.TypeCount) != analysis.TypeCount)
        {
            throw new ArgumentException("Storage history category totals do not reconcile with the analysis root.", nameof(analysis));
        }

        if (analysis.AllocatedBytes is { } rootAllocated)
        {
            if (analysis.Categories.Any(static item => !item.AllocatedBytes.HasValue) ||
                analysis.Categories.Sum(static item => item.AllocatedBytes!.Value) != rootAllocated)
            {
                throw new ArgumentException(
                    "Storage history physical category totals do not reconcile with the analysis root.",
                    nameof(analysis));
            }
        }
        else if (analysis.FileCount > 0 &&
                 analysis.Categories.All(static item => item.AllocatedBytes.HasValue))
        {
            throw new ArgumentException(
                "Storage history root allocation cannot be unknown when every category allocation is exact.",
                nameof(analysis));
        }
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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class SnapshotBuilder
    {
        public SnapshotBuilder(
            long id,
            string rootPath,
            DateTimeOffset capturedAt,
            long logicalBytes,
            long? allocatedBytes,
            int fileCount,
            int hardLinkAliasCount,
            int typeCount)
        {
            Id = id;
            RootPath = rootPath;
            CapturedAt = capturedAt;
            LogicalBytes = logicalBytes;
            AllocatedBytes = allocatedBytes;
            FileCount = fileCount;
            HardLinkAliasCount = hardLinkAliasCount;
            TypeCount = typeCount;
        }

        public long Id { get; }
        public string RootPath { get; }
        public DateTimeOffset CapturedAt { get; }
        public long LogicalBytes { get; }
        public long? AllocatedBytes { get; }
        public int FileCount { get; }
        public int HardLinkAliasCount { get; }
        public int TypeCount { get; }
        public List<StorageHistoryCategorySnapshot> Categories { get; } = [];

        public StorageHistorySnapshot Build() => new(
            Id,
            RootPath,
            CapturedAt,
            LogicalBytes,
            AllocatedBytes,
            FileCount,
            HardLinkAliasCount,
            TypeCount,
            Categories.ToArray());
    }
}
