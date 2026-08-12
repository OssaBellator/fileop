using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using Microsoft.Data.Sqlite;

namespace FileOp.Core.Operations;

/// <summary>
/// Read-only restart inspection for durable delete operations that crossed the mutation barrier.
/// Recovery discovery never reconstructs consent, authorizes retry, or infers that deletion occurred.
/// </summary>
public interface IFileDeleteOperationRecoveryHistoryReader : IAsyncDisposable
{
    ValueTask<IReadOnlyList<FileDeleteOperationActionHistory>> GetRecoveryCandidatesAsync(
        int limit = 100,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Opens the existing delete action-history database read-only and returns only operations
/// containing durable MutationStarted/RecoveryRequired entry evidence.
/// </summary>
public sealed class SqliteFileDeleteOperationRecoveryHistoryReader :
    IFileDeleteOperationRecoveryHistoryReader,
    IDisposable
{
    private const int SchemaVersion = 1;
    private const int MaximumRecoveryCandidateLimit = 4_096;

    private readonly string _connectionString;
    private bool _disposed;

    public SqliteFileDeleteOperationRecoveryHistoryReader(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = true,
            DefaultTimeout = 5,
        }.ToString();
    }

    public async ValueTask<IReadOnlyList<FileDeleteOperationActionHistory>> GetRecoveryCandidatesAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (limit <= 0 || limit > MaximumRecoveryCandidateLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                limit,
                $"Delete recovery-history limits must be between 1 and {MaximumRecoveryCandidateLimit:N0}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var connection = OpenReadOnlyConnection();
        using var transaction = connection.BeginTransaction(deferred: true);
        ValidateSchema(connection, transaction);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT operation.operation_id
            FROM file_delete_actions AS operation
            WHERE EXISTS (
                SELECT 1
                FROM file_delete_action_entries AS entry
                WHERE entry.operation_id = operation.operation_id
                  AND entry.state IN (@mutation_started_state, @recovery_required_state))
            ORDER BY operation.started_utc_ticks DESC, operation.operation_id DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue(
            "@mutation_started_state",
            (int)FileDeleteOperationActionEntryState.MutationStarted);
        command.Parameters.AddWithValue(
            "@recovery_required_state",
            (int)FileDeleteOperationActionEntryState.RecoveryRequired);
        command.Parameters.AddWithValue("@limit", limit);

        var operationIds = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                operationIds.Add(ParseId(reader.GetString(0), "operation id"));
            }
        }

        var histories = new List<FileDeleteOperationActionHistory>(operationIds.Count);
        foreach (var operationId in operationIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var history = await LoadRequiredAsync(
                connection,
                transaction,
                operationId,
                cancellationToken).ConfigureAwait(false);
            if (!history.IsRecoverySensitive || history.DeleteMutationAuthorized)
            {
                throw new InvalidDataException(
                    $"Delete recovery-history candidate {operationId:D} does not contain valid non-authorizing recovery evidence.");
            }

            histories.Add(history);
        }

