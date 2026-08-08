using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using Microsoft.Data.Sqlite;

namespace FileOp.Core.Operations;

public sealed class SqliteFileOperationActionHistoryStore : IFileOperationActionHistoryStore, IDisposable
{
    private const int SchemaVersion = 1;
    private const int MaximumRecentLimit = 4_096;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _disposed;

    public SqliteFileOperationActionHistoryStore(string databasePath)
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

    public async ValueTask<FileOperationActionHistory> BeginAsync(
        FileOperationExecutionValidationResult validation,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validation);
        ThrowIfDisposed();
        ValidateBegin(validation);

        var plan = validation.Plan;
        var queuedAt = NormalizeUtc(plan.QueuedAtUtc);
        var validatedAt = NormalizeUtc(validation.ValidatedAtUtc);
        var startedAt = NormalizeUtc(startedAtUtc);
        var operationId = FormatOperationId(plan.Id);

        var entries = new FileOperationActionEntry[validation.Items.Count];
        for (var ordinal = 0; ordinal < validation.Items.Count; ordinal++)
        {
            var item = validation.Items[ordinal];
            var state = item.Decision switch
            {
                FileOperationExecutionValidationDecision.Ready => FileOperationActionEntryState.Pending,
                FileOperationExecutionValidationDecision.Skip => FileOperationActionEntryState.Skipped,
                _ => throw new InvalidOperationException(
                    "Only ready/skip execution validation results can begin durable action history."),
            };
            entries[ordinal] = new FileOperationActionEntry(
                ordinal,
                item.Entry,
                item.Source.CanonicalPath,
                item.Destination.CanonicalPath,
                state,
                MutationStartedAtUtc: null,
                CompletedAtUtc: state == FileOperationActionEntryState.Skipped ? startedAt : null,
                item.Source.Identity,
                item.Destination.Identity,
                FileOperationUndoKind.None,
                Failure: null);
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            using (var operation = connection.CreateCommand())
            {
                operation.Transaction = transaction;
                operation.CommandText = """
                    INSERT INTO file_operation_actions(
                        operation_id,
                        queued_utc_ticks,
                        validated_utc_ticks,
                        started_utc_ticks,
                        completed_utc_ticks,
                        kind,
                        collision_policy,
                        source_directory_path,
                        destination_directory_path,
                        canonical_source_directory_path,
                        canonical_destination_directory_path,
                        terminal_state)
                    VALUES(
                        @operation_id,
                        @queued_utc_ticks,
                        @validated_utc_ticks,
                        @started_utc_ticks,
                        NULL,
                        @kind,
                        @collision_policy,
                        @source_directory_path,
                        @destination_directory_path,
                        @canonical_source_directory_path,
                        @canonical_destination_directory_path,
                        NULL);
                    """;
                operation.Parameters.AddWithValue("@operation_id", operationId);
                operation.Parameters.AddWithValue("@queued_utc_ticks", ToUtcTicks(queuedAt));
                operation.Parameters.AddWithValue("@validated_utc_ticks", ToUtcTicks(validatedAt));
                operation.Parameters.AddWithValue("@started_utc_ticks", ToUtcTicks(startedAt));
                operation.Parameters.AddWithValue("@kind", (int)plan.Kind);
                operation.Parameters.AddWithValue("@collision_policy", (int)plan.CollisionPolicy);
                operation.Parameters.AddWithValue("@source_directory_path", plan.Intent.SourceDirectoryPath);
                operation.Parameters.AddWithValue("@destination_directory_path", plan.Intent.DestinationDirectoryPath);
                operation.Parameters.AddWithValue(
                    "@canonical_source_directory_path",
                    validation.SourceDirectory.CanonicalPath);
                operation.Parameters.AddWithValue(
                    "@canonical_destination_directory_path",
                    validation.DestinationDirectory.CanonicalPath);
                await operation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using (var entryCommand = connection.CreateCommand())
            {
                entryCommand.Transaction = transaction;
                entryCommand.CommandText = """
                    INSERT INTO file_operation_action_entries(
                        operation_id,
                        ordinal,
                        source_path,
                        source_name,
                        is_directory,
                        canonical_source_path,
                        canonical_destination_path,
                        state,
                        mutation_started_utc_ticks,
                        completed_utc_ticks,
                        source_volume_serial,
                        source_file_reference,
                        destination_volume_serial,
                        destination_file_reference,
                        undo_kind,
                        failure_code,
                        failure_message,
                        failure_path,
                        failure_retryable)
                    VALUES(
                        @operation_id,
                        @ordinal,
                        @source_path,
                        @source_name,
                        @is_directory,
                        @canonical_source_path,
                        @canonical_destination_path,
                        @state,
                        NULL,
                        @completed_utc_ticks,
                        @source_volume_serial,
                        @source_file_reference,
                        @destination_volume_serial,
                        @destination_file_reference,
                        @undo_kind,
                        NULL,
                        NULL,
                        NULL,
                        NULL);
                    """;
                entryCommand.Parameters.Add("@operation_id", SqliteType.Text);
                entryCommand.Parameters.Add("@ordinal", SqliteType.Integer);
                entryCommand.Parameters.Add("@source_path", SqliteType.Text);
                entryCommand.Parameters.Add("@source_name", SqliteType.Text);
                entryCommand.Parameters.Add("@is_directory", SqliteType.Integer);
                entryCommand.Parameters.Add("@canonical_source_path", SqliteType.Text);
                entryCommand.Parameters.Add("@canonical_destination_path", SqliteType.Text);
                entryCommand.Parameters.Add("@state", SqliteType.Integer);
                entryCommand.Parameters.Add("@completed_utc_ticks", SqliteType.Integer);
                entryCommand.Parameters.Add("@source_volume_serial", SqliteType.Integer);
                entryCommand.Parameters.Add("@source_file_reference", SqliteType.Integer);
                entryCommand.Parameters.Add("@destination_volume_serial", SqliteType.Integer);
                entryCommand.Parameters.Add("@destination_file_reference", SqliteType.Integer);
                entryCommand.Parameters.Add("@undo_kind", SqliteType.Integer);

                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    entryCommand.Parameters["@operation_id"].Value = operationId;
                    entryCommand.Parameters["@ordinal"].Value = entry.Ordinal;
                    entryCommand.Parameters["@source_path"].Value = entry.Entry.Path;
                    entryCommand.Parameters["@source_name"].Value = entry.Entry.Name;
                    entryCommand.Parameters["@is_directory"].Value = entry.Entry.IsDirectory ? 1 : 0;
                    entryCommand.Parameters["@canonical_source_path"].Value = entry.CanonicalSourcePath;
                    entryCommand.Parameters["@canonical_destination_path"].Value = entry.CanonicalDestinationPath;
                    entryCommand.Parameters["@state"].Value = (int)entry.State;
                    entryCommand.Parameters["@completed_utc_ticks"].Value =
                        entry.CompletedAtUtc is { } completedAt ? ToUtcTicks(completedAt) : DBNull.Value;
                    SetIdentityParameters(
                        entryCommand,
                        "@source_volume_serial",
                        "@source_file_reference",
                        entry.SourceIdentity);
                    SetIdentityParameters(
                        entryCommand,
                        "@destination_volume_serial",
                        "@destination_file_reference",
                        entry.DestinationIdentity);
                    entryCommand.Parameters["@undo_kind"].Value = (int)entry.UndoKind;
                    await entryCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            transaction.Commit();
            return new FileOperationActionHistory(
                plan.Id,
                queuedAt,
                validatedAt,
                startedAt,
                CompletedAtUtc: null,
                plan.Kind,
                plan.CollisionPolicy,
                plan.Intent.SourceDirectoryPath,
                plan.Intent.DestinationDirectoryPath,
                validation.SourceDirectory.CanonicalPath,
                validation.DestinationDirectory.CanonicalPath,
                TerminalState: null,
                Array.AsReadOnly(entries));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException(
                $"Action history for operation {plan.Id} already exists or violates the history schema.",
                exception);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ValueTask<FileOperationActionHistory> MarkMutationStartedAsync(
        Guid operationId,
        int ordinal,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default) =>
        TransitionEntryAsync(
            operationId,
            ordinal,
            FileOperationActionEntryState.Pending,
            FileOperationActionEntryState.MutationStarted,
            NormalizeUtc(startedAtUtc),
            failure: null,
            destinationIdentity: null,
            undoKind: FileOperationUndoKind.None,
            cancellationToken);

    public ValueTask<FileOperationActionHistory> MarkEntryFailedBeforeMutationAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return TransitionEntryAsync(
            operationId,
            ordinal,
            FileOperationActionEntryState.Pending,
            FileOperationActionEntryState.Failed,
            NormalizeUtc(failedAtUtc),
            failure,
            destinationIdentity: null,
            undoKind: FileOperationUndoKind.None,
            cancellationToken);
    }

    public async ValueTask<FileOperationActionHistory> CommitCopyAsync(
        Guid operationId,
        int ordinal,
        FileIdentity destinationIdentity,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateOrdinal(ordinal);
        var committedAt = NormalizeUtc(committedAtUtc);
        var operationKey = FormatOperationId(operationId);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE file_operation_action_entries
                SET state = @committed,
                    completed_utc_ticks = @completed_utc_ticks,
                    destination_volume_serial = @destination_volume_serial,
                    destination_file_reference = @destination_file_reference,
                    undo_kind = @undo_kind,
                    failure_code = NULL,
                    failure_message = NULL,
                    failure_path = NULL,
                    failure_retryable = NULL
                WHERE operation_id = @operation_id
                  AND ordinal = @ordinal
                  AND state = @mutation_started
                  AND EXISTS(
                      SELECT 1
                      FROM file_operation_actions AS action
                      WHERE action.operation_id = @operation_id
                        AND action.terminal_state IS NULL
                        AND action.kind = @copy_kind
                  );
                """;
            command.Parameters.AddWithValue("@committed", (int)FileOperationActionEntryState.Committed);
            command.Parameters.AddWithValue("@completed_utc_ticks", ToUtcTicks(committedAt));
            command.Parameters.AddWithValue(
                "@destination_volume_serial",
                ToSqliteInteger(destinationIdentity.VolumeSerialNumber));
            command.Parameters.AddWithValue(
                "@destination_file_reference",
                ToSqliteInteger(destinationIdentity.FileReferenceNumber));
            command.Parameters.AddWithValue("@undo_kind", (int)FileOperationUndoKind.DeleteCreatedDestination);
            command.Parameters.AddWithValue("@operation_id", operationKey);
            command.Parameters.AddWithValue("@ordinal", ordinal);
            command.Parameters.AddWithValue("@mutation_started", (int)FileOperationActionEntryState.MutationStarted);
            command.Parameters.AddWithValue("@copy_kind", (int)FileOperationKind.Copy);

            var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (changed != 1)
            {
                throw new InvalidOperationException(
                    "A Copy entry can be committed only once, after MutationStarted, while its operation remains active.");
            }

            transaction.Commit();
            return await LoadRequiredAsync(connection, operationId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ValueTask<FileOperationActionHistory> MarkMutationRecoveryRequiredAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return TransitionEntryAsync(
            operationId,
            ordinal,
            FileOperationActionEntryState.MutationStarted,
            FileOperationActionEntryState.RecoveryRequired,
            NormalizeUtc(failedAtUtc),
            failure,
            destinationIdentity: null,
            undoKind: FileOperationUndoKind.None,
            cancellationToken);
    }

    public async ValueTask<FileOperationActionHistory> CompleteAsync(
        Guid operationId,
        FileOperationActionTerminalState terminalState,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(typeof(FileOperationActionTerminalState), terminalState))
        {
            throw new ArgumentOutOfRangeException(nameof(terminalState));
        }

        var completedAt = NormalizeUtc(completedAtUtc);
        var operationKey = FormatOperationId(operationId);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var current = await LoadRequiredAsync(
                connection,
                operationId,
                cancellationToken,
                transaction).ConfigureAwait(false);
            ValidateCompletion(current, terminalState);

            if (terminalState == FileOperationActionTerminalState.RecoveryRequired)
            {
                using var settle = connection.CreateCommand();
                settle.Transaction = transaction;
                settle.CommandText = """
                    UPDATE file_operation_action_entries
                    SET state = @recovery_required,
                        completed_utc_ticks = COALESCE(completed_utc_ticks, @completed_utc_ticks)
                    WHERE operation_id = @operation_id
                      AND state = @mutation_started;
                    """;
                settle.Parameters.AddWithValue(
                    "@recovery_required",
                    (int)FileOperationActionEntryState.RecoveryRequired);
                settle.Parameters.AddWithValue("@completed_utc_ticks", ToUtcTicks(completedAt));
                settle.Parameters.AddWithValue("@operation_id", operationKey);
                settle.Parameters.AddWithValue(
                    "@mutation_started",
                    (int)FileOperationActionEntryState.MutationStarted);
                await settle.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE file_operation_actions
                    SET terminal_state = @terminal_state,
                        completed_utc_ticks = @completed_utc_ticks
                    WHERE operation_id = @operation_id
                      AND terminal_state IS NULL;
                    """;
                command.Parameters.AddWithValue("@terminal_state", (int)terminalState);
                command.Parameters.AddWithValue("@completed_utc_ticks", ToUtcTicks(completedAt));
                command.Parameters.AddWithValue("@operation_id", operationKey);
                var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (changed != 1)
                {
                    throw new InvalidOperationException("The action-history operation is already terminal.");
                }
            }

            transaction.Commit();
            return await LoadRequiredAsync(connection, operationId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<FileOperationActionHistory?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var connection = OpenConnection();
        return await LoadAsync(connection, operationId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<FileOperationActionHistory>> GetRecentAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (limit <= 0 || limit > MaximumRecentLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                limit,
                $"Action-history limits must be between 1 and {MaximumRecentLimit:N0}.");
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT operation_id
            FROM file_operation_actions
            ORDER BY started_utc_ticks DESC, operation_id DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@limit", limit);

        var operationIds = new List<Guid>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                operationIds.Add(ParseOperationId(reader.GetString(0)));
            }
        }

        var histories = new List<FileOperationActionHistory>(operationIds.Count);
        foreach (var operationId in operationIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            histories.Add(await LoadRequiredAsync(connection, operationId, cancellationToken).ConfigureAwait(false));
        }

        return histories.AsReadOnly();
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

    private async ValueTask<FileOperationActionHistory> TransitionEntryAsync(
        Guid operationId,
        int ordinal,
        FileOperationActionEntryState expectedState,
        FileOperationActionEntryState newState,
        DateTimeOffset timestampUtc,
        FileOperationFailure? failure,
        FileIdentity? destinationIdentity,
        FileOperationUndoKind undoKind,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateOrdinal(ordinal);
        var operationKey = FormatOperationId(operationId);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE file_operation_action_entries
                SET state = @new_state,
                    mutation_started_utc_ticks = CASE
                        WHEN @new_state = @mutation_started THEN @timestamp_utc_ticks
                        ELSE mutation_started_utc_ticks
                    END,
                    completed_utc_ticks = CASE
                        WHEN @new_state IN (@failed, @recovery_required) THEN @timestamp_utc_ticks
                        ELSE completed_utc_ticks
                    END,
                    destination_volume_serial = @destination_volume_serial,
                    destination_file_reference = @destination_file_reference,
                    undo_kind = @undo_kind,
                    failure_code = @failure_code,
                    failure_message = @failure_message,
                    failure_path = @failure_path,
                    failure_retryable = @failure_retryable
                WHERE operation_id = @operation_id
                  AND ordinal = @ordinal
                  AND state = @expected_state
                  AND EXISTS(
                      SELECT 1
                      FROM file_operation_actions AS action
                      WHERE action.operation_id = @operation_id
                        AND action.terminal_state IS NULL
                  );
                """;
            command.Parameters.AddWithValue("@new_state", (int)newState);
            command.Parameters.AddWithValue("@mutation_started", (int)FileOperationActionEntryState.MutationStarted);
            command.Parameters.AddWithValue("@failed", (int)FileOperationActionEntryState.Failed);
            command.Parameters.AddWithValue("@recovery_required", (int)FileOperationActionEntryState.RecoveryRequired);
            command.Parameters.AddWithValue("@timestamp_utc_ticks", ToUtcTicks(timestampUtc));
            SetIdentityParameters(
                command,
                "@destination_volume_serial",
                "@destination_file_reference",
                destinationIdentity);
            command.Parameters.AddWithValue("@undo_kind", (int)undoKind);
            SetFailureParameters(command, failure);
            command.Parameters.AddWithValue("@operation_id", operationKey);
            command.Parameters.AddWithValue("@ordinal", ordinal);
            command.Parameters.AddWithValue("@expected_state", (int)expectedState);

            var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (changed != 1)
            {
                throw new InvalidOperationException(
                    $"Entry {ordinal} cannot transition from {expectedState} to {newState} in the current action-history state.");
            }

            transaction.Commit();
            return await LoadRequiredAsync(connection, operationId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using (var versionTable = connection.CreateCommand())
        {
            versionTable.CommandText = $$"""
                PRAGMA journal_mode = WAL;

                CREATE TABLE IF NOT EXISTS file_operation_action_schema_info(
                    id INTEGER PRIMARY KEY CHECK(id = 1),
                    version INTEGER NOT NULL
                );
                INSERT OR IGNORE INTO file_operation_action_schema_info(id, version)
                VALUES(1, {{SchemaVersion}});
                """;
            versionTable.ExecuteNonQuery();
        }

        using (var version = connection.CreateCommand())
        {
            version.CommandText =
                "SELECT version FROM file_operation_action_schema_info WHERE id = 1;";
            var actual = Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (actual != SchemaVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported FileOp action-history schema version {actual}; expected {SchemaVersion}.");
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS file_operation_actions(
                operation_id TEXT PRIMARY KEY,
                queued_utc_ticks INTEGER NOT NULL,
                validated_utc_ticks INTEGER NOT NULL,
                started_utc_ticks INTEGER NOT NULL,
                completed_utc_ticks INTEGER NULL,
                kind INTEGER NOT NULL,
                collision_policy INTEGER NOT NULL,
                source_directory_path TEXT NOT NULL,
                destination_directory_path TEXT NOT NULL,
                canonical_source_directory_path TEXT NOT NULL,
                canonical_destination_directory_path TEXT NOT NULL,
                terminal_state INTEGER NULL
            ) WITHOUT ROWID;

            CREATE INDEX IF NOT EXISTS ix_file_operation_actions_started
                ON file_operation_actions(started_utc_ticks DESC, operation_id DESC);

            CREATE TABLE IF NOT EXISTS file_operation_action_entries(
                operation_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                source_path TEXT NOT NULL,
                source_name TEXT NOT NULL,
                is_directory INTEGER NOT NULL,
                canonical_source_path TEXT NOT NULL,
                canonical_destination_path TEXT NOT NULL,
                state INTEGER NOT NULL,
                mutation_started_utc_ticks INTEGER NULL,
                completed_utc_ticks INTEGER NULL,
                source_volume_serial INTEGER NULL,
                source_file_reference INTEGER NULL,
                destination_volume_serial INTEGER NULL,
                destination_file_reference INTEGER NULL,
                undo_kind INTEGER NOT NULL,
                failure_code TEXT NULL,
                failure_message TEXT NULL,
                failure_path TEXT NULL,
                failure_retryable INTEGER NULL,
                PRIMARY KEY(operation_id, ordinal),
                FOREIGN KEY(operation_id) REFERENCES file_operation_actions(operation_id) ON DELETE CASCADE
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
            PRAGMA synchronous = FULL;
            PRAGMA foreign_keys = ON;
            """;
        command.ExecuteNonQuery();
        return connection;
    }

    private static void ValidateBegin(FileOperationExecutionValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(validation.Plan);
        ArgumentNullException.ThrowIfNull(validation.Plan.Intent);
        ArgumentNullException.ThrowIfNull(validation.Plan.Intent.Entries);
        if (!validation.CanBeginMutation)
        {
            throw new InvalidOperationException(
                "Durable action history can begin only after execution validation is ready.");
        }

        if (validation.Plan.Intent.Entries.Count == 0 ||
            validation.Items.Count != validation.Plan.Intent.Entries.Count)
        {
            throw new ArgumentException(
                "Execution validation must contain exactly one item for every planned entry.",
                nameof(validation));
        }

        for (var ordinal = 0; ordinal < validation.Items.Count; ordinal++)
        {
            var planned = validation.Plan.Intent.Entries[ordinal];
            var item = validation.Items[ordinal];
            if (item.Entry != planned)
            {
                throw new ArgumentException(
                    "Execution validation entry order must match the immutable operation plan.",
                    nameof(validation));
            }

            if (string.IsNullOrWhiteSpace(item.Source.CanonicalPath) ||
                string.IsNullOrWhiteSpace(item.Destination.CanonicalPath))
            {
                throw new ArgumentException(
                    "Execution validation must provide canonical source and destination paths.",
                    nameof(validation));
            }

            if (item.Decision == FileOperationExecutionValidationDecision.Ready)
            {
                if (item.Destination.State != FileOperationCanonicalPathState.Missing)
                {
                    throw new ArgumentException(
                        "A ready mutation entry must have a missing canonical destination leaf.",
                        nameof(validation));
                }
            }
            else if (item.Decision != FileOperationExecutionValidationDecision.Skip)
            {
                throw new ArgumentException(
                    "Action history cannot begin with blocked or unresolved validation items.",
                    nameof(validation));
            }
        }
    }

    private static void ValidateCompletion(
        FileOperationActionHistory history,
        FileOperationActionTerminalState terminalState)
    {
        if (history.TerminalState.HasValue)
        {
            throw new InvalidOperationException("The action-history operation is already terminal.");
        }

        var hasAmbiguous = history.Entries.Any(static entry =>
            entry.State is FileOperationActionEntryState.MutationStarted or
                FileOperationActionEntryState.RecoveryRequired);

        if (terminalState == FileOperationActionTerminalState.RecoveryRequired)
        {
            if (!hasAmbiguous)
            {
                throw new InvalidOperationException(
                    "RecoveryRequired is valid only when at least one entry has an unresolved mutation boundary.");
            }

            return;
        }

        if (hasAmbiguous)
        {
            throw new InvalidOperationException(
                "An operation with an unresolved mutation boundary must end as RecoveryRequired.");
        }

        if (terminalState == FileOperationActionTerminalState.Succeeded &&
            history.Entries.Any(static entry =>
                entry.State is not FileOperationActionEntryState.Committed and
                    not FileOperationActionEntryState.Skipped))
        {
            throw new InvalidOperationException(
                "An operation cannot succeed until every entry is committed or skipped.");
        }
    }

    private static void ValidateOrdinal(int ordinal)
    {
        if (ordinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }
    }

    private static void SetFailureParameters(SqliteCommand command, FileOperationFailure? failure)
    {
        command.Parameters.AddWithValue(
            "@failure_code",
            failure is null ? DBNull.Value : failure.Code);
        command.Parameters.AddWithValue(
            "@failure_message",
            failure is null ? DBNull.Value : failure.Message);
        command.Parameters.AddWithValue(
            "@failure_path",
            failure?.Path is { } path ? path : DBNull.Value);
        command.Parameters.AddWithValue(
            "@failure_retryable",
            failure is null ? DBNull.Value : failure.Retryable ? 1 : 0);
    }

    private static void SetIdentityParameters(
        SqliteCommand command,
        string volumeParameter,
        string referenceParameter,
        FileIdentity? identity)
    {
        command.Parameters[volumeParameter].Value = identity is { } value
            ? ToSqliteInteger(value.VolumeSerialNumber)
            : DBNull.Value;
        command.Parameters[referenceParameter].Value = identity is { } reference
            ? ToSqliteInteger(reference.FileReferenceNumber)
            : DBNull.Value;
    }

    private async ValueTask<FileOperationActionHistory> LoadRequiredAsync(
        SqliteConnection connection,
        Guid operationId,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null) =>
        await LoadAsync(connection, operationId, cancellationToken, transaction).ConfigureAwait(false)
        ?? throw new InvalidOperationException($"Action history for operation {operationId} does not exist.");

    private static async ValueTask<FileOperationActionHistory?> LoadAsync(
        SqliteConnection connection,
        Guid operationId,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        var operationKey = FormatOperationId(operationId);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                queued_utc_ticks,
                validated_utc_ticks,
                started_utc_ticks,
                completed_utc_ticks,
                kind,
                collision_policy,
                source_directory_path,
                destination_directory_path,
                canonical_source_directory_path,
                canonical_destination_directory_path,
                terminal_state
            FROM file_operation_actions
            WHERE operation_id = @operation_id;
            """;
        command.Parameters.AddWithValue("@operation_id", operationKey);

        DateTimeOffset queuedAt;
        DateTimeOffset validatedAt;
        DateTimeOffset startedAt;
        DateTimeOffset? completedAt;
        FileOperationKind kind;
        FileOperationCollisionPolicy collisionPolicy;
        string sourceDirectoryPath;
        string destinationDirectoryPath;
        string canonicalSourceDirectoryPath;
        string canonicalDestinationDirectoryPath;
        FileOperationActionTerminalState? terminalState;

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            queuedAt = ReadUtcTicks(reader.GetInt64(0));
            validatedAt = ReadUtcTicks(reader.GetInt64(1));
            startedAt = ReadUtcTicks(reader.GetInt64(2));
            completedAt = reader.IsDBNull(3) ? null : ReadUtcTicks(reader.GetInt64(3));
            kind = ReadEnum<FileOperationKind>(reader.GetInt64(4), "operation kind");
            collisionPolicy = ReadEnum<FileOperationCollisionPolicy>(reader.GetInt64(5), "collision policy");
            sourceDirectoryPath = reader.GetString(6);
            destinationDirectoryPath = reader.GetString(7);
            canonicalSourceDirectoryPath = reader.GetString(8);
            canonicalDestinationDirectoryPath = reader.GetString(9);
            terminalState = reader.IsDBNull(10)
                ? null
                : ReadEnum<FileOperationActionTerminalState>(reader.GetInt64(10), "terminal state");
        }

        using var entriesCommand = connection.CreateCommand();
        entriesCommand.Transaction = transaction;
        entriesCommand.CommandText = """
            SELECT
                ordinal,
                source_path,
                source_name,
                is_directory,
                canonical_source_path,
                canonical_destination_path,
                state,
                mutation_started_utc_ticks,
                completed_utc_ticks,
                source_volume_serial,
                source_file_reference,
                destination_volume_serial,
                destination_file_reference,
                undo_kind,
                failure_code,
                failure_message,
                failure_path,
                failure_retryable
            FROM file_operation_action_entries
            WHERE operation_id = @operation_id
            ORDER BY ordinal;
            """;
        entriesCommand.Parameters.AddWithValue("@operation_id", operationKey);

        var entries = new List<FileOperationActionEntry>();
        await using (var reader = await entriesCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var ordinal = checked((int)reader.GetInt64(0));
                var state = ReadEnum<FileOperationActionEntryState>(reader.GetInt64(6), "entry state");
                var undoKind = ReadEnum<FileOperationUndoKind>(reader.GetInt64(13), "undo kind");
                var failure = ReadFailure(reader, 14);
                entries.Add(new FileOperationActionEntry(
                    ordinal,
                    new FileOperationEntry(
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetInt64(3) != 0),
                    reader.GetString(4),
                    reader.GetString(5),
                    state,
                    reader.IsDBNull(7) ? null : ReadUtcTicks(reader.GetInt64(7)),
                    reader.IsDBNull(8) ? null : ReadUtcTicks(reader.GetInt64(8)),
                    ReadIdentity(reader, 9, 10),
                    ReadIdentity(reader, 11, 12),
                    undoKind,
                    failure));
            }
        }

        if (entries.Count == 0 ||
            entries.Select(static entry => entry.Ordinal).Distinct().Count() != entries.Count ||
            entries.Where((entry, index) => entry.Ordinal != index).Any())
        {
            throw new InvalidDataException("Persisted action-history entries are missing or have invalid ordinals.");
        }

        var history = new FileOperationActionHistory(
            operationId,
            queuedAt,
            validatedAt,
            startedAt,
            completedAt,
            kind,
            collisionPolicy,
            sourceDirectoryPath,
            destinationDirectoryPath,
            canonicalSourceDirectoryPath,
            canonicalDestinationDirectoryPath,
            terminalState,
            entries.AsReadOnly());
        ValidatePersistedHistory(history);
        return history;
    }

    private static void ValidatePersistedHistory(FileOperationActionHistory history)
    {
        foreach (var entry in history.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Entry.Path) ||
                string.IsNullOrWhiteSpace(entry.Entry.Name) ||
                string.IsNullOrWhiteSpace(entry.CanonicalSourcePath) ||
                string.IsNullOrWhiteSpace(entry.CanonicalDestinationPath))
            {
                throw new InvalidDataException("Persisted action-history paths are invalid.");
            }

            if (entry.IsUndoEligible && history.Kind != FileOperationKind.Copy)
            {
                throw new InvalidDataException("Only committed Copy entries may carry delete-destination undo metadata.");
            }

            if (entry.State == FileOperationActionEntryState.Committed &&
                entry.UndoKind == FileOperationUndoKind.DeleteCreatedDestination &&
                !entry.DestinationIdentity.HasValue)
            {
                throw new InvalidDataException("Undo-eligible Copy history is missing destination identity.");
            }

            if (entry.State == FileOperationActionEntryState.MutationStarted &&
                !entry.MutationStartedAtUtc.HasValue)
            {
                throw new InvalidDataException("MutationStarted action history is missing its start timestamp.");
            }
        }

        if (history.TerminalState == FileOperationActionTerminalState.Succeeded &&
            history.Entries.Any(static entry =>
                entry.State is not FileOperationActionEntryState.Committed and
                    not FileOperationActionEntryState.Skipped))
        {
            throw new InvalidDataException("Persisted successful action history contains an incomplete entry.");
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
                throw new InvalidDataException("Persisted action-history failure columns are inconsistent.");
            }

            return null;
        }

        if (reader.IsDBNull(startOrdinal + 1) || reader.IsDBNull(startOrdinal + 3))
        {
            throw new InvalidDataException("Persisted action-history failure data is incomplete.");
        }

        return new FileOperationFailure(
            reader.GetString(startOrdinal),
            reader.GetString(startOrdinal + 1),
            reader.IsDBNull(startOrdinal + 2) ? null : reader.GetString(startOrdinal + 2),
            reader.GetInt64(startOrdinal + 3) != 0);
    }

    private static FileIdentity? ReadIdentity(
        SqliteDataReader reader,
        int volumeOrdinal,
        int referenceOrdinal)
    {
        var hasVolume = !reader.IsDBNull(volumeOrdinal);
        var hasReference = !reader.IsDBNull(referenceOrdinal);
        if (hasVolume != hasReference)
        {
            throw new InvalidDataException("Persisted action-history file identity is incomplete.");
        }

        if (!hasVolume)
        {
            return null;
        }

        return new FileIdentity(
            FromSqliteInteger(reader.GetInt64(volumeOrdinal)),
            FromSqliteInteger(reader.GetInt64(referenceOrdinal)));
    }

    private static TEnum ReadEnum<TEnum>(long value, string description)
        where TEnum : struct, Enum
    {
        var converted = checked((int)value);
        if (!Enum.IsDefined(typeof(TEnum), converted))
        {
            throw new InvalidDataException($"Unknown action-history {description} value {converted}.");
        }

        return (TEnum)Enum.ToObject(typeof(TEnum), converted);
    }

    private static string FormatOperationId(Guid operationId) => operationId.ToString("D");

    private static Guid ParseOperationId(string value) =>
        Guid.TryParseExact(value, "D", out var parsed)
            ? parsed
            : throw new InvalidDataException($"Persisted action-history operation id is invalid: {value}.");

    private static DateTimeOffset NormalizeUtc(DateTimeOffset value) => value.ToUniversalTime();

    private static long ToUtcTicks(DateTimeOffset value) =>
        NormalizeUtc(value).UtcDateTime.Ticks;

    private static DateTimeOffset ReadUtcTicks(long ticks)
    {
        try
        {
            return new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException("Persisted action-history UTC ticks are invalid.", exception);
        }
    }

    private static long ToSqliteInteger(ulong value) => unchecked((long)value);

    private static ulong FromSqliteInteger(long value) => unchecked((ulong)value);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
