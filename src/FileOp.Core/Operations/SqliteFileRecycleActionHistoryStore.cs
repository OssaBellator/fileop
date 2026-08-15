using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using Microsoft.Data.Sqlite;

namespace FileOp.Core.Operations;

public sealed class SqliteFileRecycleActionHistoryStore : IFileRecycleActionHistoryStore, IDisposable
{
    private const int SchemaVersion = 1;
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _disposed;

    public SqliteFileRecycleActionHistoryStore(string databasePath, TimeProvider? timeProvider = null)
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

    public async ValueTask<FileRecycleActionHistory> BeginAsync(
        FileRecycleOperationUserAuthorizationReceipt authorization,
        FileRecycleFreshIdentityResult freshValidation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(freshValidation);
        ThrowIfDisposed();
        if (!freshValidation.CanBeginDurableHistory || !freshValidation.IsBoundTo(authorization))
        {
            throw new ArgumentException("Recycle history may begin only from ready freshness evidence bound to the exact authorization.", nameof(freshValidation));
        }

        var startedAt = _timeProvider.GetUtcNow();
        var key = FormatId(authorization.PlanId);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO file_recycle_operations(
                        operation_id, authorization_id, queued_utc_ticks, reviewed_utc_ticks,
                        authorized_utc_ticks, fresh_validated_utc_ticks, started_utc_ticks,
                        completed_utc_ticks, source_pane, source_tab_id,
                        canonical_source_directory_path, source_directory_volume_serial,
                        source_directory_file_reference, terminal_state)
                    VALUES(
                        @operation_id, @authorization_id, @queued, @reviewed,
                        @authorized, @fresh, @started, NULL, @source_pane, @source_tab,
                        @root_path, @root_volume, @root_reference, NULL);
                    """;
                command.Parameters.AddWithValue("@operation_id", key);
                command.Parameters.AddWithValue("@authorization_id", FormatId(authorization.AuthorizationId));
                command.Parameters.AddWithValue("@queued", ToUtcTicks(authorization.Plan.QueuedAtUtc));
                command.Parameters.AddWithValue("@reviewed", ToUtcTicks(authorization.Review.ReviewedAtUtc));
                command.Parameters.AddWithValue("@authorized", ToUtcTicks(authorization.AuthorizedAtUtc));
                command.Parameters.AddWithValue("@fresh", ToUtcTicks(freshValidation.ValidatedAtUtc));
                command.Parameters.AddWithValue("@started", ToUtcTicks(startedAt));
                command.Parameters.AddWithValue("@source_pane", authorization.Plan.Intent.SourcePane);
                command.Parameters.AddWithValue("@source_tab", FormatId(authorization.Plan.Intent.SourceTabId));
                command.Parameters.AddWithValue("@root_path", authorization.Review.CanonicalSourceDirectoryPath);
                command.Parameters.AddWithValue("@root_volume", ToSqliteInteger(authorization.Review.SourceDirectoryIdentity.VolumeSerialNumber));
                command.Parameters.AddWithValue("@root_reference", ToSqliteInteger(authorization.Review.SourceDirectoryIdentity.FileReferenceNumber));
                RequireSingleRow(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "Recycle operation insert");
            }

            for (var ordinal = 0; ordinal < authorization.Review.Items.Count; ordinal++)
            {
                var item = authorization.Review.Items[ordinal];
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO file_recycle_actions(
                        operation_id, ordinal, source_path, source_name, canonical_source_path,
                        source_volume_serial, source_file_reference, state,
                        mutation_started_utc_ticks, completed_utc_ticks,
                        provider_name, recycle_locator,
                        failure_code, failure_message, failure_path, failure_retryable)
                    VALUES(
                        @operation_id, @ordinal, @source_path, @source_name, @canonical_source_path,
                        @source_volume, @source_reference, @state,
                        NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL);
                    """;
                command.Parameters.AddWithValue("@operation_id", key);
                command.Parameters.AddWithValue("@ordinal", ordinal);
                command.Parameters.AddWithValue("@source_path", item.Entry.Path);
                command.Parameters.AddWithValue("@source_name", item.Entry.Name);
                command.Parameters.AddWithValue("@canonical_source_path", item.CanonicalPath);
                command.Parameters.AddWithValue("@source_volume", ToSqliteInteger(item.Identity.VolumeSerialNumber));
                command.Parameters.AddWithValue("@source_reference", ToSqliteInteger(item.Identity.FileReferenceNumber));
                command.Parameters.AddWithValue("@state", (int)FileRecycleActionEntryState.Pending);
                RequireSingleRow(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "Recycle action insert");
            }

