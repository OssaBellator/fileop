using System.Globalization;
using System.Text;
using FileOp.Core.Models;
using Microsoft.Data.Sqlite;

namespace FileOp.Core.Search;

public sealed class SqliteFileIndex : IFileIndex, IIndexCheckpointStore, IDisposable
{
    private const int SchemaVersion = 1;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _disposed;

    public SqliteFileIndex(string databasePath)
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

    public int Count
    {
        get
        {
            ThrowIfDisposed();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT item_count FROM index_metadata WHERE id = 1;";
            return checked((int)(long)(command.ExecuteScalar() ?? 0L));
        }
    }

    public async ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM files;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask AddBatchAsync(
        IReadOnlyList<FileRecord> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        ThrowIfDisposed();

        if (records.Count == 0)
        {
            return;
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = CreateUpsertCommand(connection, transaction);

            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BindRecord(command, record);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            transaction.Commit();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask ApplyChangesAsync(
        IReadOnlyList<FileIndexChange> changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ThrowIfDisposed();

        if (changes.Count == 0)
        {
            return;
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var upsert = CreateUpsertCommand(connection, transaction);
            using var delete = CreateDeleteCommand(connection, transaction);

            foreach (var change in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                switch (change.Kind)
                {
                    case FileIndexChangeKind.Upsert when change.Record is { } record:
                        BindRecord(upsert, record);
                        await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                        break;

                    case FileIndexChangeKind.Delete when change.Identity is { } identity:
                        delete.Parameters["@volume_serial"].Value = ToSqlInteger(identity.VolumeSerialNumber);
                        delete.Parameters["@file_reference"].Value = ToSqlInteger(identity.FileReferenceNumber);
                        delete.Parameters["@path"].Value = change.Path is null ? DBNull.Value : change.Path;
                        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                        break;

                    default:
                        throw new InvalidOperationException(
                            "The file-index change is missing the data required by its change kind.");
                }
            }

            transaction.Commit();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<FileRecord>> SearchAsync(
        FileSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        BuildSearchCommand(command, query);

        var records = new List<FileRecord>(Math.Min(query.Limit, 256));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(ReadRecord(reader));
        }

        return records;
    }

    public async ValueTask<IndexSourceCheckpoint?> GetCheckpointAsync(
        string sourceKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        ThrowIfDisposed();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT generation, position, updated_utc_ticks
            FROM source_checkpoints
            WHERE source_key = @source_key;
            """;
        command.Parameters.AddWithValue("@source_key", sourceKey);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new IndexSourceCheckpoint(
            sourceKey,
            FromSqlInteger(reader.GetInt64(0)),
            reader.GetInt64(1),
            FromUtcTicks(reader.GetInt64(2)));
    }

    public async ValueTask SaveCheckpointAsync(
        IndexSourceCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpoint.SourceKey);
        ThrowIfDisposed();

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO source_checkpoints(source_key, generation, position, updated_utc_ticks)
                VALUES (@source_key, @generation, @position, @updated_utc_ticks)
                ON CONFLICT(source_key) DO UPDATE SET
                    generation = excluded.generation,
                    position = excluded.position,
                    updated_utc_ticks = excluded.updated_utc_ticks;
                """;
            command.Parameters.AddWithValue("@source_key", checkpoint.SourceKey);
            command.Parameters.AddWithValue("@generation", ToSqlInteger(checkpoint.Generation));
            command.Parameters.AddWithValue("@position", checkpoint.Position);
            command.Parameters.AddWithValue("@updated_utc_ticks", checkpoint.UpdatedAt.UtcDateTime.Ticks);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DeleteCheckpointAsync(
        string sourceKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        ThrowIfDisposed();

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM source_checkpoints WHERE source_key = @source_key;";
            command.Parameters.AddWithValue("@source_key", sourceKey);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $$"""
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;

            CREATE TABLE IF NOT EXISTS schema_info(
                id INTEGER PRIMARY KEY CHECK(id = 1),
                version INTEGER NOT NULL
            );
            INSERT OR IGNORE INTO schema_info(id, version) VALUES (1, {{SchemaVersion}});

            CREATE TABLE IF NOT EXISTS index_metadata(
                id INTEGER PRIMARY KEY CHECK(id = 1),
                item_count INTEGER NOT NULL
            );
            INSERT OR IGNORE INTO index_metadata(id, item_count) VALUES (1, 0);

            CREATE TABLE IF NOT EXISTS files(
                path TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
                path_norm TEXT NOT NULL,
                name TEXT NOT NULL,
                name_norm TEXT NOT NULL,
                parent_path TEXT NOT NULL,
                extension TEXT NOT NULL,
                extension_norm TEXT NOT NULL,
                length INTEGER NOT NULL,
                allocated_length INTEGER NULL,
                is_directory INTEGER NOT NULL,
                last_write_utc_ticks INTEGER NOT NULL,
                attributes INTEGER NOT NULL,
                volume_serial INTEGER NULL,
                file_reference INTEGER NULL,
                parent_volume_serial INTEGER NULL,
                parent_file_reference INTEGER NULL
            ) WITHOUT ROWID;

            CREATE INDEX IF NOT EXISTS ix_files_identity
                ON files(volume_serial, file_reference);
            CREATE INDEX IF NOT EXISTS ix_files_parent_identity
                ON files(parent_volume_serial, parent_file_reference);
            CREATE INDEX IF NOT EXISTS ix_files_extension
                ON files(extension_norm);
            CREATE INDEX IF NOT EXISTS ix_files_name
                ON files(name_norm);

            CREATE TRIGGER IF NOT EXISTS files_count_insert
            AFTER INSERT ON files
            BEGIN
                UPDATE index_metadata SET item_count = item_count + 1 WHERE id = 1;
            END;

            CREATE TRIGGER IF NOT EXISTS files_count_delete
            AFTER DELETE ON files
            BEGIN
                UPDATE index_metadata SET item_count = item_count - 1 WHERE id = 1;
            END;

            CREATE TABLE IF NOT EXISTS source_checkpoints(
                source_key TEXT NOT NULL PRIMARY KEY,
                generation INTEGER NOT NULL,
                position INTEGER NOT NULL,
                updated_utc_ticks INTEGER NOT NULL
            ) WITHOUT ROWID;
            """;
        command.ExecuteNonQuery();

        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT version FROM schema_info WHERE id = 1;";
        var version = Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version != SchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported FileOp index schema version {version}; expected {SchemaVersion}.");
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA synchronous = NORMAL;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static SqliteCommand CreateUpsertCommand(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO files(
                path, path_norm, name, name_norm, parent_path, extension, extension_norm,
                length, allocated_length, is_directory, last_write_utc_ticks, attributes,
                volume_serial, file_reference, parent_volume_serial, parent_file_reference)
            VALUES (
                @path, @path_norm, @name, @name_norm, @parent_path, @extension, @extension_norm,
                @length, @allocated_length, @is_directory, @last_write_utc_ticks, @attributes,
                @volume_serial, @file_reference, @parent_volume_serial, @parent_file_reference)
            ON CONFLICT(path) DO UPDATE SET
                path_norm = excluded.path_norm,
                name = excluded.name,
                name_norm = excluded.name_norm,
                parent_path = excluded.parent_path,
                extension = excluded.extension,
                extension_norm = excluded.extension_norm,
                length = excluded.length,
                allocated_length = excluded.allocated_length,
                is_directory = excluded.is_directory,
                last_write_utc_ticks = excluded.last_write_utc_ticks,
                attributes = excluded.attributes,
                volume_serial = excluded.volume_serial,
                file_reference = excluded.file_reference,
                parent_volume_serial = excluded.parent_volume_serial,
                parent_file_reference = excluded.parent_file_reference;
            """;
        AddRecordParameters(command);
        return command;
    }

    private static SqliteCommand CreateDeleteCommand(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM files
            WHERE volume_serial = @volume_serial
              AND file_reference = @file_reference
              AND (@path IS NULL OR path = @path COLLATE NOCASE);
            """;
        command.Parameters.Add("@volume_serial", SqliteType.Integer);
        command.Parameters.Add("@file_reference", SqliteType.Integer);
        command.Parameters.Add("@path", SqliteType.Text);
        return command;
    }

    private static void AddRecordParameters(SqliteCommand command)
    {
        command.Parameters.Add("@path", SqliteType.Text);
        command.Parameters.Add("@path_norm", SqliteType.Text);
        command.Parameters.Add("@name", SqliteType.Text);
        command.Parameters.Add("@name_norm", SqliteType.Text);
        command.Parameters.Add("@parent_path", SqliteType.Text);
        command.Parameters.Add("@extension", SqliteType.Text);
        command.Parameters.Add("@extension_norm", SqliteType.Text);
        command.Parameters.Add("@length", SqliteType.Integer);
        command.Parameters.Add("@allocated_length", SqliteType.Integer);
        command.Parameters.Add("@is_directory", SqliteType.Integer);
        command.Parameters.Add("@last_write_utc_ticks", SqliteType.Integer);
        command.Parameters.Add("@attributes", SqliteType.Integer);
        command.Parameters.Add("@volume_serial", SqliteType.Integer);
        command.Parameters.Add("@file_reference", SqliteType.Integer);
        command.Parameters.Add("@parent_volume_serial", SqliteType.Integer);
        command.Parameters.Add("@parent_file_reference", SqliteType.Integer);
    }

    private static void BindRecord(SqliteCommand command, FileRecord record)
    {
        command.Parameters["@path"].Value = record.Path;
        command.Parameters["@path_norm"].Value = record.Path.ToLowerInvariant();
        command.Parameters["@name"].Value = record.Name;
        command.Parameters["@name_norm"].Value = record.Name.ToLowerInvariant();
        command.Parameters["@parent_path"].Value = record.ParentPath;
        command.Parameters["@extension"].Value = record.Extension;
        command.Parameters["@extension_norm"].Value = record.Extension.TrimStart('.').ToLowerInvariant();
        command.Parameters["@length"].Value = record.Length;
        command.Parameters["@allocated_length"].Value = record.AllocatedLength is { } allocated
            ? allocated
            : DBNull.Value;
        command.Parameters["@is_directory"].Value = record.IsDirectory ? 1 : 0;
        command.Parameters["@last_write_utc_ticks"].Value = record.LastWriteTime.UtcDateTime.Ticks;
        command.Parameters["@attributes"].Value = (long)record.Attributes;
        BindIdentity(command, string.Empty, record.Identity);
        BindIdentity(command, "parent_", record.ParentIdentity);
    }

    private static void BindIdentity(SqliteCommand command, string prefix, FileIdentity? identity)
    {
        command.Parameters[$"@{prefix}volume_serial"].Value = identity is { } value
            ? ToSqlInteger(value.VolumeSerialNumber)
            : DBNull.Value;
        command.Parameters[$"@{prefix}file_reference"].Value = identity is { } value
            ? ToSqlInteger(value.FileReferenceNumber)
            : DBNull.Value;
    }

    private static void BuildSearchCommand(SqliteCommand command, FileSearchQuery query)
    {
        var where = new List<string>();
        var score = new List<string>();

        for (var index = 0; index < query.Terms.Count; index++)
        {
            var parameterName = $"@term{index}";
            command.Parameters.AddWithValue(parameterName, query.Terms[index].ToLowerInvariant());
            where.Add($"(instr(name_norm, {parameterName}) > 0 OR instr(path_norm, {parameterName}) > 0)");
            score.Add(
                $"CASE WHEN name_norm = {parameterName} THEN 100 " +
                $"WHEN name_norm LIKE {parameterName} || '%' THEN 50 " +
                $"WHEN instr(name_norm, {parameterName}) > 0 THEN 20 ELSE 5 END");
        }

        if (query.Extensions.Count > 0)
        {
            var extensionParameters = new List<string>(query.Extensions.Count);
            var index = 0;
            foreach (var extension in query.Extensions)
            {
                var parameterName = $"@ext{index++}";
                extensionParameters.Add(parameterName);
                command.Parameters.AddWithValue(parameterName, extension.ToLowerInvariant());
            }

            where.Add($"extension_norm IN ({string.Join(", ", extensionParameters)})");
        }

        if (query.ExactSize.HasValue || query.MinimumSize.HasValue || query.MaximumSize.HasValue)
        {
            where.Add("is_directory = 0");
        }

        if (query.ExactSize is { } exactSize)
        {
            where.Add("length = @exact_size");
            command.Parameters.AddWithValue("@exact_size", exactSize);
        }

        if (query.MinimumSize is { } minimumSize)
        {
            where.Add("length > @minimum_size");
            command.Parameters.AddWithValue("@minimum_size", minimumSize);
        }

        if (query.MaximumSize is { } maximumSize)
        {
            where.Add("length < @maximum_size");
            command.Parameters.AddWithValue("@maximum_size", maximumSize);
        }

        command.Parameters.AddWithValue("@limit", query.Limit);

        var sql = new StringBuilder(
            "SELECT path, name, parent_path, extension, length, is_directory, " +
            "last_write_utc_ticks, attributes, volume_serial, file_reference, " +
            "parent_volume_serial, parent_file_reference, allocated_length, ");
        sql.Append(score.Count == 0 ? "0" : string.Join(" + ", score));
        sql.Append(" AS score FROM files");

        if (where.Count > 0)
        {
            sql.Append(" WHERE ").Append(string.Join(" AND ", where));
        }

        sql.Append(" ORDER BY score DESC, name_norm ASC LIMIT @limit;");
        command.CommandText = sql.ToString();
    }

    private static FileRecord ReadRecord(SqliteDataReader reader)
    {
        var identity = ReadIdentity(reader, 8, 9);
        var parentIdentity = ReadIdentity(reader, 10, 11);
        long? allocatedLength = reader.IsDBNull(12) ? null : reader.GetInt64(12);

        return new FileRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetInt64(5) != 0,
            FromUtcTicks(reader.GetInt64(6)),
            (FileAttributes)reader.GetInt64(7),
            identity,
            parentIdentity,
            allocatedLength);
    }

    private static FileIdentity? ReadIdentity(
        SqliteDataReader reader,
        int volumeIndex,
        int referenceIndex)
    {
        if (reader.IsDBNull(volumeIndex) || reader.IsDBNull(referenceIndex))
        {
            return null;
        }

        return new FileIdentity(
            FromSqlInteger(reader.GetInt64(volumeIndex)),
            FromSqlInteger(reader.GetInt64(referenceIndex)));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static long ToSqlInteger(ulong value) => unchecked((long)value);

    private static ulong FromSqlInteger(long value) => unchecked((ulong)value);

    private static DateTimeOffset FromUtcTicks(long ticks) =>
        new(new DateTime(ticks, DateTimeKind.Utc));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writeGate.Dispose();
    }
}