        return histories.AsReadOnly();
    }

    public void Dispose()
    {
        _disposed = true;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private SqliteConnection OpenReadOnlyConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA query_only=ON; PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void ValidateSchema(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "SELECT version FROM file_delete_action_history_schema_info WHERE singleton = 1;";
            var value = command.ExecuteScalar();
            if (value is null || value is DBNull)
            {
                throw new InvalidDataException(
                    "Delete recovery-history reader requires an initialized delete action-history schema.");
            }

            if (Convert.ToInt32(value) != SchemaVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported delete action-history schema version {value}; expected {SchemaVersion}.");
            }
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException(
                "Delete recovery-history reader could not validate the existing action-history schema.",
                exception);
        }
    }

    private static async ValueTask<FileDeleteOperationActionHistory> LoadRequiredAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var history = await LoadAsync(connection, transaction, operationId, cancellationToken).ConfigureAwait(false);
        return history
            ?? throw new InvalidDataException(
                $"Delete recovery-history candidate {operationId:D} disappeared inside the read snapshot.");
    }

    private static async ValueTask<FileDeleteOperationActionHistory?> LoadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        using var operation = connection.CreateCommand();
        operation.Transaction = transaction;
        operation.CommandText = """
            SELECT authorization_id, queued_utc_ticks, validated_utc_ticks, authorized_utc_ticks,
                   started_utc_ticks, completed_utc_ticks, source_pane, source_tab_id,
                   canonical_source_directory_path, source_directory_volume_serial,
                   source_directory_file_reference, terminal_state
            FROM file_delete_actions
            WHERE operation_id = @operation_id;
            """;
        operation.Parameters.AddWithValue("@operation_id", FormatId(operationId));

        Guid authorizationId;
        DateTimeOffset queuedAt;
        DateTimeOffset validatedAt;
        DateTimeOffset authorizedAt;
        DateTimeOffset startedAt;
        DateTimeOffset? completedAt;
        string sourcePane;
        Guid sourceTabId;
        string rootPath;
        FileIdentity rootIdentity;
        FileDeleteOperationActionTerminalState? terminalState;
        await using (var reader = await operation.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            authorizationId = ParseId(reader.GetString(0), "authorization id");
            queuedAt = ReadUtcTicks(reader.GetInt64(1));
            validatedAt = ReadUtcTicks(reader.GetInt64(2));
            authorizedAt = ReadUtcTicks(reader.GetInt64(3));
            startedAt = ReadUtcTicks(reader.GetInt64(4));
            completedAt = reader.IsDBNull(5) ? null : ReadUtcTicks(reader.GetInt64(5));
            sourcePane = reader.GetString(6);
            sourceTabId = ParseId(reader.GetString(7), "source tab id");
            rootPath = reader.GetString(8);
            rootIdentity = new FileIdentity(
                FromSqliteInteger(reader.GetInt64(9)),
                FromSqliteInteger(reader.GetInt64(10)));
            terminalState = reader.IsDBNull(11)
                ? null
                : ReadEnum<FileDeleteOperationActionTerminalState>(reader.GetInt64(11), "terminal state");
        }

        using var entriesCommand = connection.CreateCommand();
        entriesCommand.Transaction = transaction;
        entriesCommand.CommandText = """
            SELECT ordinal, source_path, source_name, canonical_source_path,
                   source_volume_serial, source_file_reference, state,
                   mutation_started_utc_ticks, completed_utc_ticks,
                   failure_code, failure_message, failure_path, failure_retryable
            FROM file_delete_action_entries
            WHERE operation_id = @operation_id
            ORDER BY ordinal;
            """;
        entriesCommand.Parameters.AddWithValue("@operation_id", FormatId(operationId));

        var entries = new List<FileDeleteOperationActionEntry>();
        await using (var reader = await entriesCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var ordinal = checked((int)reader.GetInt64(0));
                entries.Add(new FileDeleteOperationActionEntry(
                    ordinal,
                    new FileOperationEntry(reader.GetString(1), reader.GetString(2), IsDirectory: false),
                    reader.GetString(3),
                    new FileIdentity(
                        FromSqliteInteger(reader.GetInt64(4)),
                        FromSqliteInteger(reader.GetInt64(5))),
                    ReadEnum<FileDeleteOperationActionEntryState>(reader.GetInt64(6), "entry state"),
                    reader.IsDBNull(7) ? null : ReadUtcTicks(reader.GetInt64(7)),
                    reader.IsDBNull(8) ? null : ReadUtcTicks(reader.GetInt64(8)),
                    ReadFailure(reader, 9)));
            }
        }

        try
        {
            return new FileDeleteOperationActionHistory(
                operationId,
                authorizationId,
                queuedAt,
                validatedAt,
                authorizedAt,
                startedAt,
                completedAt,
                sourcePane,
                sourceTabId,
                rootPath,
                rootIdentity,
                terminalState,
                entries);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"Persisted delete recovery-history candidate {operationId:D} is structurally invalid.",
                exception);
        }
    }

    private static FileOperationFailure? ReadFailure(SqliteDataReader reader, int startOrdinal)
    {
        if (reader.IsDBNull(startOrdinal))
        {
            if (!reader.IsDBNull(startOrdinal + 1) ||
                !reader.IsDBNull(startOrdinal + 2) ||
                !reader.IsDBNull(startOrdinal + 3))
            {
                throw new InvalidDataException(
                    "Persisted delete recovery-history failure columns are inconsistent.");
            }

            return null;
        }

        if (reader.IsDBNull(startOrdinal + 1) || reader.IsDBNull(startOrdinal + 3))
        {
            throw new InvalidDataException(
                "Persisted delete recovery-history failure is incomplete.");
        }

        var retryable = reader.GetInt64(startOrdinal + 3) switch
        {
            0 => false,
            1 => true,
            var invalid => throw new InvalidDataException(
                $"Persisted delete recovery-history retryable value {invalid} is invalid."),
        };
        var failure = new FileOperationFailure(
            reader.GetString(startOrdinal),
            reader.GetString(startOrdinal + 1),
            reader.IsDBNull(startOrdinal + 2) ? null : reader.GetString(startOrdinal + 2),
            retryable);
        try
        {
            FileDeleteOperationActionHistory.ValidateFailure(failure);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "Persisted delete recovery-history failure is invalid.",
                exception);
        }

        return failure;
    }

    private static TEnum ReadEnum<TEnum>(long value, string description)
        where TEnum : struct, Enum
    {
        if (value < int.MinValue ||
            value > int.MaxValue ||
            !Enum.IsDefined(typeof(TEnum), (int)value))
        {
            throw new InvalidDataException(
                $"Unknown delete recovery-history {description} value {value}.");
        }

        return (TEnum)Enum.ToObject(typeof(TEnum), (int)value);
    }

    private static string FormatId(Guid value) => value.ToString("D");

    private static Guid ParseId(string value, string description) =>
        Guid.TryParseExact(value, "D", out var parsed)
            ? parsed
            : throw new InvalidDataException(
                $"Persisted delete recovery-history {description} is invalid: {value}.");

    private static DateTimeOffset ReadUtcTicks(long ticks)
    {
        try
        {
            return new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException(
                "Persisted delete recovery-history UTC ticks are invalid.",
                exception);
        }
    }

    private static ulong FromSqliteInteger(long value) => unchecked((ulong)value);

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SqliteFileDeleteOperationRecoveryHistoryReader));
        }
    }
}