            var history = await LoadRequiredAsync(connection, transaction, authorization.PlanId, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return history;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ValueTask<FileRecycleActionHistory> MarkMutationStartedAsync(
        Guid operationId,
        int ordinal,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            operationId,
            ordinal,
            FileRecycleActionEntryState.Pending,
            FileRecycleActionEntryState.MutationStarted,
            expectedIdentity: null,
            providerName: null,
            recycleLocator: null,
            failure: null,
            requireMutationFrontier: true,
            cancellationToken);

    public ValueTask<FileRecycleActionHistory> CommitRecycledAsync(
        Guid operationId,
        int ordinal,
        FileIdentity sourceIdentity,
        string providerName,
        string recycleLocator,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(recycleLocator);
        return TransitionAsync(
            operationId,
            ordinal,
            FileRecycleActionEntryState.MutationStarted,
            FileRecycleActionEntryState.Recycled,
            sourceIdentity,
            providerName,
            recycleLocator,
            failure: null,
            requireMutationFrontier: false,
            cancellationToken);
    }

    public ValueTask<FileRecycleActionHistory> MarkFailedAsync(
        Guid operationId,
        int ordinal,
        FileIdentity sourceIdentity,
        string providerName,
        FileOperationFailure failure,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ValidateFailure(failure);
        return TransitionAsync(
            operationId,
            ordinal,
            FileRecycleActionEntryState.MutationStarted,
            FileRecycleActionEntryState.Failed,
            sourceIdentity,
            providerName,
            recycleLocator: null,
            failure,
            requireMutationFrontier: false,
            cancellationToken);
    }

    public ValueTask<FileRecycleActionHistory> MarkRecoveryRequiredAsync(
        Guid operationId,
        int ordinal,
        FileIdentity sourceIdentity,
        string providerName,
        string? recycleLocator,
        FileOperationFailure failure,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        if (recycleLocator is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(recycleLocator);
        }
        ValidateFailure(failure);
        return TransitionAsync(
            operationId,
            ordinal,
            FileRecycleActionEntryState.MutationStarted,
            FileRecycleActionEntryState.RecoveryRequired,
            sourceIdentity,
            providerName,
            recycleLocator,
            failure,
            requireMutationFrontier: false,
            cancellationToken);
    }

    public async ValueTask<FileRecycleActionHistory> CompleteAsync(
        Guid operationId,
        FileRecycleActionTerminalState terminalState,
        CancellationToken cancellationToken = default)
    {
        ValidateOperationId(operationId);
        if (!Enum.IsDefined(terminalState))
        {
            throw new ArgumentOutOfRangeException(nameof(terminalState));
        }
        ThrowIfDisposed();

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var current = await LoadRequiredAsync(connection, transaction, operationId, cancellationToken).ConfigureAwait(false);
            if (current.TerminalState.HasValue)
            {
                throw new InvalidOperationException("Recycle operation is already terminal.");
            }

            var completedAt = _timeProvider.GetUtcNow();
            _ = BuildTerminal(current, terminalState, completedAt);

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE file_recycle_operations
                SET terminal_state = @terminal_state,
                    completed_utc_ticks = @completed
                WHERE operation_id = @operation_id
                  AND terminal_state IS NULL;
                """;
            command.Parameters.AddWithValue("@terminal_state", (int)terminalState);
            command.Parameters.AddWithValue("@completed", ToUtcTicks(completedAt));
            command.Parameters.AddWithValue("@operation_id", FormatId(operationId));
            RequireSingleRow(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false), "Recycle operation completion");

            var history = await LoadRequiredAsync(connection, transaction, operationId, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return history;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<FileRecycleActionHistory?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ValidateOperationId(operationId);
        ThrowIfDisposed();
        using var connection = OpenConnection();
        return await LoadAsync(connection, transaction: null, operationId, cancellationToken).ConfigureAwait(false);
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

    private async ValueTask<FileRecycleActionHistory> TransitionAsync(
        Guid operationId,
        int ordinal,
        FileRecycleActionEntryState expectedState,
        FileRecycleActionEntryState nextState,
        FileIdentity? expectedIdentity,
        string? providerName,
        string? recycleLocator,
        FileOperationFailure? failure,
        bool requireMutationFrontier,
        CancellationToken cancellationToken)
    {
        ValidateOperationId(operationId);
        if (ordinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }
        ThrowIfDisposed();
        if (failure is not null)
        {
            ValidateFailure(failure);
        }
        if (requireMutationFrontier &&
            (expectedState != FileRecycleActionEntryState.Pending || nextState != FileRecycleActionEntryState.MutationStarted))
        {
            throw new InvalidOperationException("The recycle mutation-frontier guard may be used only for Pending -> MutationStarted.");
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
                UPDATE file_recycle_actions
                SET state = @next_state,
                    mutation_started_utc_ticks = CASE WHEN @next_state = @mutation_started THEN @observed ELSE mutation_started_utc_ticks END,
                    completed_utc_ticks = CASE WHEN @next_state IN (@recycled, @failed, @recovery) THEN @observed ELSE completed_utc_ticks END,
                    provider_name = @provider_name,
                    recycle_locator = @recycle_locator,
                    failure_code = @failure_code,
                    failure_message = @failure_message,
                    failure_path = @failure_path,
                    failure_retryable = @failure_retryable
                WHERE operation_id = @operation_id
                  AND ordinal = @ordinal
                  AND state = @expected_state
                  AND EXISTS (
                      SELECT 1 FROM file_recycle_operations operation
                      WHERE operation.operation_id = @operation_id
                        AND operation.terminal_state IS NULL)
                  AND (@identity_required = 0 OR (
                      source_volume_serial = @source_volume
                      AND source_file_reference = @source_reference))
                  AND (@require_frontier = 0 OR (
                      NOT EXISTS (
                          SELECT 1 FROM file_recycle_actions prior
                          WHERE prior.operation_id = @operation_id
                            AND prior.ordinal < @ordinal
                            AND prior.state <> @recycled)
                      AND NOT EXISTS (
                          SELECT 1 FROM file_recycle_actions later
                          WHERE later.operation_id = @operation_id
                            AND later.ordinal > @ordinal
                            AND later.state <> @pending)));
                """;
            command.Parameters.AddWithValue("@next_state", (int)nextState);
            command.Parameters.AddWithValue("@mutation_started", (int)FileRecycleActionEntryState.MutationStarted);
            command.Parameters.AddWithValue("@pending", (int)FileRecycleActionEntryState.Pending);
            command.Parameters.AddWithValue("@recycled", (int)FileRecycleActionEntryState.Recycled);
            command.Parameters.AddWithValue("@failed", (int)FileRecycleActionEntryState.Failed);
            command.Parameters.AddWithValue("@recovery", (int)FileRecycleActionEntryState.RecoveryRequired);
            command.Parameters.AddWithValue("@observed", ToUtcTicks(observedAt));
            command.Parameters.AddWithValue("@operation_id", FormatId(operationId));
            command.Parameters.AddWithValue("@ordinal", ordinal);
            command.Parameters.AddWithValue("@expected_state", (int)expectedState);
            command.Parameters.AddWithValue("@identity_required", expectedIdentity.HasValue ? 1 : 0);
            command.Parameters.AddWithValue("@source_volume", expectedIdentity.HasValue ? ToSqliteInteger(expectedIdentity.Value.VolumeSerialNumber) : 0L);
            command.Parameters.AddWithValue("@source_reference", expectedIdentity.HasValue ? ToSqliteInteger(expectedIdentity.Value.FileReferenceNumber) : 0L);
            command.Parameters.AddWithValue("@provider_name", (object?)providerName ?? DBNull.Value);
            command.Parameters.AddWithValue("@recycle_locator", (object?)recycleLocator ?? DBNull.Value);
            command.Parameters.AddWithValue("@require_frontier", requireMutationFrontier ? 1 : 0);
            SetFailureParameters(command, failure);
            RequireSingleRow(
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false),
                $"Recycle transition {expectedState} -> {nextState}");

            var history = await LoadRequiredAsync(connection, transaction, operationId, cancellationToken).ConfigureAwait(false);
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

            CREATE TABLE IF NOT EXISTS file_recycle_schema_info(
                singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1),
                version INTEGER NOT NULL
            );
            INSERT OR IGNORE INTO file_recycle_schema_info(singleton, version) VALUES(1, 1);

