using Microsoft.Data.Sqlite;

namespace FileOp.Core.Performance;

public sealed record IndexJournalCheckpointDiagnostics(
    ulong JournalId,
    long NextUsn,
    DateTimeOffset UpdatedAt);

public sealed record IndexJournalFreshnessDiagnostics(
    ulong DurableJournalId,
    long DurableNextUsn,
    DateTimeOffset DurableUpdatedAt,
    ulong LiveJournalId,
    long LowestValidUsn,
    long LiveNextUsn)
{
    public bool JournalIdentityMatches => DurableJournalId == LiveJournalId;

    public bool CheckpointWithinReadableWindow =>
        JournalIdentityMatches &&
        DurableNextUsn >= LowestValidUsn &&
        DurableNextUsn <= LiveNextUsn;

    public bool CheckpointBelowRetentionFloor =>
        JournalIdentityMatches && DurableNextUsn < LowestValidUsn;

    public bool CheckpointAheadOfJournal =>
        JournalIdentityMatches && DurableNextUsn > LiveNextUsn;

    public long? BacklogUsnDistance => CheckpointWithinReadableWindow
        ? SaturatingDistance(LiveNextUsn, DurableNextUsn)
        : null;

    public long? RetentionHeadroomUsnDistance => CheckpointWithinReadableWindow
        ? SaturatingDistance(DurableNextUsn, LowestValidUsn)
        : null;

    public TimeSpan AgeAt(DateTimeOffset capturedAt)
    {
        var age = capturedAt.ToUniversalTime() - DurableUpdatedAt.ToUniversalTime();
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }

    private static long SaturatingDistance(long high, long low)
    {
        if (high < low)
        {
            return 0;
        }

        try
        {
            return checked(high - low);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }
}

public sealed record IndexDatabaseDiagnostics(
    DateTimeOffset CapturedAt,
    int IndexedItemCount,
    long DatabaseFileBytes,
    long WalFileBytes,
    long SharedMemoryFileBytes,
    long PageSizeBytes,
    long PageCount,
    long FreePageCount,
    long CacheSizeSetting,
    string JournalMode,
    IndexJournalCheckpointDiagnostics? DurableCheckpoint = null,
    IndexJournalFreshnessDiagnostics? JournalFreshness = null)
{
    public long FileFootprintBytes => SaturatingAdd(
        SaturatingAdd(Math.Max(0, DatabaseFileBytes), Math.Max(0, WalFileBytes)),
        Math.Max(0, SharedMemoryFileBytes));

    public long LogicalDatabasePageBytes => SaturatingMultiply(PageSizeBytes, PageCount);

    public long ReusableFreePageBytes => SaturatingMultiply(PageSizeBytes, FreePageCount);

    public long LivePageBytes => Math.Max(0, LogicalDatabasePageBytes - ReusableFreePageBytes);

    public double? ReusableFreePagePercent => PageCount > 0
        ? Math.Clamp(FreePageCount * 100d / PageCount, 0d, 100d)
        : null;

    // FileOp currently does not set PRAGMA cache_size. This value therefore describes
    // the default target observed on the diagnostics reader connection. Positive values
    // are pages; negative values are an approximate KiB target. It is not resident RAM.
    public long? ReaderCacheDefaultTargetBytes
    {
        get
        {
            if (CacheSizeSetting == 0)
            {
                return null;
            }

            if (CacheSizeSetting < 0)
            {
                var kib = CacheSizeSetting == long.MinValue
                    ? long.MaxValue
                    : Math.Abs(CacheSizeSetting);
                return SaturatingMultiply(kib, 1_024);
            }

            return SaturatingMultiply(CacheSizeSetting, PageSizeBytes);
        }
    }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private static long SaturatingMultiply(long left, long right)
    {
        if (left <= 0 || right <= 0)
        {
            return 0;
        }

        return left > long.MaxValue / right ? long.MaxValue : left * right;
    }
}

public sealed class SqliteIndexDatabaseDiagnosticsReader
{
    private readonly string _databasePath;
    private readonly string _connectionString;

    public SqliteIndexDatabaseDiagnosticsReader(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 5,
        }.ToString();
    }

    public ValueTask<IndexDatabaseDiagnostics> ReadAsync(
        DateTimeOffset? capturedAt = null,
        CancellationToken cancellationToken = default) =>
        ReadCoreAsync(null, capturedAt, cancellationToken);

    public ValueTask<IndexDatabaseDiagnostics> ReadWithCheckpointAsync(
        string sourceKey,
        DateTimeOffset? capturedAt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        return ReadCoreAsync(sourceKey, capturedAt, cancellationToken);
    }

    private async ValueTask<IndexDatabaseDiagnostics> ReadCoreAsync(
        string? sourceKey,
        DateTimeOffset? capturedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA query_only = ON;";
            await setup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var pageSize = await ReadPragmaInt64Async(connection, "page_size", cancellationToken)
            .ConfigureAwait(false);
        var pageCount = await ReadPragmaInt64Async(connection, "page_count", cancellationToken)
            .ConfigureAwait(false);
        var freePageCount = await ReadPragmaInt64Async(connection, "freelist_count", cancellationToken)
            .ConfigureAwait(false);
        var cacheSize = await ReadPragmaInt64Async(connection, "cache_size", cancellationToken)
            .ConfigureAwait(false);
        var journalMode = await ReadPragmaTextAsync(connection, "journal_mode", cancellationToken)
            .ConfigureAwait(false);
        var indexedItemCount = await ReadIndexedItemCountAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        var checkpoint = sourceKey is null
            ? null
            : await ReadCheckpointAsync(connection, sourceKey, cancellationToken).ConfigureAwait(false);

        return new IndexDatabaseDiagnostics(
            (capturedAt ?? DateTimeOffset.UtcNow).ToUniversalTime(),
            indexedItemCount,
            ReadFileLength(_databasePath),
            ReadOptionalFileLength(_databasePath + "-wal"),
            ReadOptionalFileLength(_databasePath + "-shm"),
            NonNegative(pageSize, "page_size"),
            NonNegative(pageCount, "page_count"),
            NonNegative(freePageCount, "freelist_count"),
            cacheSize,
            journalMode,
            checkpoint);
    }

    private static async ValueTask<IndexJournalCheckpointDiagnostics?> ReadCheckpointAsync(
        SqliteConnection connection,
        string sourceKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT generation, position, updated_utc_ticks
            FROM source_checkpoints
            WHERE source_key = @source_key;
            """;
        command.Parameters.AddWithValue("@source_key", sourceKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var nextUsn = reader.GetInt64(1);
        if (nextUsn < 0)
        {
            throw new InvalidDataException($"Durable NTFS checkpoint USN {nextUsn} is invalid.");
        }

        var updatedTicks = reader.GetInt64(2);
        if (updatedTicks < DateTime.MinValue.Ticks || updatedTicks > DateTime.MaxValue.Ticks)
        {
            throw new InvalidDataException($"Durable NTFS checkpoint timestamp {updatedTicks} is invalid.");
        }

        return new IndexJournalCheckpointDiagnostics(
            FromSqlInteger(reader.GetInt64(0)),
            nextUsn,
            new DateTimeOffset(new DateTime(updatedTicks, DateTimeKind.Utc)));
    }

    private static async ValueTask<int> ReadIndexedItemCountAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT item_count FROM index_metadata WHERE id = 1;";
        var value = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
        if (value < 0 || value > int.MaxValue)
        {
            throw new InvalidDataException($"Index item count {value} is outside the supported range.");
        }

        return checked((int)value);
    }

    private static async ValueTask<long> ReadPragmaInt64Async(
        SqliteConnection connection,
        string name,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name};";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value ?? 0L);
    }

    private static async ValueTask<string> ReadPragmaTextAsync(
        SqliteConnection connection,
        string name,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name};";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToString(value) ?? string.Empty;
    }

    private static long ReadFileLength(string path)
    {
        var length = new FileInfo(path).Length;
        return Math.Max(0, length);
    }

    private static long ReadOptionalFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? Math.Max(0, new FileInfo(path).Length) : 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static long NonNegative(long value, string name)
    {
        if (value < 0)
        {
            throw new InvalidDataException($"SQLite PRAGMA {name} returned invalid negative value {value}.");
        }

        return value;
    }

    private static ulong FromSqlInteger(long value) => unchecked((ulong)value);
}
