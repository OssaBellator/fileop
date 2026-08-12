using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using Microsoft.Data.Sqlite;

namespace FileOp.Core.Operations;

public sealed class SqliteFileDeleteOperationActionHistoryStore : IFileDeleteOperationActionHistoryStore, IDisposable
{
    private const int SchemaVersion = 1;

    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _disposed;

    public SqliteFileDeleteOperationActionHistoryStore(
        string databasePath,
        TimeProvider? timeProvider = null)
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
        _timeProvider = timeProvider ?? TimeProvider.System;
        Initialize();
    }

    public async ValueTask<FileDeleteOperationActionHistory> BeginAsync(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ThrowIfDisposed();
        ValidateBegin(authorization);

        var startedAt = _timeProvider.GetUtcNow();
        var operationKey = FormatId(authorization.PlanId);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            using (var operation = connection.CreateCommand())
            {
                operation.Transaction = transaction;
                operation.CommandText = """
                    INSERT INTO file_delete_actions(
                        operation_id,
                        authorization_id,
                        queued_utc_ticks,
                        validated_utc_ticks,
                        authorized_utc_ticks,
                        started_utc_ticks,
                        completed_utc_ticks,
                        source_pane,
                        source_tab_id,
                        canonical_source_directory_path,
                        source_directory_volume_serial,
                        source_directory_file_reference,
                        terminal_state)
                    VALUES(
                        @operation_id,
                        @authorization_id,
                        @queued,
                        @validated,
                        @authorized,
                        @started,
                        NULL,
                        @source_pane,
                        @source_tab_id,
                        @root_path,
                        @root_volume,
                        @root_reference,
                        NULL);
                    """;
                operation.Parameters.AddWithValue("@operation_id", operationKey);
                operation.Parameters.AddWithValue("@authorization_id", FormatId(authorization.AuthorizationId));
                operation.Parameters.AddWithValue("@queued", ToUtcTicks(authorization.Plan.QueuedAtUtc));
                operation.Parameters.AddWithValue("@validated", ToUtcTicks(authorization.ValidatedAtUtc));
                operation.Parameters.AddWithValue("@authorized", ToUtcTicks(authorization.AuthorizedAtUtc));
                operation.Parameters.AddWithValue("@started", ToUtcTicks(startedAt));
                operation.Parameters.AddWithValue("@source_pane", authorization.Plan.Intent.SourcePane);
                operation.Parameters.AddWithValue("@source_tab_id", FormatId(authorization.Plan.Intent.SourceTabId));
                operation.Parameters.AddWithValue("@root_path", authorization.CanonicalSourceDirectoryPath);
                operation.Parameters.AddWithValue("@root_volume", ToSqliteInteger(authorization.SourceDirectoryIdentity.VolumeSerialNumber));
                operation.Parameters.AddWithValue("@root_reference", ToSqliteInteger(authorization.SourceDirectoryIdentity.FileReferenceNumber));
                if (await operation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException("Delete action-history operation insert did not affect exactly one row.");
                }
            }

            for (var ordinal = 0; ordinal < authorization.Items.Count; ordinal++)
            {
                var item = authorization.Items[ordinal];
                using var entry = connection.CreateCommand();
                entry.Transaction = transaction;
                entry.CommandText = """
                    INSERT INTO file_delete_action_entries(
                        operation_id,
                        ordinal,
                        source_path,
                        source_name,
                        canonical_source_path,
                        source_volume_serial,
                        source_file_reference,
                        state,
                        mutation_started_utc_ticks,
                        completed_utc_ticks,
                        failure_code,
                        failure_message,
                        failure_path,
                        failure_retryable)
                    VALUES(
                        @operation_id,
                        @ordinal,
                        @source_path,
                        @source_name,
                        @canonical_source_path,
                        @source_volume,
                        @source_reference,
                        @state,
                        NULL,
                        NULL,
                        NULL,
                        NULL,
                        NULL,
                        NULL);
                    """;
                entry.Parameters.AddWithValue("@operation_id", operationKey);
                entry.Parameters.AddWithValue("@ordinal", ordinal);
                entry.Parameters.AddWithValue("@source_path", item.Entry.Path);
                entry.Parameters.AddWithValue("@source_name", item.Entry.Name);
                entry.Parameters.AddWithValue("@canonical_source_path", item.CanonicalPath);
                entry.Parameters.AddWithValue("@source_volume", ToSqliteInteger(item.Identity.VolumeSerialNumber));
                entry.Parameters.AddWithValue("@source_reference", ToSqliteInteger(item.Identity.FileReferenceNumber));
                entry.Parameters.AddWithValue("@state", (int)FileDeleteOperationActionEntryState.Pending);
                if (await entry.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException("Delete action-history entry insert did not affect exactly one row.");
                }
            }

            var history = await LoadRequiredAsync(
                connection,
                transaction,
                authorization.PlanId,
                cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return history;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ValueTask<FileDeleteOperationActionHistory> MarkMutationStartedAsync(
        Guid operationId,
        int ordinal,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            operationId,
            ordinal,
            FileDeleteOperationActionEntryState.Pending,
            FileDeleteOperationActionEntryState.MutationStarted,
            failure: null,
            expectedIdentity: null,
            cancellationToken);

    public ValueTask<FileDeleteOperationActionHistory> CommitDeletedAsync(
        Guid operationId,
        int ordinal,
        FileIdentity deletedSourceIdentity,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            operationId,
            ordinal,
            FileDeleteOperationActionEntryState.MutationStarted,
            FileDeleteOperationActionEntryState.Committed,
            failure: null,
            expectedIdentity: deletedSourceIdentity,
            cancellationToken);

    public ValueTask<FileDeleteOperationActionHistory> MarkFailedAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        CancellationToken cancellationToken = default)
    {
        FileDeleteOperationActionHistory.ValidateFailure(failure);
        return TransitionAsync(
            operationId,
            ordinal,
            FileDeleteOperationActionEntryState.Pending,
            FileDeleteOperationActionEntryState.Failed,
            failure,
            expectedIdentity: null,
            cancellationToken);
    }

    public ValueTask<FileDeleteOperationActionHistory> MarkMutationRecoveryRequiredAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        CancellationToken cancellationToken = default)
    {
        FileDeleteOperationActionHistory.ValidateFailure(failure);
        return TransitionAsync(
            operationId,
            ordinal,
            FileDeleteOperationActionEntryState.MutationStarted,
            FileDeleteOperationActionEntryState.RecoveryRequired,
            failure,
            expectedIdentity: null,
            cancellationToken);
    }

    public async ValueTask<FileDeleteOperationActionHistory> CompleteAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ValidateOperationId(operationId);
        ThrowIfDisposed();

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var history = await LoadRequiredAsync(connection, transaction, operationId, cancellationToken).ConfigureAwait(false);
            if (history.TerminalState.HasValue)
            {
                throw new InvalidOperationException("Delete action-history operation is already terminal.");
            }

            var terminalState = InferTerminalState(history);
            var completedAt = _timeProvider.GetUtcNow();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE file_delete_actions
                SET terminal_state = @terminal_state,
                    completed_utc_ticks = @completed
                WHERE operation_id = @operation_id
                  AND terminal_state IS NULL;
                """;
            command.Parameters.AddWithValue("@terminal_state", (int)terminalState);
            command.Parameters.AddWithValue("@completed", ToUtcTicks(completedAt));
            command.Parameters.AddWithValue("@operation_id", FormatId(operationId));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("Delete action-history completion did not affect exactly one running operation.");
            }

            history = await LoadRequiredAsync(
                connection,
                transaction,
                operationId,
                cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return history;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<FileDeleteOperationActionHistory?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ValidateOperationId(operationId);
        ThrowIfDisposed();
        using var connection = OpenConnection();
        return await LoadAsync(connection, null, operationId, cancellationToken).ConfigureAwait(false);
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

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private async ValueTask<FileDeleteOperationActionHistory> TransitionAsync(
        Guid operationId,
        int ordinal,
        FileDeleteOperationActionEntryState expectedState,
        FileDeleteOperationActionEntryState nextState,
        FileOperationFailure? failure,
        FileIdentity? expectedIdentity,
        CancellationToken cancellationToken)
    {
        ValidateOperationId(operationId);
        ValidateOrdinal(ordinal);
        ThrowIfDisposed();
        if (failure is not null)
        {
            FileDeleteOperationActionHistory.ValidateFailure(failure);
        }

        var observedAt = _timeProvider.GetUtcNow();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE file_delete_action_entries
                SET state = @next_state,
                    mutation_started_utc_ticks = CASE
                        WHEN @next_state = @mutation_started_state THEN @observed
                        ELSE mutation_started_utc_ticks
                    END,
                    completed_utc_ticks = CASE
                        WHEN @next_state IN (@committed_state, @failed_state, @recovery_state) THEN @observed
                        ELSE completed_utc_ticks
                    END,
                    failure_code = @failure_code,
                    failure_message = @failure_message,
                    failure_path = @failure_path,
                    failure_retryable = @failure_retryable
                WHERE operation_id = @operation_id
                  AND ordinal = @ordinal
                  AND state = @expected_state
                  AND EXISTS (
                      SELECT 1
                      FROM file_delete_actions AS operation
                      WHERE operation.operation_id = @operation_id
                        AND operation.terminal_state IS NULL)
                  AND (@identity_required = 0 OR (
                      source_volume_serial = @source_volume
                      AND source_file_reference = @source_reference));
                """;
            command.Parameters.AddWithValue("@next_state", (int)nextState);
            command.Parameters.AddWithValue("@mutation_started_state", (int)FileDeleteOperationActionEntryState.MutationStarted);
            command.Parameters.AddWithValue("@committed_state", (int)FileDeleteOperationActionEntryState.Committed);
            command.Parameters.AddWithValue("@failed_state", (int)FileDeleteOperationActionEntryState.Failed);
            command.Parameters.AddWithValue("@recovery_state", (int)FileDeleteOperationActionEntryState.RecoveryRequired);
            command.Parameters.AddWithValue("@observed", ToUtcTicks(observedAt));
            command.Parameters.AddWithValue("@operation_id", FormatId(operationId));
            command.Parameters.AddWithValue("@ordinal", ordinal);
            command.Parameters.AddWithValue("@expected_state", (int)expectedState);
            command.Parameters.AddWithValue("@identity_required", expectedIdentity.HasValue ? 1 : 0);
            command.Parameters.AddWithValue("@source_volume", expectedIdentity.HasValue ? ToSqliteInteger(expectedIdentity.Value.VolumeSerialNumber) : 0L);
            command.Parameters.AddWithValue("@source_reference", expectedIdentity.HasValue ? ToSqliteInteger(expectedIdentity.Value.FileReferenceNumber) : 0L);
            SetFailureParameters(command, failure);

            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Delete action-history transition {expectedState} -> {nextState} did not match exactly one current entry with the required provenance.");
            }

            var history = await LoadRequiredAsync(
                connection,
                transaction,
                operationId,
                cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return history;
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
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            PRAGMA foreign_keys=ON;

            CREATE TABLE IF NOT EXISTS file_delete_action_history_schema_info(
                singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1),
                version INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS file_delete_actions(
                operation_id TEXT NOT NULL PRIMARY KEY,
                authorization_id TEXT NOT NULL,
                queued_utc_ticks INTEGER NOT NULL,
                validated_utc_ticks INTEGER NOT NULL,
                authorized_utc_ticks INTEGER NOT NULL,
                started_utc_ticks INTEGER NOT NULL,
                completed_utc_ticks INTEGER NULL,
                source_pane TEXT NOT NULL,
                source_tab_id TEXT NOT NULL,
                canonical_source_directory_path TEXT NOT NULL,
                source_directory_volume_serial INTEGER NOT NULL,
                source_directory_file_reference INTEGER NOT NULL,
                terminal_state INTEGER NULL CHECK(terminal_state IS NULL OR terminal_state BETWEEN 0 AND 2),
                CHECK((terminal_state IS NULL AND completed_utc_ticks IS NULL) OR
                      (terminal_state IS NOT NULL AND completed_utc_ticks IS NOT NULL))
            );

            CREATE TABLE IF NOT EXISTS file_delete_action_entries(
                operation_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
                source_path TEXT NOT NULL,
                source_name TEXT NOT NULL,
                canonical_source_path TEXT NOT NULL,
                source_volume_serial INTEGER NOT NULL,
                source_file_reference INTEGER NOT NULL,
                state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 4),
                mutation_started_utc_ticks INTEGER NULL,
                completed_utc_ticks INTEGER NULL,
                failure_code TEXT NULL,
                failure_message TEXT NULL,
                failure_path TEXT NULL,
                failure_retryable INTEGER NULL CHECK(failure_retryable IS NULL OR failure_retryable IN (0, 1)),
                PRIMARY KEY(operation_id, ordinal),
                FOREIGN KEY(operation_id) REFERENCES file_delete_actions(operation_id) ON DELETE CASCADE
            );
            """;
        command.ExecuteNonQuery();

        using var schemaRead = connection.CreateCommand();
        schemaRead.CommandText = "SELECT version FROM file_delete_action_history_schema_info WHERE singleton = 1;";
        var value = schemaRead.ExecuteScalar();
        if (value is null || value is DBNull)
        {
            using var schemaInsert = connection.CreateCommand();
            schemaInsert.CommandText = "INSERT INTO file_delete_action_history_schema_info(singleton, version) VALUES(1, @version);";
            schemaInsert.Parameters.AddWithValue("@version", SchemaVersion);
            schemaInsert.ExecuteNonQuery();
        }
        else if (Convert.ToInt32(value) != SchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported delete action-history schema version {value}; expected {SchemaVersion}.");
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void ValidateBegin(FileDeleteOperationUserAuthorizationReceipt authorization)
    {
        if (!authorization.UserAuthorizedAttempt || authorization.DeleteMutationAuthorized ||
            authorization.PlanId == Guid.Empty || authorization.AuthorizationId == Guid.Empty ||
            authorization.Items.Count == 0 || authorization.Items.Count != authorization.Plan.Intent.Entries.Count ||
            string.IsNullOrWhiteSpace(authorization.CanonicalSourceDirectoryPath))
        {
            throw new ArgumentException("Delete action history requires one complete non-mutating user-authorization receipt.", nameof(authorization));
        }

        for (var ordinal = 0; ordinal < authorization.Items.Count; ordinal++)
        {
            var item = authorization.Items[ordinal];
            if (item.Entry != authorization.Plan.Intent.Entries[ordinal] || item.Entry.IsDirectory ||
                string.IsNullOrWhiteSpace(item.CanonicalPath))
            {
                throw new ArgumentException("Delete action history requires exact ordered authorized file evidence.", nameof(authorization));
            }
        }
    }

    private static FileDeleteOperationActionTerminalState InferTerminalState(FileDeleteOperationActionHistory history)
    {
        if (history.Entries.Any(static entry =>
            entry.State is FileDeleteOperationActionEntryState.MutationStarted or
                FileDeleteOperationActionEntryState.RecoveryRequired))
        {
            return FileDeleteOperationActionTerminalState.RecoveryRequired;
        }

        if (history.Entries.Any(static entry => entry.State == FileDeleteOperationActionEntryState.Failed))
        {
            return FileDeleteOperationActionTerminalState.Failed;
        }

        if (history.Entries.All(static entry => entry.State == FileDeleteOperationActionEntryState.Committed))
        {
            return FileDeleteOperationActionTerminalState.Succeeded;
        }

        throw new InvalidOperationException(
            "Delete action history cannot complete while safe pending entries remain without a recorded failure.");
    }

    private static async ValueTask<FileDeleteOperationActionHistory> LoadRequiredAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid operationId,
        CancellationToken cancellationToken) =>
        await LoadAsync(connection, transaction, operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Delete action-history operation {operationId:D} does not exist.");

    private static async ValueTask<FileDeleteOperationActionHistory?> LoadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
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
                    new FileIdentity(FromSqliteInteger(reader.GetInt64(4)), FromSqliteInteger(reader.GetInt64(5))),
                    ReadEnum<FileDeleteOperationActionEntryState>(reader.GetInt64(6), "entry state"),
                    reader.IsDBNull(7) ? null : ReadUtcTicks(reader.GetInt64(7)),
                    reader.IsDBNull(8) ? null : ReadUtcTicks(reader.GetInt64(8)),
                    ReadFailure(reader, 9)));
            }
        }

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

    private static FileOperationFailure? ReadFailure(SqliteDataReader reader, int startOrdinal)
    {
        if (reader.IsDBNull(startOrdinal))
        {
            if (!reader.IsDBNull(startOrdinal + 1) || !reader.IsDBNull(startOrdinal + 2) || !reader.IsDBNull(startOrdinal + 3))
            {
                throw new InvalidDataException("Persisted delete action-history failure columns are inconsistent.");
            }
            return null;
        }
        if (reader.IsDBNull(startOrdinal + 1) || reader.IsDBNull(startOrdinal + 3))
        {
            throw new InvalidDataException("Persisted delete action-history failure is incomplete.");
        }

        var retryable = reader.GetInt64(startOrdinal + 3) switch
        {
            0 => false,
            1 => true,
            var invalid => throw new InvalidDataException($"Persisted delete action-history retryable value {invalid} is invalid."),
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
            throw new InvalidDataException("Persisted delete action-history failure is invalid.", exception);
        }
        return failure;
    }

    private static void SetFailureParameters(SqliteCommand command, FileOperationFailure? failure)
    {
        command.Parameters.AddWithValue("@failure_code", failure is null ? DBNull.Value : failure.Code);
        command.Parameters.AddWithValue("@failure_message", failure is null ? DBNull.Value : failure.Message);
        command.Parameters.AddWithValue("@failure_path", failure?.Path is { } path ? path : DBNull.Value);
        command.Parameters.AddWithValue("@failure_retryable", failure is null ? DBNull.Value : failure.Retryable ? 1 : 0);
    }

    private static TEnum ReadEnum<TEnum>(long value, string description)
        where TEnum : struct, Enum
    {
        if (value < int.MinValue || value > int.MaxValue || !Enum.IsDefined(typeof(TEnum), (int)value))
        {
            throw new InvalidDataException($"Unknown delete action-history {description} value {value}.");
        }
        return (TEnum)Enum.ToObject(typeof(TEnum), (int)value);
    }

    private static string FormatId(Guid value) => value.ToString("D");

    private static Guid ParseId(string value, string description) =>
        Guid.TryParseExact(value, "D", out var parsed)
            ? parsed
            : throw new InvalidDataException($"Persisted delete action-history {description} is invalid: {value}.");

    private static long ToUtcTicks(DateTimeOffset value) => value.ToUniversalTime().UtcDateTime.Ticks;

    private static DateTimeOffset ReadUtcTicks(long ticks)
    {
        try
        {
            return new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException("Persisted delete action-history UTC ticks are invalid.", exception);
        }
    }

    private static long ToSqliteInteger(ulong value) => unchecked((long)value);

    private static ulong FromSqliteInteger(long value) => unchecked((ulong)value);

    private static void ValidateOperationId(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("Delete action-history operation ID cannot be empty.", nameof(operationId));
        }
    }

    private static void ValidateOrdinal(int ordinal)
    {
        if (ordinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SqliteFileDeleteOperationActionHistoryStore));
        }
    }
}
