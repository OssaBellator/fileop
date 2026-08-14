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

public sealed class SqliteFileOperationActionHistoryStore :
    IFileOperationActionHistoryStore,
    IFileMoveOperationActionHistoryStore,
    IDisposable
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
        var operationKey = FormatOperationId(plan.Id);
        var entries = CreateInitialEntries(validation, startedAt);
        var sourceDirectoryIdentity = validation.SourceDirectory.Identity
            ?? throw new InvalidOperationException("Validated source directory identity is missing.");
        var destinationDirectoryIdentity = validation.DestinationDirectory.Identity
            ?? throw new InvalidOperationException("Validated destination directory identity is missing.");

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
                operation.Parameters.AddWithValue("@operation_id", operationKey);
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

            await PersistRootIdentitiesAsync(
                connection,
                transaction,
                operationKey,
                sourceDirectoryIdentity,
                destinationDirectoryIdentity,
                cancellationToken).ConfigureAwait(false);

            using (var entryCommand = CreateEntryInsertCommand(connection, transaction))
            {
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    BindEntryInsert(entryCommand, operationKey, entry);
                    await entryCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            var history = new FileOperationActionHistory(
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
                Array.AsReadOnly(entries),
                SourceDirectoryIdentity: sourceDirectoryIdentity,
                DestinationDirectoryIdentity: destinationDirectoryIdentity);
            ValidatePersistedHistory(history);
            transaction.Commit();
            return history;
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
            cancellationToken);

    public ValueTask<FileOperationActionHistory> MarkEntryFailedBeforeMutationAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateFailure(failure);
        return TransitionEntryAsync(
            operationId,
            ordinal,
            FileOperationActionEntryState.Pending,
            FileOperationActionEntryState.Failed,
            NormalizeUtc(failedAtUtc),
            failure,
            cancellationToken);
    }

    public async ValueTask<FileOperationActionHistory> CommitCopyAsync(
        Guid operationId,
        int ordinal,
        FileIdentity destinationIdentity,
        FileContentFingerprint destinationContentFingerprint,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destinationContentFingerprint);
        ValidateOrdinal(ordinal);
        var operationKey = FormatOperationId(operationId);
        var committedAt = NormalizeUtc(committedAtUtc);

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
            command.Parameters.AddWithValue(
                "@mutation_started",
                (int)FileOperationActionEntryState.MutationStarted);
            command.Parameters.AddWithValue("@copy_kind", (int)FileOperationKind.Copy);

            var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (changed != 1)
            {
                throw new InvalidOperationException(
                    "A Copy entry can be committed only once, after MutationStarted, while its operation remains active.");
            }

            await PersistDestinationContentFingerprintAsync(
                connection,
                transaction,
                operationKey,
                ordinal,
                destinationContentFingerprint,
                cancellationToken).ConfigureAwait(false);

            var history = await LoadRequiredAsync(
                connection,
                operationId,
                cancellationToken,
                transaction).ConfigureAwait(false);
            transaction.Commit();
            return history;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<FileOperationActionHistory> CommitSameVolumeMoveAsync(
        Guid operationId,
        int ordinal,
        FileIdentity destinationIdentity,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateOrdinal(ordinal);
        var operationKey = FormatOperationId(operationId);
        var committedAt = NormalizeUtc(committedAtUtc);

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
            ValidateSameVolumeMoveMutation(current, ordinal, destinationIdentity);

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
                        AND action.kind = @move_kind
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
            command.Parameters.AddWithValue("@undo_kind", (int)FileOperationUndoKind.None);
            command.Parameters.AddWithValue("@operation_id", operationKey);
            command.Parameters.AddWithValue("@ordinal", ordinal);
            command.Parameters.AddWithValue(
                "@mutation_started",
                (int)FileOperationActionEntryState.MutationStarted);
            command.Parameters.AddWithValue("@move_kind", (int)FileOperationKind.Move);

            var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (changed != 1)
            {
                throw new InvalidOperationException(
                    "A same-volume Move entry can be committed only once, after MutationStarted, while its Move operation remains active.");
            }

            var history = await LoadRequiredAsync(
                connection,
                operationId,
                cancellationToken,
                transaction).ConfigureAwait(false);
            transaction.Commit();
            return history;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<FileOperationActionHistory> MarkSameVolumeMoveRecoveryRequiredAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        DateTimeOffset failedAtUtc,
        FileIdentity? observedDestinationIdentity = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateFailure(failure);
        ValidateOrdinal(ordinal);
        var operationKey = FormatOperationId(operationId);
        var failedAt = NormalizeUtc(failedAtUtc);

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
            ValidateSameVolumeMoveMutation(current, ordinal, observedDestinationIdentity);

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE file_operation_action_entries
                SET state = @recovery_required,
                    completed_utc_ticks = @completed_utc_ticks,
                    destination_volume_serial = @destination_volume_serial,
                    destination_file_reference = @destination_file_reference,
                    undo_kind = @undo_kind,
                    failure_code = @failure_code,
                    failure_message = @failure_message,
                    failure_path = @failure_path,
                    failure_retryable = @failure_retryable
                WHERE operation_id = @operation_id
                  AND ordinal = @ordinal
                  AND state = @mutation_started
                  AND EXISTS(
                      SELECT 1
                      FROM file_operation_actions AS action
                      WHERE action.operation_id = @operation_id
                        AND action.terminal_state IS NULL
                        AND action.kind = @move_kind
                  );
                """;
            command.Parameters.AddWithValue(
                "@recovery_required",
                (int)FileOperationActionEntryState.RecoveryRequired);
            command.Parameters.AddWithValue("@completed_utc_ticks", ToUtcTicks(failedAt));
            command.Parameters.Add("@destination_volume_serial", SqliteType.Integer).Value =
                observedDestinationIdentity is { } identity
                    ? ToSqliteInteger(identity.VolumeSerialNumber)
                    : DBNull.Value;
            command.Parameters.Add("@destination_file_reference", SqliteType.Integer).Value =
                observedDestinationIdentity is { } reference
                    ? ToSqliteInteger(reference.FileReferenceNumber)
                    : DBNull.Value;
            command.Parameters.AddWithValue("@undo_kind", (int)FileOperationUndoKind.None);
            SetFailureParameters(command, failure);
            command.Parameters.AddWithValue("@operation_id", operationKey);
            command.Parameters.AddWithValue("@ordinal", ordinal);
            command.Parameters.AddWithValue(
                "@mutation_started",
                (int)FileOperationActionEntryState.MutationStarted);
            command.Parameters.AddWithValue("@move_kind", (int)FileOperationKind.Move);

            var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (changed != 1)
            {
                throw new InvalidOperationException(
                    "A same-volume Move entry can require recovery only after MutationStarted while its Move operation remains active.");
            }

            var history = await LoadRequiredAsync(
                connection,
                operationId,
                cancellationToken,
                transaction).ConfigureAwait(false);
            transaction.Commit();
            return history;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<FileOperationActionHistory> MarkMutationRecoveryRequiredAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken = default,
        FileIdentity? destinationIdentity = null,
        FileContentFingerprint? destinationContentFingerprint = null)
    {
        ThrowIfDisposed();
        ValidateFailure(failure);
        ValidateOrdinal(ordinal);
        if (destinationIdentity.HasValue != (destinationContentFingerprint is not null))
        {
            throw new ArgumentException(
                "Verified recovery evidence requires both destination identity and content fingerprint, or neither.");
        }

        var operationKey = FormatOperationId(operationId);
        var failedAt = NormalizeUtc(failedAtUtc);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE file_operation_action_entries
                SET state = @recovery_required,
                    completed_utc_ticks = @completed_utc_ticks,
                    destination_volume_serial = @destination_volume_serial,
                    destination_file_reference = @destination_file_reference,
                    undo_kind = @undo_kind,
                    failure_code = @failure_code,
                    failure_message = @failure_message,
                    failure_path = @failure_path,
                    failure_retryable = @failure_retryable
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
            command.Parameters.AddWithValue(
                "@recovery_required",
                (int)FileOperationActionEntryState.RecoveryRequired);
            command.Parameters.AddWithValue("@completed_utc_ticks", ToUtcTicks(failedAt));
            command.Parameters.Add("@destination_volume_serial", SqliteType.Integer).Value =
                destinationIdentity is { } identity
                    ? ToSqliteInteger(identity.VolumeSerialNumber)
                    : DBNull.Value;
            command.Parameters.Add("@destination_file_reference", SqliteType.Integer).Value =
                destinationIdentity is { } reference
                    ? ToSqliteInteger(reference.FileReferenceNumber)
                    : DBNull.Value;
            command.Parameters.AddWithValue("@undo_kind", (int)FileOperationUndoKind.None);
            SetFailureParameters(command, failure);
            command.Parameters.AddWithValue("@operation_id", operationKey);
            command.Parameters.AddWithValue("@ordinal", ordinal);
            command.Parameters.AddWithValue(
                "@mutation_started",
                (int)FileOperationActionEntryState.MutationStarted);
            command.Parameters.AddWithValue("@copy_kind", (int)FileOperationKind.Copy);

            var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (changed != 1)
            {
                throw new InvalidOperationException(
                    "A Copy entry can require recovery only after MutationStarted while its operation remains active.");
            }

            if (destinationContentFingerprint is not null)
            {
                await PersistDestinationContentFingerprintAsync(
                    connection,
                    transaction,
                    operationKey,
                    ordinal,
                    destinationContentFingerprint,
                    cancellationToken).ConfigureAwait(false);
            }

            var history = await LoadRequiredAsync(
                connection,
                operationId,
                cancellationToken,
                transaction).ConfigureAwait(false);
            transaction.Commit();
            return history;
        }
        finally
        {
            _writeGate.Release();
        }
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

        var operationKey = FormatOperationId(operationId);
        var completedAt = NormalizeUtc(completedAtUtc);

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

            var history = await LoadRequiredAsync(
                connection,
                operationId,
                cancellationToken,
                transaction).ConfigureAwait(false);
            transaction.Commit();
            return history;
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
                $"Action-history limits must be between 1 and {MaximumRecentLimit:N0}." );
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
                    destination_volume_serial = NULL,
                    destination_file_reference = NULL,
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
            command.Parameters.AddWithValue(
                "@mutation_started",
                (int)FileOperationActionEntryState.MutationStarted);
            command.Parameters.AddWithValue("@failed", (int)FileOperationActionEntryState.Failed);
            command.Parameters.AddWithValue(
                "@recovery_required",
                (int)FileOperationActionEntryState.RecoveryRequired);
            command.Parameters.AddWithValue("@timestamp_utc_ticks", ToUtcTicks(timestampUtc));
            command.Parameters.AddWithValue("@undo_kind", (int)FileOperationUndoKind.None);
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

            var history = await LoadRequiredAsync(
                connection,
                operationId,
                cancellationToken,
                transaction).ConfigureAwait(false);
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
                    $"Unsupported FileOp action-history schema version {actual}; expected {SchemaVersion}." );
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
                kind INTEGER NOT NULL CHECK(kind BETWEEN 0 AND 1),
                collision_policy INTEGER NOT NULL CHECK(collision_policy BETWEEN 0 AND 2),
                source_directory_path TEXT NOT NULL,
                destination_directory_path TEXT NOT NULL,
                canonical_source_directory_path TEXT NOT NULL,
                canonical_destination_directory_path TEXT NOT NULL,
                terminal_state INTEGER NULL CHECK(terminal_state IS NULL OR terminal_state BETWEEN 0 AND 3)
            ) WITHOUT ROWID;

            CREATE INDEX IF NOT EXISTS ix_file_operation_actions_started
                ON file_operation_actions(started_utc_ticks DESC, operation_id DESC);

            CREATE TABLE IF NOT EXISTS file_operation_action_root_identities(
                operation_id TEXT PRIMARY KEY,
                source_volume_serial INTEGER NOT NULL,
                source_file_reference INTEGER NOT NULL,
                destination_volume_serial INTEGER NOT NULL,
                destination_file_reference INTEGER NOT NULL,
                FOREIGN KEY(operation_id) REFERENCES file_operation_actions(operation_id) ON DELETE CASCADE
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS file_operation_action_entries(
                operation_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
                source_path TEXT NOT NULL,
                source_name TEXT NOT NULL,
                is_directory INTEGER NOT NULL CHECK(is_directory IN (0, 1)),
                canonical_source_path TEXT NOT NULL,
                canonical_destination_path TEXT NOT NULL,
                state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 5),
                mutation_started_utc_ticks INTEGER NULL,
                completed_utc_ticks INTEGER NULL,
                source_volume_serial INTEGER NULL,
                source_file_reference INTEGER NULL,
                destination_volume_serial INTEGER NULL,
                destination_file_reference INTEGER NULL,
                undo_kind INTEGER NOT NULL CHECK(undo_kind BETWEEN 0 AND 1),
                failure_code TEXT NULL,
                failure_message TEXT NULL,
                failure_path TEXT NULL,
                failure_retryable INTEGER NULL CHECK(failure_retryable IS NULL OR failure_retryable IN (0, 1)),
                PRIMARY KEY(operation_id, ordinal),
                FOREIGN KEY(operation_id) REFERENCES file_operation_actions(operation_id) ON DELETE CASCADE
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS file_operation_action_entry_content_fingerprints(
                operation_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
                algorithm INTEGER NOT NULL CHECK(algorithm = 1),
                digest_hex TEXT NOT NULL CHECK(length(digest_hex) = 64),
                PRIMARY KEY(operation_id, ordinal),
                FOREIGN KEY(operation_id, ordinal)
                    REFERENCES file_operation_action_entries(operation_id, ordinal)
                    ON DELETE CASCADE
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

    private static FileOperationActionEntry[] CreateInitialEntries(
        FileOperationExecutionValidationResult validation,
        DateTimeOffset startedAtUtc)
    {
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
                CompletedAtUtc: state == FileOperationActionEntryState.Skipped ? startedAtUtc : null,
                item.Source.Identity,
                item.Destination.Identity,
                FileOperationUndoKind.None,
                Failure: null);
        }

        return entries;
    }

    private static SqliteCommand CreateEntryInsertCommand(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
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
        command.Parameters.Add("@operation_id", SqliteType.Text);
        command.Parameters.Add("@ordinal", SqliteType.Integer);
        command.Parameters.Add("@source_path", SqliteType.Text);
        command.Parameters.Add("@source_name", SqliteType.Text);
        command.Parameters.Add("@is_directory", SqliteType.Integer);
        command.Parameters.Add("@canonical_source_path", SqliteType.Text);
        command.Parameters.Add("@canonical_destination_path", SqliteType.Text);
        command.Parameters.Add("@state", SqliteType.Integer);
        command.Parameters.Add("@completed_utc_ticks", SqliteType.Integer);
        command.Parameters.Add("@source_volume_serial", SqliteType.Integer);
        command.Parameters.Add("@source_file_reference", SqliteType.Integer);
        command.Parameters.Add("@destination_volume_serial", SqliteType.Integer);
        command.Parameters.Add("@destination_file_reference", SqliteType.Integer);
        command.Parameters.Add("@undo_kind", SqliteType.Integer);
        return command;
    }

    private static void BindEntryInsert(
        SqliteCommand command,
        string operationKey,
        FileOperationActionEntry entry)
    {
        command.Parameters["@operation_id"].Value = operationKey;
        command.Parameters["@ordinal"].Value = entry.Ordinal;
        command.Parameters["@source_path"].Value = entry.Entry.Path;
        command.Parameters["@source_name"].Value = entry.Entry.Name;
        command.Parameters["@is_directory"].Value = entry.Entry.IsDirectory ? 1 : 0;
        command.Parameters["@canonical_source_path"].Value = entry.CanonicalSourcePath;
        command.Parameters["@canonical_destination_path"].Value = entry.CanonicalDestinationPath;
        command.Parameters["@state"].Value = (int)entry.State;
        command.Parameters["@completed_utc_ticks"].Value =
            entry.CompletedAtUtc is { } completedAt ? ToUtcTicks(completedAt) : DBNull.Value;
        SetExistingIdentityParameters(
            command,
            "@source_volume_serial",
            "@source_file_reference",
            entry.SourceIdentity);
        SetExistingIdentityParameters(
            command,
            "@destination_volume_serial",
            "@destination_file_reference",
            entry.DestinationIdentity);
        command.Parameters["@undo_kind"].Value = (int)entry.UndoKind;
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

        if (validation.SourceDirectory.State != FileOperationCanonicalPathState.Directory ||
            validation.DestinationDirectory.State != FileOperationCanonicalPathState.Directory ||
            validation.SourceDirectory.IsLeafReparsePoint ||
            validation.DestinationDirectory.IsLeafReparsePoint ||
            !validation.SourceDirectory.Identity.HasValue ||
            !validation.DestinationDirectory.Identity.HasValue ||
            string.IsNullOrWhiteSpace(validation.SourceDirectory.CanonicalPath) ||
            string.IsNullOrWhiteSpace(validation.DestinationDirectory.CanonicalPath))
        {
            throw new ArgumentException(
                "Execution validation must contain canonical non-reparse source/destination directories with stable identities.",
                nameof(validation));
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

            var expectedSourceState = planned.IsDirectory
                ? FileOperationCanonicalPathState.Directory
                : FileOperationCanonicalPathState.File;
            if (item.Source.State != expectedSourceState ||
                item.Source.IsLeafReparsePoint ||
                !item.Source.Identity.HasValue ||
                string.IsNullOrWhiteSpace(item.Source.CanonicalPath) ||
                string.IsNullOrWhiteSpace(item.Destination.CanonicalPath))
            {
                throw new ArgumentException(
                    "Execution validation does not contain a stable canonical source entry.",
                    nameof(validation));
            }

            if (item.Decision == FileOperationExecutionValidationDecision.Ready)
            {
                if (item.Destination.State != FileOperationCanonicalPathState.Missing ||
                    item.Destination.Identity.HasValue)
                {
                    throw new ArgumentException(
                        "A ready mutation entry must have a missing canonical destination leaf.",
                        nameof(validation));
                }
            }
            else if (item.Decision == FileOperationExecutionValidationDecision.Skip)
            {
                if (!item.Destination.Exists)
                {
                    throw new ArgumentException(
                        "A skipped entry must describe the existing canonical destination.",
                        nameof(validation));
                }
            }
            else
            {
                throw new ArgumentException(
                    "Action history cannot begin with blocked or unresolved validation items.",
                    nameof(validation));
            }
        }
    }

    private static void ValidateSameVolumeMoveMutation(
        FileOperationActionHistory history,
        int ordinal,
        FileIdentity? observedDestinationIdentity)
    {
        if (history.Kind != FileOperationKind.Move ||
            history.TerminalState.HasValue ||
            history.SourceDirectoryIdentity is not FileIdentity sourceDirectoryIdentity ||
            history.DestinationDirectoryIdentity is not FileIdentity destinationDirectoryIdentity ||
            sourceDirectoryIdentity.VolumeSerialNumber != destinationDirectoryIdentity.VolumeSerialNumber)
        {
            throw new InvalidOperationException(
                "Same-volume Move history requires an active Move operation whose canonical roots are identity-bound to one volume.");
        }

        if (ordinal >= history.Entries.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        var entry = history.Entries[ordinal];
        if (entry.Entry.IsDirectory ||
            entry.State != FileOperationActionEntryState.MutationStarted ||
            entry.SourceIdentity is not FileIdentity sourceIdentity ||
            sourceIdentity.VolumeSerialNumber != sourceDirectoryIdentity.VolumeSerialNumber ||
            entry.DestinationIdentity.HasValue ||
            entry.UndoKind != FileOperationUndoKind.None ||
            entry.DestinationContentFingerprint is not null ||
            entry.DestinationHardLinkCount.HasValue)
        {
            throw new InvalidOperationException(
                "Same-volume Move history can settle only a regular file at its durable MutationStarted boundary with unchanged source identity evidence.");
        }

        if (observedDestinationIdentity is FileIdentity destinationIdentity &&
            destinationIdentity != sourceIdentity)
        {
            throw new InvalidOperationException(
                "A same-volume rename/move must preserve the exact filesystem object identity at the destination.");
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

    private static void ValidateFailure(FileOperationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (string.IsNullOrWhiteSpace(failure.Code) || string.IsNullOrWhiteSpace(failure.Message))
        {
            throw new ArgumentException("Action-history failures require a code and message.", nameof(failure));
        }
    }

    private static void ValidatePersistedFailure(FileOperationFailure failure)
    {
        if (string.IsNullOrWhiteSpace(failure.Code) || string.IsNullOrWhiteSpace(failure.Message))
        {
            throw new InvalidDataException("Persisted action-history failure requires a code and message.");
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

    private static void SetExistingIdentityParameters(
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

    private static async ValueTask PersistRootIdentitiesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationKey,
        FileIdentity sourceDirectoryIdentity,
        FileIdentity destinationDirectoryIdentity,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO file_operation_action_root_identities(
                operation_id,
                source_volume_serial,
                source_file_reference,
                destination_volume_serial,
                destination_file_reference)
            VALUES(
                @operation_id,
                @source_volume_serial,
                @source_file_reference,
                @destination_volume_serial,
                @destination_file_reference);
            """;
        command.Parameters.AddWithValue("@operation_id", operationKey);
        command.Parameters.AddWithValue(
            "@source_volume_serial",
            ToSqliteInteger(sourceDirectoryIdentity.VolumeSerialNumber));
        command.Parameters.AddWithValue(
            "@source_file_reference",
            ToSqliteInteger(sourceDirectoryIdentity.FileReferenceNumber));
        command.Parameters.AddWithValue(
            "@destination_volume_serial",
            ToSqliteInteger(destinationDirectoryIdentity.VolumeSerialNumber));
        command.Parameters.AddWithValue(
            "@destination_file_reference",
            ToSqliteInteger(destinationDirectoryIdentity.FileReferenceNumber));
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (changed != 1)
        {
            throw new InvalidOperationException("Root identity persistence did not write exactly one row.");
        }
    }

    private static async ValueTask PersistDestinationContentFingerprintAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationKey,
        int ordinal,
        FileContentFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO file_operation_action_entry_content_fingerprints(
                operation_id,
                ordinal,
                algorithm,
                digest_hex)
            VALUES(
                @operation_id,
                @ordinal,
                @algorithm,
                @digest_hex);
            """;
        command.Parameters.AddWithValue("@operation_id", operationKey);
        command.Parameters.AddWithValue("@ordinal", ordinal);
        command.Parameters.AddWithValue("@algorithm", (int)fingerprint.Algorithm);
        command.Parameters.AddWithValue("@digest_hex", fingerprint.HexDigest);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (changed != 1)
        {
            throw new InvalidOperationException("Destination content fingerprint persistence did not write exactly one row.");
        }
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
        using var operationCommand = connection.CreateCommand();
        operationCommand.Transaction = transaction;
        operationCommand.CommandText = """
            SELECT
                action.queued_utc_ticks,
                action.validated_utc_ticks,
                action.started_utc_ticks,
                action.completed_utc_ticks,
                action.kind,
                action.collision_policy,
                action.source_directory_path,
                action.destination_directory_path,
                action.canonical_source_directory_path,
                action.canonical_destination_directory_path,
                action.terminal_state,
                roots.source_volume_serial,
                roots.source_file_reference,
                roots.destination_volume_serial,
                roots.destination_file_reference
            FROM file_operation_actions AS action
            LEFT JOIN file_operation_action_root_identities AS roots
              ON roots.operation_id = action.operation_id
            WHERE action.operation_id = @operation_id;
            """;
        operationCommand.Parameters.AddWithValue("@operation_id", operationKey);

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
        FileIdentity? sourceDirectoryIdentity;
        FileIdentity? destinationDirectoryIdentity;

        await using (var reader = await operationCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
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
            sourceDirectoryIdentity = ReadIdentity(reader, 11, 12);
            destinationDirectoryIdentity = ReadIdentity(reader, 13, 14);
            if (sourceDirectoryIdentity.HasValue != destinationDirectoryIdentity.HasValue)
            {
                throw new InvalidDataException("Persisted action-history root identity evidence is incomplete.");
            }
        }

        using var entriesCommand = connection.CreateCommand();
        entriesCommand.Transaction = transaction;
        entriesCommand.CommandText = """
            SELECT
                entry.ordinal,
                entry.source_path,
                entry.source_name,
                entry.is_directory,
                entry.canonical_source_path,
                entry.canonical_destination_path,
                entry.state,
                entry.mutation_started_utc_ticks,
                entry.completed_utc_ticks,
                entry.source_volume_serial,
                entry.source_file_reference,
                entry.destination_volume_serial,
                entry.destination_file_reference,
                entry.undo_kind,
                entry.failure_code,
                entry.failure_message,
                entry.failure_path,
                entry.failure_retryable,
                fingerprint.algorithm,
                fingerprint.digest_hex
            FROM file_operation_action_entries AS entry
            LEFT JOIN file_operation_action_entry_content_fingerprints AS fingerprint
              ON fingerprint.operation_id = entry.operation_id
             AND fingerprint.ordinal = entry.ordinal
            WHERE entry.operation_id = @operation_id
            ORDER BY entry.ordinal;
            """;
        entriesCommand.Parameters.AddWithValue("@operation_id", operationKey);

        var entries = new List<FileOperationActionEntry>();
        await using (var reader = await entriesCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var ordinal = checked((int)reader.GetInt64(0));
                entries.Add(new FileOperationActionEntry(
                    ordinal,
                    new FileOperationEntry(
                        reader.GetString(1),
                        reader.GetString(2),
                        ReadBoolean(reader, 3, "is_directory")),
                    reader.GetString(4),
                    reader.GetString(5),
                    ReadEnum<FileOperationActionEntryState>(reader.GetInt64(6), "entry state"),
                    reader.IsDBNull(7) ? null : ReadUtcTicks(reader.GetInt64(7)),
                    reader.IsDBNull(8) ? null : ReadUtcTicks(reader.GetInt64(8)),
                    ReadIdentity(reader, 9, 10),
                    ReadIdentity(reader, 11, 12),
                    ReadEnum<FileOperationUndoKind>(reader.GetInt64(13), "undo kind"),
                    ReadFailure(reader, 14),
                    ReadContentFingerprint(reader, 18, 19)));
            }
        }

        if (entries.Count == 0 || entries.Where((entry, index) => entry.Ordinal != index).Any())
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
            entries.AsReadOnly(),
            sourceDirectoryIdentity,
            destinationDirectoryIdentity);
        ValidatePersistedHistory(history);
        return history;
    }

    private static void ValidatePersistedHistory(FileOperationActionHistory history)
    {
        if (string.IsNullOrWhiteSpace(history.SourceDirectoryPath) ||
            string.IsNullOrWhiteSpace(history.DestinationDirectoryPath) ||
            string.IsNullOrWhiteSpace(history.CanonicalSourceDirectoryPath) ||
            string.IsNullOrWhiteSpace(history.CanonicalDestinationDirectoryPath) ||
            history.SourceDirectoryIdentity.HasValue != history.DestinationDirectoryIdentity.HasValue ||
            history.TerminalState.HasValue != history.CompletedAtUtc.HasValue)
        {
            throw new InvalidDataException("Persisted action-history operation metadata is inconsistent.");
        }

        foreach (var entry in history.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Entry.Path) ||
                string.IsNullOrWhiteSpace(entry.Entry.Name) ||
                string.IsNullOrWhiteSpace(entry.CanonicalSourcePath) ||
                string.IsNullOrWhiteSpace(entry.CanonicalDestinationPath))
            {
                throw new InvalidDataException("Persisted action-history entry paths are invalid.");
            }

            if (entry.DestinationContentFingerprint is not null &&
                (history.Kind != FileOperationKind.Copy ||
                 entry.State is not FileOperationActionEntryState.Committed and
                    not FileOperationActionEntryState.RecoveryRequired ||
                 !entry.DestinationIdentity.HasValue))
            {
                throw new InvalidDataException(
                    "Persisted content fingerprint evidence requires a committed/recovery Copy destination identity.");
            }

            switch (entry.State)
            {
                case FileOperationActionEntryState.Pending:
                    RequireEntryShape(entry, mutationStarted: false, completed: false, failure: false);
                    RequireNoUndo(entry);
                    if (entry.DestinationIdentity.HasValue)
                    {
                        throw new InvalidDataException("Pending action history cannot have a destination identity.");
                    }
                    break;

                case FileOperationActionEntryState.MutationStarted:
                    RequireEntryShape(entry, mutationStarted: true, completed: false, failure: false);
                    RequireNoUndo(entry);
                    if (entry.DestinationIdentity.HasValue)
                    {
                        throw new InvalidDataException("MutationStarted action history cannot have a committed destination identity.");
                    }
                    break;

                case FileOperationActionEntryState.Committed:
                    RequireEntryShape(entry, mutationStarted: true, completed: true, failure: false);
                    if (history.Kind == FileOperationKind.Copy)
                    {
                        if (entry.UndoKind != FileOperationUndoKind.DeleteCreatedDestination ||
                            !entry.DestinationIdentity.HasValue)
                        {
                            throw new InvalidDataException(
                                "Committed Copy history requires an exact destination identity and delete-destination undo metadata.");
                        }
                    }
                    else if (history.Kind == FileOperationKind.Move)
                    {
                        RequireNoUndo(entry);
                        if (entry.Entry.IsDirectory ||
                            entry.SourceIdentity is not FileIdentity sourceIdentity ||
                            entry.DestinationIdentity is not FileIdentity destinationIdentity ||
                            destinationIdentity != sourceIdentity ||
                            entry.DestinationContentFingerprint is not null ||
                            entry.DestinationHardLinkCount.HasValue)
                        {
                            throw new InvalidDataException(
                                "Committed same-volume Move history must preserve the exact source object identity without Copy-only evidence.");
                        }
                    }
                    else
                    {
                        throw new InvalidDataException("Committed action history has an unsupported operation kind.");
                    }
                    break;

                case FileOperationActionEntryState.Skipped:
                    RequireEntryShape(entry, mutationStarted: false, completed: true, failure: false);
                    RequireNoUndo(entry);
                    break;

                case FileOperationActionEntryState.Failed:
                    RequireEntryShape(entry, mutationStarted: false, completed: true, failure: true);
                    RequireNoUndo(entry);
                    break;

                case FileOperationActionEntryState.RecoveryRequired:
                    RequireEntryShape(entry, mutationStarted: true, completed: true, failure: null);
                    RequireNoUndo(entry);
                    if (history.Kind == FileOperationKind.Move &&
                        entry.DestinationIdentity is FileIdentity observedDestinationIdentity &&
                        (entry.SourceIdentity is not FileIdentity recoverySourceIdentity ||
                         observedDestinationIdentity != recoverySourceIdentity))
                    {
                        throw new InvalidDataException(
                            "Recovery-sensitive same-volume Move destination identity must match the original source identity.");
                    }
                    break;

                default:
                    throw new InvalidDataException("Persisted action-history entry state is invalid.");
            }
        }

        var hasRecovery = history.Entries.Any(static entry =>
            entry.State is FileOperationActionEntryState.MutationStarted or
                FileOperationActionEntryState.RecoveryRequired);
        if (history.TerminalState == FileOperationActionTerminalState.Succeeded &&
            history.Entries.Any(static entry =>
                entry.State is not FileOperationActionEntryState.Committed and
                    not FileOperationActionEntryState.Skipped))
        {
            throw new InvalidDataException("Persisted successful action history contains an incomplete entry.");
        }

        if ((history.TerminalState is FileOperationActionTerminalState.Failed or
                FileOperationActionTerminalState.Cancelled) && hasRecovery)
        {
            throw new InvalidDataException(
                "Persisted failed/cancelled action history cannot contain an unresolved mutation boundary.");
        }

        if (history.TerminalState == FileOperationActionTerminalState.RecoveryRequired && !hasRecovery)
        {
            throw new InvalidDataException(
                "Persisted RecoveryRequired history has no recovery-sensitive entry.");
        }
    }

    private static void RequireEntryShape(
        FileOperationActionEntry entry,
        bool mutationStarted,
        bool completed,
        bool? failure)
    {
        if (entry.MutationStartedAtUtc.HasValue != mutationStarted ||
            entry.CompletedAtUtc.HasValue != completed ||
            (failure.HasValue && (entry.Failure is not null) != failure.Value))
        {
            throw new InvalidDataException(
                $"Persisted action-history entry {entry.Ordinal} has timestamps/failure data inconsistent with state {entry.State}." );
        }

        if (entry.Failure is { } persistedFailure)
        {
            ValidatePersistedFailure(persistedFailure);
        }
    }

    private static void RequireNoUndo(FileOperationActionEntry entry)
    {
        if (entry.UndoKind != FileOperationUndoKind.None)
        {
            throw new InvalidDataException(
                $"Persisted action-history entry {entry.Ordinal} has undo metadata that its state does not permit." );
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

        var retryable = ReadBoolean(reader, startOrdinal + 3, "failure_retryable");
        var failure = new FileOperationFailure(
            reader.GetString(startOrdinal),
            reader.GetString(startOrdinal + 1),
            reader.IsDBNull(startOrdinal + 2) ? null : reader.GetString(startOrdinal + 2),
            retryable);
        ValidatePersistedFailure(failure);
        return failure;
    }

    private static FileContentFingerprint? ReadContentFingerprint(
        SqliteDataReader reader,
        int algorithmOrdinal,
        int digestOrdinal)
    {
        var hasAlgorithm = !reader.IsDBNull(algorithmOrdinal);
        var hasDigest = !reader.IsDBNull(digestOrdinal);
        if (hasAlgorithm != hasDigest)
        {
            throw new InvalidDataException("Persisted destination content fingerprint is incomplete.");
        }

        if (!hasAlgorithm)
        {
            return null;
        }

        var algorithm = ReadEnum<FileContentFingerprintAlgorithm>(
            reader.GetInt64(algorithmOrdinal),
            "content fingerprint algorithm");
        try
        {
            return new FileContentFingerprint(algorithm, reader.GetString(digestOrdinal));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Persisted destination content fingerprint is invalid.", exception);
        }
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

        return hasVolume
            ? new FileIdentity(
                FromSqliteInteger(reader.GetInt64(volumeOrdinal)),
                FromSqliteInteger(reader.GetInt64(referenceOrdinal)))
            : null;
    }

    private static bool ReadBoolean(SqliteDataReader reader, int ordinal, string description)
    {
        var value = reader.GetInt64(ordinal);
        return value switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidDataException(
                $"Persisted action-history {description} value {value} is not Boolean."),
        };
    }

    private static TEnum ReadEnum<TEnum>(long value, string description)
        where TEnum : struct, Enum
    {
        if (value < int.MinValue || value > int.MaxValue)
        {
            throw new InvalidDataException($"Unknown action-history {description} value {value}.");
        }

        var converted = (int)value;
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
            : throw new InvalidDataException($"Persisted action-history operation id is invalid: {value}." );

    private static DateTimeOffset NormalizeUtc(DateTimeOffset value) => value.ToUniversalTime();

    private static long ToUtcTicks(DateTimeOffset value) => NormalizeUtc(value).UtcDateTime.Ticks;

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

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SqliteFileOperationActionHistoryStore));
        }
    }
}
