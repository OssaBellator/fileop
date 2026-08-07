using System.Globalization;
using FileOp.Core.Models;
using FileOp.Core.Search;
using Microsoft.Data.Sqlite;

namespace FileOp.Windows.Ntfs;

public sealed class NtfsSqliteNamespaceStore : IDisposable
{
    private const int ExpectedSchemaVersion = 1;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _disposed;

    public NtfsSqliteNamespaceStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var fullPath = Path.GetFullPath(databasePath);
        using (var bootstrap = new SqliteFileIndex(fullPath))
        {
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 5,
        }.ToString();

        VerifySchema();
    }

    public async ValueTask<IReadOnlyList<FileRecord>> FindByIdentityAsync(
        FileIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectRecordColumns + """
            FROM files
            WHERE volume_serial = @volume_serial
              AND file_reference = @file_reference
            ORDER BY path_norm;
            """;
        command.Parameters.AddWithValue("@volume_serial", ToSqlInteger(identity.VolumeSerialNumber));
        command.Parameters.AddWithValue("@file_reference", ToSqlInteger(identity.FileReferenceNumber));
        return await ReadRecordsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<FileRecord>> FindByParentAndNameAsync(
        FileIdentity parentIdentity,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ThrowIfDisposed();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectRecordColumns + """
            FROM files
            WHERE parent_volume_serial = @parent_volume_serial
              AND parent_file_reference = @parent_file_reference
              AND name = @name COLLATE NOCASE
            ORDER BY path_norm;
            """;
        command.Parameters.AddWithValue("@parent_volume_serial", ToSqlInteger(parentIdentity.VolumeSerialNumber));
        command.Parameters.AddWithValue("@parent_file_reference", ToSqlInteger(parentIdentity.FileReferenceNumber));
        command.Parameters.AddWithValue("@name", name);
        return await ReadRecordsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ApplyAsync(
        IReadOnlyList<NtfsIndexMutation> mutations,
        string sourceKey,
        NtfsJournalCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        ThrowIfDisposed();

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var upsert = CreateUpsertCommand(connection, transaction);
            using var delete = CreateDeleteCommand(connection, transaction);
            using var deleteSubtree = CreateDeleteSubtreeCommand(connection, transaction);
            using var moveSubtree = CreateMoveSubtreeCommand(connection, transaction);
            using var deleteDestination = CreateDeleteDestinationCommand(connection, transaction);

            foreach (var mutation in mutations)
            {
                cancellationToken.ThrowIfCancellationRequested();

                switch (mutation.Kind)
                {
                    case NtfsIndexMutationKind.Upsert when mutation.Record is { } record:
                        BindRecord(upsert, record);
                        await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                        break;

                    case NtfsIndexMutationKind.Delete
                        when mutation.Identity is { } identity && mutation.Path is { } path:
                        if (mutation.DeleteSubtree)
                        {
                            BindPath(deleteSubtree, path);
                            await deleteSubtree.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            delete.Parameters["@volume_serial"].Value = ToSqlInteger(identity.VolumeSerialNumber);
                            delete.Parameters["@file_reference"].Value = ToSqlInteger(identity.FileReferenceNumber);
                            delete.Parameters["@path"].Value = path;
                            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                        }

                        break;

                    case NtfsIndexMutationKind.Move
                        when mutation.Record is { } newRecord && mutation.OldPath is { } oldPath:
                        await MoveAsync(
                            oldPath,
                            newRecord,
                            deleteDestination,
                            moveSubtree,
                            upsert,
                            cancellationToken).ConfigureAwait(false);
                        break;

                    default:
                        throw new InvalidOperationException("The NTFS index mutation is missing required data.");
                }
            }

            using var checkpointCommand = connection.CreateCommand();
            checkpointCommand.Transaction = transaction;
            checkpointCommand.CommandText = """
                INSERT INTO source_checkpoints(source_key, generation, position, updated_utc_ticks)
                VALUES (@source_key, @generation, @position, @updated_utc_ticks)
                ON CONFLICT(source_key) DO UPDATE SET
                    generation = excluded.generation,
                    position = excluded.position,
                    updated_utc_ticks = excluded.updated_utc_ticks;
                """;
            checkpointCommand.Parameters.AddWithValue("@source_key", sourceKey);
            checkpointCommand.Parameters.AddWithValue("@generation", ToSqlInteger(checkpoint.JournalId));
            checkpointCommand.Parameters.AddWithValue("@position", checkpoint.NextUsn);
            checkpointCommand.Parameters.AddWithValue("@updated_utc_ticks", DateTime.UtcNow.Ticks);
            await checkpointCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            transaction.Commit();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async ValueTask MoveAsync(
        string oldPath,
        FileRecord newRecord,
        SqliteCommand deleteDestination,
        SqliteCommand moveSubtree,
        SqliteCommand upsert,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(oldPath, newRecord.Path, StringComparison.OrdinalIgnoreCase))
        {
            BindPath(deleteDestination, newRecord.Path);
            await deleteDestination.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        moveSubtree.Parameters["@old_path"].Value = oldPath;
        moveSubtree.Parameters["@new_path"].Value = newRecord.Path;
        moveSubtree.Parameters["@new_parent"].Value = newRecord.ParentPath;
        moveSubtree.Parameters["@separator"].Value = Path.DirectorySeparatorChar.ToString();
        await moveSubtree.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        BindRecord(upsert, newRecord);
        await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void VerifySchema()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_info WHERE id = 1;";
        var version = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version != ExpectedSchemaVersion)
        {
            throw new InvalidDataException(
                $"NTFS namespace synchronization requires FileOp index schema {ExpectedSchemaVersion}, but found {version}.");
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

    private static async ValueTask<IReadOnlyList<FileRecord>> ReadRecordsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var records = new List<FileRecord>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(ReadRecord(reader));
        }

        return records;
    }

    private static SqliteCommand CreateUpsertCommand(SqliteConnection connection, SqliteTransaction transaction)
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
        return command;
    }

    private static SqliteCommand CreateDeleteCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM files
            WHERE volume_serial = @volume_serial
              AND file_reference = @file_reference
              AND path = @path COLLATE NOCASE;
            """;
        command.Parameters.Add("@volume_serial", SqliteType.Integer);
        command.Parameters.Add("@file_reference", SqliteType.Integer);
        command.Parameters.Add("@path", SqliteType.Text);
        return command;
    }

    private static SqliteCommand CreateDeleteSubtreeCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM files
            WHERE path = @path COLLATE NOCASE
               OR (substr(path, 1, length(@path)) = @path COLLATE NOCASE
                   AND substr(path, length(@path) + 1, 1) = @separator);
            """;
        command.Parameters.Add("@path", SqliteType.Text);
        command.Parameters.Add("@separator", SqliteType.Text);
        return command;
    }

    private static SqliteCommand CreateDeleteDestinationCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM files
            WHERE path = @path COLLATE NOCASE
               OR (substr(path, 1, length(@path)) = @path COLLATE NOCASE
                   AND substr(path, length(@path) + 1, 1) = @separator);
            """;
        command.Parameters.Add("@path", SqliteType.Text);
        command.Parameters.Add("@separator", SqliteType.Text);
        return command;
    }

    private static SqliteCommand CreateMoveSubtreeCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE files
            SET path = @new_path || substr(path, length(@old_path) + 1),
                path_norm = lower(@new_path || substr(path, length(@old_path) + 1)),
                parent_path = CASE
                    WHEN path = @old_path COLLATE NOCASE THEN @new_parent
                    WHEN parent_path = @old_path COLLATE NOCASE
                         OR (substr(parent_path, 1, length(@old_path)) = @old_path COLLATE NOCASE
                             AND substr(parent_path, length(@old_path) + 1, 1) = @separator)
                    THEN @new_path || substr(parent_path, length(@old_path) + 1)
                    ELSE parent_path
                END
            WHERE path = @old_path COLLATE NOCASE
               OR (substr(path, 1, length(@old_path)) = @old_path COLLATE NOCASE
                   AND substr(path, length(@old_path) + 1, 1) = @separator);
            """;
        command.Parameters.Add("@old_path", SqliteType.Text);
        command.Parameters.Add("@new_path", SqliteType.Text);
        command.Parameters.Add("@new_parent", SqliteType.Text);
        command.Parameters.Add("@separator", SqliteType.Text);
        return command;
    }

    private static void BindPath(SqliteCommand command, string path)
    {
        command.Parameters["@path"].Value = path;
        command.Parameters["@separator"].Value = Path.DirectorySeparatorChar.ToString();
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
        if (identity is { } value)
        {
            command.Parameters[$"@{prefix}volume_serial"].Value = ToSqlInteger(value.VolumeSerialNumber);
            command.Parameters[$"@{prefix}file_reference"].Value = ToSqlInteger(value.FileReferenceNumber);
            return;
        }

        command.Parameters[$"@{prefix}volume_serial"].Value = DBNull.Value;
        command.Parameters[$"@{prefix}file_reference"].Value = DBNull.Value;
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

    private static FileIdentity? ReadIdentity(SqliteDataReader reader, int volumeIndex, int referenceIndex)
    {
        if (reader.IsDBNull(volumeIndex) || reader.IsDBNull(referenceIndex))
        {
            return null;
        }

        return new FileIdentity(
            FromSqlInteger(reader.GetInt64(volumeIndex)),
            FromSqlInteger(reader.GetInt64(referenceIndex)));
    }

    private static long ToSqlInteger(ulong value) => unchecked((long)value);

    private static ulong FromSqlInteger(long value) => unchecked((ulong)value);

    private static DateTimeOffset FromUtcTicks(long ticks) => new(new DateTime(ticks, DateTimeKind.Utc));

    private const string SelectRecordColumns =
        "SELECT path, name, parent_path, extension, length, is_directory, " +
        "last_write_utc_ticks, attributes, volume_serial, file_reference, " +
        "parent_volume_serial, parent_file_reference, allocated_length ";

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

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
