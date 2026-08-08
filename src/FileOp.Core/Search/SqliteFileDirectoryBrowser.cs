using FileOp.Core.Models;
using Microsoft.Data.Sqlite;

namespace FileOp.Core.Search;

public sealed class SqliteFileDirectoryBrowser : IFileDirectoryBrowser
{
    private const int MaximumPageSize = 4_096;
    private readonly string _connectionString;

    public SqliteFileDirectoryBrowser(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 5,
        }.ToString();
    }

    public async ValueTask<FileDirectoryBrowsePage> BrowseDirectoryAsync(
        string directoryPath,
        int pageSize = 256,
        FileDirectoryBrowseCursor? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        if (pageSize <= 0 || pageSize > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                pageSize,
                $"Directory browse page sizes must be between 1 and {MaximumPageSize:N0}.");
        }

        ValidateCursor(cursor);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedDirectory = NormalizeIndexedPath(directoryPath);
        using var connection = OpenConnection();
        var parentIdentity = await TryGetDirectoryIdentityAsync(
            connection,
            normalizedDirectory,
            cancellationToken).ConfigureAwait(false);

        var totalCount = await CountDirectChildrenAsync(
            connection,
            normalizedDirectory,
            parentIdentity,
            cancellationToken).ConfigureAwait(false);
        var entries = await ReadPageAsync(
            connection,
            normalizedDirectory,
            parentIdentity,
            pageSize,
            cursor,
            cancellationToken).ConfigureAwait(false);

        FileDirectoryBrowseCursor? nextCursor = null;
        if (entries.Count > pageSize)
        {
            entries.RemoveAt(entries.Count - 1);
            var last = entries[^1];
            nextCursor = new FileDirectoryBrowseCursor(last.IsDirectory, last.Name, last.Path);
        }

        return new FileDirectoryBrowsePage(
            normalizedDirectory,
            totalCount,
            entries,
            nextCursor);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA query_only = ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static async ValueTask<FileIdentity?> TryGetDirectoryIdentityAsync(
        SqliteConnection connection,
        string directoryPath,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT volume_serial, file_reference
            FROM files
            WHERE path = @directory COLLATE NOCASE
              AND is_directory = 1
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@directory", directoryPath);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.IsDBNull(0) ||
            reader.IsDBNull(1))
        {
            return null;
        }

        return new FileIdentity(
            FromSqlInteger(reader.GetInt64(0)),
            FromSqlInteger(reader.GetInt64(1)));
    }

    private static async ValueTask<int> CountDirectChildrenAsync(
        SqliteConnection connection,
        string directoryPath,
        FileIdentity? parentIdentity,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = parentIdentity.HasValue
            ? """
                SELECT COUNT(*)
                FROM files
                WHERE parent_volume_serial = @parent_volume_serial
                  AND parent_file_reference = @parent_file_reference;
                """
            : """
                SELECT COUNT(*)
                FROM files
                WHERE parent_path = @directory COLLATE NOCASE;
                """;
        BindParent(command, directoryPath, parentIdentity);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return checked((int)Convert.ToInt64(value ?? 0L));
    }

    private static async ValueTask<List<FileRecord>> ReadPageAsync(
        SqliteConnection connection,
        string directoryPath,
        FileIdentity? parentIdentity,
        int pageSize,
        FileDirectoryBrowseCursor? cursor,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        var parentPredicate = parentIdentity.HasValue
            ? "parent_volume_serial = @parent_volume_serial AND parent_file_reference = @parent_file_reference"
            : "parent_path = @directory COLLATE NOCASE";
        var cursorPredicate = cursor is null
            ? string.Empty
            : """
                AND (
                    is_directory < @cursor_is_directory
                    OR (
                        is_directory = @cursor_is_directory
                        AND (
                            name_norm > @cursor_name_norm
                            OR (name_norm = @cursor_name_norm AND path_norm > @cursor_path_norm)
                        )
                    )
                )
                """;

        command.CommandText = $"""
            SELECT path, name, parent_path, extension, length, is_directory,
                   last_write_utc_ticks, attributes, volume_serial, file_reference,
                   parent_volume_serial, parent_file_reference, allocated_length
            FROM files
            WHERE {parentPredicate}
            {cursorPredicate}
            ORDER BY is_directory DESC, name_norm ASC, path_norm ASC
            LIMIT @take;
            """;
        BindParent(command, directoryPath, parentIdentity);
        command.Parameters.AddWithValue("@take", checked(pageSize + 1));
        if (cursor is not null)
        {
            command.Parameters.AddWithValue("@cursor_is_directory", cursor.IsDirectory ? 1 : 0);
            command.Parameters.AddWithValue("@cursor_name_norm", cursor.Name.ToLowerInvariant());
            command.Parameters.AddWithValue("@cursor_path_norm", cursor.Path.ToLowerInvariant());
        }

        var records = new List<FileRecord>(pageSize + 1);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(ReadRecord(reader));
        }

        return records;
    }

    private static void BindParent(
        SqliteCommand command,
        string directoryPath,
        FileIdentity? parentIdentity)
    {
        if (parentIdentity is { } identity)
        {
            command.Parameters.AddWithValue("@parent_volume_serial", ToSqlInteger(identity.VolumeSerialNumber));
            command.Parameters.AddWithValue("@parent_file_reference", ToSqlInteger(identity.FileReferenceNumber));
            return;
        }

        command.Parameters.AddWithValue("@directory", directoryPath);
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

    private static void ValidateCursor(FileDirectoryBrowseCursor? cursor)
    {
        if (cursor is null)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(cursor.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(cursor.Path);
        if (!Path.IsPathFullyQualified(cursor.Path))
        {
            throw new ArgumentException("Directory browse cursor paths must be absolute.", nameof(cursor));
        }
    }

    private static string NormalizeIndexedPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(root) &&
            string.Equals(
                fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static long ToSqlInteger(ulong value) => unchecked((long)value);

    private static ulong FromSqlInteger(long value) => unchecked((ulong)value);

    private static DateTimeOffset FromUtcTicks(long ticks) =>
        new(new DateTime(ticks, DateTimeKind.Utc));
}