            CREATE TABLE IF NOT EXISTS file_recycle_operations(
                operation_id TEXT NOT NULL PRIMARY KEY,
                authorization_id TEXT NOT NULL,
                queued_utc_ticks INTEGER NOT NULL,
                reviewed_utc_ticks INTEGER NOT NULL,
                authorized_utc_ticks INTEGER NOT NULL,
                fresh_validated_utc_ticks INTEGER NOT NULL,
                started_utc_ticks INTEGER NOT NULL,
                completed_utc_ticks INTEGER NULL,
                source_pane TEXT NOT NULL,
                source_tab_id TEXT NOT NULL,
                canonical_source_directory_path TEXT NOT NULL,
                source_directory_volume_serial INTEGER NOT NULL,
                source_directory_file_reference INTEGER NOT NULL,
                terminal_state INTEGER NULL
            );

            CREATE TABLE IF NOT EXISTS file_recycle_actions(
                operation_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                source_path TEXT NOT NULL,
                source_name TEXT NOT NULL,
                canonical_source_path TEXT NOT NULL,
                source_volume_serial INTEGER NOT NULL,
                source_file_reference INTEGER NOT NULL,
                state INTEGER NOT NULL,
                mutation_started_utc_ticks INTEGER NULL,
                completed_utc_ticks INTEGER NULL,
                provider_name TEXT NULL,
                recycle_locator TEXT NULL,
                failure_code TEXT NULL,
                failure_message TEXT NULL,
                failure_path TEXT NULL,
                failure_retryable INTEGER NULL,
                PRIMARY KEY(operation_id, ordinal),
                FOREIGN KEY(operation_id) REFERENCES file_recycle_operations(operation_id) ON DELETE CASCADE
            );
            """;
        command.ExecuteNonQuery();

        using var version = connection.CreateCommand();
        version.CommandText = "SELECT version FROM file_recycle_schema_info WHERE singleton = 1;";
        var observed = Convert.ToInt32(version.ExecuteScalar());
        if (observed != SchemaVersion)
        {
            throw new InvalidOperationException($"Unsupported recycle history schema version {observed}.");
        }
    }

    private async ValueTask<FileRecycleActionHistory> LoadRequiredAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid operationId,
        CancellationToken cancellationToken) =>
        await LoadAsync(connection, transaction, operationId, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("Recycle operation history was not found.");

    private static async ValueTask<FileRecycleActionHistory?> LoadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        using var operation = connection.CreateCommand();
        operation.Transaction = transaction;
        operation.CommandText = """
            SELECT authorization_id, queued_utc_ticks, reviewed_utc_ticks, authorized_utc_ticks,
                   fresh_validated_utc_ticks, started_utc_ticks, completed_utc_ticks,
                   source_pane, source_tab_id, canonical_source_directory_path,
                   source_directory_volume_serial, source_directory_file_reference, terminal_state
            FROM file_recycle_operations
            WHERE operation_id = @operation_id;
            """;
        operation.Parameters.AddWithValue("@operation_id", FormatId(operationId));

        Guid authorizationId;
        DateTimeOffset queued;
        DateTimeOffset reviewed;
        DateTimeOffset authorized;
        DateTimeOffset fresh;
        DateTimeOffset started;
        DateTimeOffset? completed;
        string sourcePane;
        Guid sourceTab;
        string rootPath;
        FileIdentity rootIdentity;
        FileRecycleActionTerminalState? terminal;

        using (var reader = await operation.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            authorizationId = ParseId(reader.GetString(0));
            queued = FromUtcTicks(reader.GetInt64(1));
            reviewed = FromUtcTicks(reader.GetInt64(2));
            authorized = FromUtcTicks(reader.GetInt64(3));
            fresh = FromUtcTicks(reader.GetInt64(4));
            started = FromUtcTicks(reader.GetInt64(5));
            completed = reader.IsDBNull(6) ? null : FromUtcTicks(reader.GetInt64(6));
            sourcePane = reader.GetString(7);
            sourceTab = ParseId(reader.GetString(8));
            rootPath = reader.GetString(9);
            rootIdentity = new FileIdentity(FromSqliteInteger(reader.GetInt64(10)), FromSqliteInteger(reader.GetInt64(11)));
            terminal = reader.IsDBNull(12) ? null : (FileRecycleActionTerminalState)reader.GetInt32(12);
        }

        var entries = new List<FileRecycleActionEntry>();
        using var actions = connection.CreateCommand();
        actions.Transaction = transaction;
        actions.CommandText = """
            SELECT ordinal, source_path, source_name, canonical_source_path,
                   source_volume_serial, source_file_reference, state,
                   mutation_started_utc_ticks, completed_utc_ticks,
                   provider_name, recycle_locator,
                   failure_code, failure_message, failure_path, failure_retryable
            FROM file_recycle_actions
            WHERE operation_id = @operation_id
            ORDER BY ordinal;
            """;
        actions.Parameters.AddWithValue("@operation_id", FormatId(operationId));
        using (var reader = await actions.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                FileOperationFailure? failure = null;
                if (!reader.IsDBNull(11))
                {
                    failure = new FileOperationFailure(
                        reader.GetString(11),
                        reader.GetString(12),
                        reader.IsDBNull(13) ? null : reader.GetString(13),
                        reader.GetInt32(14) != 0);
                }

                entries.Add(new FileRecycleActionEntry(
                    reader.GetInt32(0),
                    new FileOperationEntry(reader.GetString(1), reader.GetString(2), IsDirectory: false),
                    reader.GetString(3),
                    new FileIdentity(FromSqliteInteger(reader.GetInt64(4)), FromSqliteInteger(reader.GetInt64(5))),
                    (FileRecycleActionEntryState)reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : FromUtcTicks(reader.GetInt64(7)),
                    reader.IsDBNull(8) ? null : FromUtcTicks(reader.GetInt64(8)),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    failure));
            }
        }

        return new FileRecycleActionHistory(
            operationId,
            authorizationId,
            queued,
            reviewed,
            authorized,
            fresh,
            started,
            completed,
            sourcePane,
            sourceTab,
            rootPath,
            rootIdentity,
            terminal,
            entries);
    }

    private static FileRecycleActionHistory BuildTerminal(
        FileRecycleActionHistory current,
        FileRecycleActionTerminalState terminalState,
        DateTimeOffset completedAtUtc) =>
        new(
            current.OperationId,
            current.AuthorizationId,
            current.QueuedAtUtc,
            current.ReviewedAtUtc,
            current.AuthorizedAtUtc,
            current.FreshValidatedAtUtc,
            current.StartedAtUtc,
            completedAtUtc,
            current.SourcePaneId,
            current.SourceTabId,
            current.CanonicalSourceDirectoryPath,
            current.SourceDirectoryIdentity,
            terminalState,
            current.Entries);

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void SetFailureParameters(SqliteCommand command, FileOperationFailure? failure)
    {
        command.Parameters.AddWithValue("@failure_code", (object?)failure?.Code ?? DBNull.Value);
        command.Parameters.AddWithValue("@failure_message", (object?)failure?.Message ?? DBNull.Value);
        command.Parameters.AddWithValue("@failure_path", (object?)failure?.Path ?? DBNull.Value);
        command.Parameters.AddWithValue("@failure_retryable", failure is null ? DBNull.Value : failure.Retryable ? 1 : 0);
    }

    private static void ValidateFailure(FileOperationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentException.ThrowIfNullOrWhiteSpace(failure.Code);
        ArgumentException.ThrowIfNullOrWhiteSpace(failure.Message);
    }

    private static void ValidateOperationId(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("Recycle operation ID cannot be empty.", nameof(operationId));
        }
    }

    private static void RequireSingleRow(int affected, string operation)
    {
        if (affected != 1)
        {
            throw new InvalidOperationException($"{operation} did not affect exactly one row.");
        }
    }

    private static string FormatId(Guid value) => value.ToString("N");
    private static Guid ParseId(string value) => Guid.ParseExact(value, "N");
    private static long ToUtcTicks(DateTimeOffset value) => value.ToUniversalTime().UtcDateTime.Ticks;
    private static DateTimeOffset FromUtcTicks(long ticks) => new(ticks, TimeSpan.Zero);
    private static long ToSqliteInteger(ulong value) => unchecked((long)value);
    private static ulong FromSqliteInteger(long value) => unchecked((ulong)value);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
