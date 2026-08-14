using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using Microsoft.Data.Sqlite;

namespace FileOp.Core.Operations;

/// <summary>
/// Independent composite journal for cross-volume Move. It deliberately uses separate
/// tables from file_operation_actions: that journal represents Copy and same-volume Move
/// mutation state and must never be reinterpreted as Copy-plus-source-delete authority.
/// </summary>
public sealed class SqliteFileCrossVolumeMoveActionHistoryStore :
    IFileCrossVolumeMoveActionHistoryStore,
    IDisposable
{
    private const int SchemaVersion = 1;
    private const int MaximumRecentLimit = 4_096;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _disposed;

    public SqliteFileCrossVolumeMoveActionHistoryStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
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

    public async ValueTask<FileCrossVolumeMoveActionHistory> BeginAsync(
        FileOperationExecutionValidationResult validation,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validation);
        ThrowIfDisposed();
        ValidateBegin(validation);

        var plan = validation.Plan;
        var operationKey = FormatOperationId(plan.Id);
        var sourceRoot = validation.SourceDirectory.Identity!.Value;
        var destinationRoot = validation.DestinationDirectory.Identity!.Value;
        var startedAt = NormalizeUtc(startedAtUtc);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO file_cross_volume_move_actions(
                        operation_id,
                        queued_utc_ticks,
                        validated_utc_ticks,
                        started_utc_ticks,
                        completed_utc_ticks,
                        collision_policy,
                        source_directory_path,
                        destination_directory_path,
                        canonical_source_directory_path,
                        canonical_destination_directory_path,
                        source_root_volume_serial,
                        source_root_file_reference,
                        destination_root_volume_serial,
                        destination_root_file_reference,
                        terminal_state)
                    VALUES(
                        @operation_id,
                        @queued,
                        @validated,
                        @started,
                        NULL,
                        @collision,
                        @source_path,
                        @destination_path,
                        @canonical_source,
                        @canonical_destination,
                        @source_volume,
                        @source_reference,
                        @destination_volume,
                        @destination_reference,
                        NULL);
                    """;
                command.Parameters.AddWithValue("@operation_id", operationKey);
                command.Parameters.AddWithValue("@queued", ToUtcTicks(plan.QueuedAtUtc));
                command.Parameters.AddWithValue("@validated", ToUtcTicks(validation.ValidatedAtUtc));
                command.Parameters.AddWithValue("@started", ToUtcTicks(startedAt));
                command.Parameters.AddWithValue("@collision", (int)plan.CollisionPolicy);
                command.Parameters.AddWithValue("@source_path", plan.Intent.SourceDirectoryPath);
                command.Parameters.AddWithValue("@destination_path", plan.Intent.DestinationDirectoryPath);
                command.Parameters.AddWithValue("@canonical_source", validation.SourceDirectory.CanonicalPath);
                command.Parameters.AddWithValue("@canonical_destination", validation.DestinationDirectory.CanonicalPath);
                command.Parameters.AddWithValue("@source_volume", ToSqliteInteger(sourceRoot.VolumeSerialNumber));
                command.Parameters.AddWithValue("@source_reference", ToSqliteInteger(sourceRoot.FileReferenceNumber));
                command.Parameters.AddWithValue("@destination_volume", ToSqliteInteger(destinationRoot.VolumeSerialNumber));
                command.Parameters.AddWithValue("@destination_reference", ToSqliteInteger(destinationRoot.FileReferenceNumber));
                command.ExecuteNonQuery();
            }

            for (var ordinal = 0; ordinal < validation.Items.Count; ordinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = validation.Items[ordinal];
                var sourceIdentity = item.Source.Identity!.Value;
                var skipped = item.Decision == FileOperationExecutionValidationDecision.Skip;

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO file_cross_volume_move_entries(
                        operation_id,
                        ordinal,
                        entry_path,
                        entry_name,
                        is_directory,
                        canonical_source_path,
                        canonical_destination_path,
                        state,
                        source_volume_serial,
                        source_file_reference,
                        destination_volume_serial,
                        destination_file_reference,
                        destination_fingerprint_algorithm,
                        destination_fingerprint_hex,
                        copy_mutation_started_utc_ticks,
                        destination_committed_utc_ticks,
                        source_delete_started_utc_ticks,
                        completed_utc_ticks,
                        failure_code,
                        failure_message,
                        failure_path,
                        failure_retryable)
                    VALUES(
                        @operation_id,
                        @ordinal,
                        @entry_path,
                        @entry_name,
                        0,
                        @canonical_source,
                        @canonical_destination,
                        @state,
                        @source_volume,
                        @source_reference,
                        NULL,
                        NULL,
                        NULL,
                        NULL,
                        NULL,
                        NULL,
                        NULL,
                        @completed,
                        NULL,
                        NULL,
                        NULL,
                        NULL);
                    """;
                command.Parameters.AddWithValue("@operation_id", operationKey);
                command.Parameters.AddWithValue("@ordinal", ordinal);
                command.Parameters.AddWithValue("@entry_path", item.Entry.Path);
                command.Parameters.AddWithValue("@entry_name", item.Entry.Name);
                command.Parameters.AddWithValue("@canonical_source", item.Source.CanonicalPath);
                command.Parameters.AddWithValue("@canonical_destination", item.Destination.CanonicalPath);
                command.Parameters.AddWithValue(
                    "@state",
                    (int)(skipped
                        ? FileCrossVolumeMoveEntryState.Skipped
                        : FileCrossVolumeMoveEntryState.Pending));
                command.Parameters.AddWithValue("@source_volume", ToSqliteInteger(sourceIdentity.VolumeSerialNumber));
                command.Parameters.AddWithValue("@source_reference", ToSqliteInteger(sourceIdentity.FileReferenceNumber));
                command.Parameters.AddWithValue(
                    "@completed",
                    skipped ? ToUtcTicks(startedAt) : DBNull.Value);
                command.ExecuteNonQuery();
            }

            var history = LoadHistory(connection, transaction, operationKey)
                ?? throw new InvalidDataException(
                    "Cross-volume Move history disappeared during Begin.");
            transaction.Commit();
            return history;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException(
                $"Cross-volume Move history for operation {plan.Id} already exists or violates the composite journal schema.",
                exception);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ValueTask<FileCrossVolumeMoveActionHistory> MarkCopyMutationStartedAsync(
        Guid operationId,
        int ordinal,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default) =>
        TransitionTimestampAsync(
            operationId,
            ordinal,
            FileCrossVolumeMoveEntryState.Pending,
            FileCrossVolumeMoveEntryState.CopyMutationStarted,
            "copy_mutation_started_utc_ticks",
            NormalizeUtc(startedAtUtc),
            cancellationToken);

    public async ValueTask<FileCrossVolumeMoveActionHistory> CommitDestinationAsync(
        Guid operationId,
        int ordinal,
        FileIdentity destinationIdentity,
        FileContentFingerprint destinationContentFingerprint,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destinationContentFingerprint);
        ThrowIfDisposed();
        ValidateOrdinal(ordinal);
        var operationKey = FormatOperationId(operationId);
        var committedAt = NormalizeUtc(committedAtUtc);

        return await MutateAsync(
            operationKey,
            cancellationToken,
            (connection, transaction, current) =>
            {
                var entry = RequireEntry(current, ordinal);
                RequireState(entry, FileCrossVolumeMoveEntryState.CopyMutationStarted);
                if (destinationIdentity.VolumeSerialNumber !=
                    current.DestinationDirectoryIdentity.VolumeSerialNumber)
                {
                    throw new InvalidOperationException(
                        "Committed cross-volume Move destination identity is not bound to the durable destination root volume.");
                }

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE file_cross_volume_move_entries
                    SET state = @next_state,
                        destination_volume_serial = @destination_volume,
                        destination_file_reference = @destination_reference,
                        destination_fingerprint_algorithm = @fingerprint_algorithm,
                        destination_fingerprint_hex = @fingerprint_hex,
                        destination_committed_utc_ticks = @committed
                    WHERE operation_id = @operation_id
                      AND ordinal = @ordinal
                      AND state = @expected_state;
                    """;
                command.Parameters.AddWithValue("@next_state", (int)FileCrossVolumeMoveEntryState.DestinationCommitted);
                command.Parameters.AddWithValue("@destination_volume", ToSqliteInteger(destinationIdentity.VolumeSerialNumber));
                command.Parameters.AddWithValue("@destination_reference", ToSqliteInteger(destinationIdentity.FileReferenceNumber));
                command.Parameters.AddWithValue("@fingerprint_algorithm", (int)destinationContentFingerprint.Algorithm);
                command.Parameters.AddWithValue("@fingerprint_hex", destinationContentFingerprint.HexDigest);
                command.Parameters.AddWithValue("@committed", ToUtcTicks(committedAt));
                command.Parameters.AddWithValue("@operation_id", operationKey);
                command.Parameters.AddWithValue("@ordinal", ordinal);
                command.Parameters.AddWithValue("@expected_state", (int)FileCrossVolumeMoveEntryState.CopyMutationStarted);
                RequireOne(command.ExecuteNonQuery(), operationId, ordinal, "commit destination");
            }).ConfigureAwait(false);
    }

    public async ValueTask<FileCrossVolumeMoveActionHistory> MarkEntryFailedAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ThrowIfDisposed();
        ValidateFailure(failure);
        ValidateOrdinal(ordinal);
        var operationKey = FormatOperationId(operationId);
        var failedAt = NormalizeUtc(failedAtUtc);

        return await MutateAsync(
            operationKey,
            cancellationToken,
            (connection, transaction, current) =>
            {
                var entry = RequireEntry(current, ordinal);
                if (entry.State is not FileCrossVolumeMoveEntryState.Pending and
                    not FileCrossVolumeMoveEntryState.DestinationCommitted)
                {
                    throw new InvalidOperationException(
                        "A safe cross-volume Move failure may be recorded only before Copy starts or after a durable destination commit while the source is retained.");
                }

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE file_cross_volume_move_entries
                    SET state = @failed,
                        completed_utc_ticks = @completed,
                        failure_code = @failure_code,
                        failure_message = @failure_message,
                        failure_path = @failure_path,
                        failure_retryable = @failure_retryable
                    WHERE operation_id = @operation_id
                      AND ordinal = @ordinal
                      AND state = @expected_state;
                    """;
                BindFailure(command, failure);
                command.Parameters.AddWithValue("@failed", (int)FileCrossVolumeMoveEntryState.Failed);
                command.Parameters.AddWithValue("@completed", ToUtcTicks(failedAt));
                command.Parameters.AddWithValue("@operation_id", operationKey);
                command.Parameters.AddWithValue("@ordinal", ordinal);
                command.Parameters.AddWithValue("@expected_state", (int)entry.State);
                RequireOne(command.ExecuteNonQuery(), operationId, ordinal, "record safe failure");
            }).ConfigureAwait(false);
    }

    public ValueTask<FileCrossVolumeMoveActionHistory> MarkSourceDeleteStartedAsync(
        Guid operationId,
        int ordinal,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default) =>
        TransitionTimestampAsync(
            operationId,
            ordinal,
            FileCrossVolumeMoveEntryState.DestinationCommitted,
            FileCrossVolumeMoveEntryState.SourceDeleteStarted,
            "source_delete_started_utc_ticks",
            NormalizeUtc(startedAtUtc),
            cancellationToken);

    public async ValueTask<FileCrossVolumeMoveActionHistory> CommitSourceDeletedAsync(
        Guid operationId,
        int ordinal,
        FileIdentity deletedSourceIdentity,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateOrdinal(ordinal);
        var operationKey = FormatOperationId(operationId);
        var committedAt = NormalizeUtc(committedAtUtc);

        return await MutateAsync(
            operationKey,
            cancellationToken,
            (connection, transaction, current) =>
            {
                var entry = RequireEntry(current, ordinal);
                RequireState(entry, FileCrossVolumeMoveEntryState.SourceDeleteStarted);
                if (deletedSourceIdentity != entry.SourceIdentity)
                {
                    throw new InvalidOperationException(
                        "Cross-volume Move source-delete commit identity does not match the original durable source identity.");
                }

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE file_cross_volume_move_entries
                    SET state = @moved,
                        completed_utc_ticks = @completed,
                        failure_code = NULL,
                        failure_message = NULL,
                        failure_path = NULL,
                        failure_retryable = NULL
                    WHERE operation_id = @operation_id
                      AND ordinal = @ordinal
                      AND state = @expected_state
                      AND source_volume_serial = @source_volume
                      AND source_file_reference = @source_reference
                      AND destination_volume_serial IS NOT NULL
                      AND destination_file_reference IS NOT NULL
                      AND destination_fingerprint_algorithm IS NOT NULL
                      AND destination_fingerprint_hex IS NOT NULL;
                    """;
                command.Parameters.AddWithValue("@moved", (int)FileCrossVolumeMoveEntryState.Moved);
                command.Parameters.AddWithValue("@completed", ToUtcTicks(committedAt));
                command.Parameters.AddWithValue("@operation_id", operationKey);
                command.Parameters.AddWithValue("@ordinal", ordinal);
                command.Parameters.AddWithValue("@expected_state", (int)FileCrossVolumeMoveEntryState.SourceDeleteStarted);
                command.Parameters.AddWithValue("@source_volume", ToSqliteInteger(deletedSourceIdentity.VolumeSerialNumber));
                command.Parameters.AddWithValue("@source_reference", ToSqliteInteger(deletedSourceIdentity.FileReferenceNumber));
                RequireOne(command.ExecuteNonQuery(), operationId, ordinal, "commit source deletion");
            }).ConfigureAwait(false);
    }

    public async ValueTask<FileCrossVolumeMoveActionHistory> MarkRecoveryRequiredAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        DateTimeOffset failedAtUtc,
        FileIdentity? destinationIdentity = null,
        FileContentFingerprint? destinationContentFingerprint = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ThrowIfDisposed();
        ValidateFailure(failure);
        ValidateOrdinal(ordinal);
        if (destinationIdentity.HasValue != (destinationContentFingerprint is not null))
        {
            throw new ArgumentException(
                "Recovery destination identity and content fingerprint must be supplied together.");
        }

        var operationKey = FormatOperationId(operationId);
        var failedAt = NormalizeUtc(failedAtUtc);
        return await MutateAsync(
            operationKey,
            cancellationToken,
            (connection, transaction, current) =>
            {
                var entry = RequireEntry(current, ordinal);
                if (entry.State is not FileCrossVolumeMoveEntryState.CopyMutationStarted and
                    not FileCrossVolumeMoveEntryState.SourceDeleteStarted)
                {
                    throw new InvalidOperationException(
                        "RecoveryRequired may be recorded only after a durable Copy or source-delete mutation barrier.");
                }

                FileIdentity? persistedDestinationIdentity = entry.DestinationIdentity;
                FileContentFingerprint? persistedFingerprint = entry.DestinationContentFingerprint;
                DateTimeOffset? destinationCommittedAt = entry.DestinationCommittedAtUtc;

                if (entry.State == FileCrossVolumeMoveEntryState.CopyMutationStarted &&
                    destinationIdentity.HasValue)
                {
                    if (destinationIdentity.Value.VolumeSerialNumber !=
                        current.DestinationDirectoryIdentity.VolumeSerialNumber)
                    {
                        throw new InvalidOperationException(
                            "Recovery destination identity is not bound to the durable destination root volume.");
                    }
                    persistedDestinationIdentity = destinationIdentity;
                    persistedFingerprint = destinationContentFingerprint;
                    destinationCommittedAt = failedAt;
                }
                else if (entry.State == FileCrossVolumeMoveEntryState.SourceDeleteStarted &&
                    destinationIdentity.HasValue &&
                    (destinationIdentity != entry.DestinationIdentity ||
                     destinationContentFingerprint != entry.DestinationContentFingerprint))
                {
                    throw new InvalidOperationException(
                        "Recovery evidence cannot replace the already committed cross-volume Move destination identity/content evidence.");
                }

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE file_cross_volume_move_entries
                    SET state = @recovery,
                        completed_utc_ticks = @completed,
                        destination_volume_serial = @destination_volume,
                        destination_file_reference = @destination_reference,
                        destination_fingerprint_algorithm = @fingerprint_algorithm,
                        destination_fingerprint_hex = @fingerprint_hex,
                        destination_committed_utc_ticks = @destination_committed,
                        failure_code = @failure_code,
                        failure_message = @failure_message,
                        failure_path = @failure_path,
                        failure_retryable = @failure_retryable
                    WHERE operation_id = @operation_id
                      AND ordinal = @ordinal
                      AND state = @expected_state;
                    """;
                BindFailure(command, failure);
                command.Parameters.AddWithValue("@recovery", (int)FileCrossVolumeMoveEntryState.RecoveryRequired);
                command.Parameters.AddWithValue("@completed", ToUtcTicks(failedAt));
                command.Parameters.AddWithValue(
                    "@destination_volume",
                    persistedDestinationIdentity is FileIdentity identity
                        ? ToSqliteInteger(identity.VolumeSerialNumber)
                        : DBNull.Value);
                command.Parameters.AddWithValue(
                    "@destination_reference",
                    persistedDestinationIdentity is FileIdentity identity2
                        ? ToSqliteInteger(identity2.FileReferenceNumber)
                        : DBNull.Value);
                command.Parameters.AddWithValue(
                    "@fingerprint_algorithm",
                    persistedFingerprint is null
                        ? DBNull.Value
                        : (int)persistedFingerprint.Algorithm);
                command.Parameters.AddWithValue(
                    "@fingerprint_hex",
                    persistedFingerprint?.HexDigest ?? (object)DBNull.Value);
                command.Parameters.AddWithValue(
                    "@destination_committed",
                    destinationCommittedAt.HasValue
                        ? ToUtcTicks(destinationCommittedAt.Value)
                        : DBNull.Value);
                command.Parameters.AddWithValue("@operation_id", operationKey);
                command.Parameters.AddWithValue("@ordinal", ordinal);
                command.Parameters.AddWithValue("@expected_state", (int)entry.State);
                RequireOne(command.ExecuteNonQuery(), operationId, ordinal, "mark recovery required");
            }).ConfigureAwait(false);
    }

    public async ValueTask<FileCrossVolumeMoveActionHistory> CompleteAsync(
        Guid operationId,
        FileCrossVolumeMoveTerminalState terminalState,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(terminalState))
        {
            throw new ArgumentOutOfRangeException(nameof(terminalState));
        }

        var operationKey = FormatOperationId(operationId);
        var completedAt = NormalizeUtc(completedAtUtc);
        return await MutateAsync(
            operationKey,
            cancellationToken,
            (connection, transaction, current) =>
            {
                _ = new FileCrossVolumeMoveActionHistory(
                    current.OperationId,
                    current.QueuedAtUtc,
                    current.ValidatedAtUtc,
                    current.StartedAtUtc,
                    completedAt,
                    current.CollisionPolicy,
                    current.SourceDirectoryPath,
                    current.DestinationDirectoryPath,
                    current.CanonicalSourceDirectoryPath,
                    current.CanonicalDestinationDirectoryPath,
                    current.SourceDirectoryIdentity,
                    current.DestinationDirectoryIdentity,
                    terminalState,
                    current.Entries);

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE file_cross_volume_move_actions
                    SET terminal_state = @terminal,
                        completed_utc_ticks = @completed
                    WHERE operation_id = @operation_id
                      AND terminal_state IS NULL;
                    """;
                command.Parameters.AddWithValue("@terminal", (int)terminalState);
                command.Parameters.AddWithValue("@completed", ToUtcTicks(completedAt));
                command.Parameters.AddWithValue("@operation_id", operationKey);
                RequireOne(command.ExecuteNonQuery(), operationId, ordinal: null, "complete operation");
            }).ConfigureAwait(false);
    }

    public ValueTask<FileCrossVolumeMoveActionHistory?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = OpenConnection();
        return ValueTask.FromResult(
            LoadHistory(connection, transaction: null, FormatOperationId(operationId)));
    }

    public ValueTask<IReadOnlyList<FileCrossVolumeMoveActionHistory>> GetRecentAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (limit <= 0 || limit > MaximumRecentLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT operation_id
            FROM file_cross_volume_move_actions
            ORDER BY started_utc_ticks DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@limit", limit);
        var keys = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                keys.Add(reader.GetString(0));
            }
        }

        var histories = new List<FileCrossVolumeMoveActionHistory>(keys.Count);
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var history = LoadHistory(connection, transaction: null, key);
            if (history is not null)
            {
                histories.Add(history);
            }
        }

        return ValueTask.FromResult<IReadOnlyList<FileCrossVolumeMoveActionHistory>>(
            histories.AsReadOnly());
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

    private async ValueTask<FileCrossVolumeMoveActionHistory> TransitionTimestampAsync(
        Guid operationId,
        int ordinal,
        FileCrossVolumeMoveEntryState expected,
        FileCrossVolumeMoveEntryState next,
        string timestampColumn,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateOrdinal(ordinal);
        if (timestampColumn is not "copy_mutation_started_utc_ticks" and
            not "source_delete_started_utc_ticks")
        {
            throw new ArgumentOutOfRangeException(nameof(timestampColumn));
        }

        var operationKey = FormatOperationId(operationId);
        return await MutateAsync(
            operationKey,
            cancellationToken,
            (connection, transaction, current) =>
            {
                var entry = RequireEntry(current, ordinal);
                RequireState(entry, expected);

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"""
                    UPDATE file_cross_volume_move_entries
                    SET state = @next_state,
                        {timestampColumn} = @timestamp
                    WHERE operation_id = @operation_id
                      AND ordinal = @ordinal
                      AND state = @expected_state;
                    """;
                command.Parameters.AddWithValue("@next_state", (int)next);
                command.Parameters.AddWithValue("@timestamp", ToUtcTicks(timestamp));
                command.Parameters.AddWithValue("@operation_id", operationKey);
                command.Parameters.AddWithValue("@ordinal", ordinal);
                command.Parameters.AddWithValue("@expected_state", (int)expected);
                RequireOne(command.ExecuteNonQuery(), operationId, ordinal, $"transition {expected} -> {next}");
            }).ConfigureAwait(false);
    }

    private async ValueTask<FileCrossVolumeMoveActionHistory> MutateAsync(
        string operationKey,
        CancellationToken cancellationToken,
        Action<SqliteConnection, SqliteTransaction, FileCrossVolumeMoveActionHistory> mutation)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var current = LoadHistory(connection, transaction, operationKey)
                ?? throw new KeyNotFoundException(
                    $"Cross-volume Move history {operationKey} was not found.");
            if (current.TerminalState is not null)
            {
                throw new InvalidOperationException(
                    $"Cross-volume Move history {current.OperationId} is already terminal.");
            }

            mutation(connection, transaction, current);
            var result = LoadHistory(connection, transaction, operationKey)
                ?? throw new InvalidDataException(
                    "Cross-volume Move history disappeared during a durable transition.");
            transaction.Commit();
            return result;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static void ValidateBegin(FileOperationExecutionValidationResult validation)
    {
        var plan = validation.Plan;
        if (plan.Kind != FileOperationKind.Move ||
            !validation.CanBeginMutation ||
            validation.Items.Count == 0 ||
            validation.Items.Count != plan.Intent.Entries.Count ||
            validation.SourceDirectory.State != FileOperationCanonicalPathState.Directory ||
            validation.DestinationDirectory.State != FileOperationCanonicalPathState.Directory ||
            validation.SourceDirectory.IsLeafReparsePoint ||
            validation.DestinationDirectory.IsLeafReparsePoint ||
            validation.SourceDirectory.Identity is not FileIdentity sourceRoot ||
            validation.DestinationDirectory.Identity is not FileIdentity destinationRoot ||
            sourceRoot.VolumeSerialNumber == destinationRoot.VolumeSerialNumber)
        {
            throw new ArgumentException(
                "Cross-volume Move history can begin only from execution-grade validation of distinct-volume directory roots.",
                nameof(validation));
        }

        for (var ordinal = 0; ordinal < validation.Items.Count; ordinal++)
        {
            var item = validation.Items[ordinal];
            if (item.Entry != plan.Intent.Entries[ordinal] ||
                item.Entry.IsDirectory ||
                item.Source.State != FileOperationCanonicalPathState.File ||
                item.Source.IsLeafReparsePoint ||
                item.Source.Identity is not FileIdentity sourceIdentity ||
                sourceIdentity.VolumeSerialNumber != sourceRoot.VolumeSerialNumber ||
                item.Decision is not FileOperationExecutionValidationDecision.Ready and
                    not FileOperationExecutionValidationDecision.Skip)
            {
                throw new ArgumentException(
                    "Cross-volume Move history requires ordered regular-file Ready/Skip validation bound to the source volume.",
                    nameof(validation));
            }

            if (item.Decision == FileOperationExecutionValidationDecision.Ready)
            {
                if (item.Destination.State != FileOperationCanonicalPathState.Missing ||
                    item.Destination.Identity.HasValue)
                {
                    throw new ArgumentException(
                        "A Ready cross-volume Move destination must be missing and identity-free.",
                        nameof(validation));
                }
            }
            else if (plan.CollisionPolicy != FileOperationCollisionPolicy.Skip ||
                item.Destination.State is not FileOperationCanonicalPathState.File and
                    not FileOperationCanonicalPathState.Directory ||
                item.Destination.Identity is not FileIdentity destinationIdentity ||
                destinationIdentity.VolumeSerialNumber != destinationRoot.VolumeSerialNumber)
            {
                throw new ArgumentException(
                    "A skipped cross-volume Move entry requires explicit Skip policy and existing destination identity on the destination volume.",
                    nameof(validation));
            }
        }
    }

    private FileCrossVolumeMoveActionHistory? LoadHistory(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string operationKey)
    {
        using var operation = connection.CreateCommand();
        operation.Transaction = transaction;
        operation.CommandText = """
            SELECT
                queued_utc_ticks,
                validated_utc_ticks,
                started_utc_ticks,
                completed_utc_ticks,
                collision_policy,
                source_directory_path,
                destination_directory_path,
                canonical_source_directory_path,
                canonical_destination_directory_path,
                source_root_volume_serial,
                source_root_file_reference,
                destination_root_volume_serial,
                destination_root_file_reference,
                terminal_state
            FROM file_cross_volume_move_actions
            WHERE operation_id = @operation_id;
            """;
        operation.Parameters.AddWithValue("@operation_id", operationKey);

        long queuedTicks;
        long validatedTicks;
        long startedTicks;
        long? completedTicks;
        FileOperationCollisionPolicy collisionPolicy;
        string sourcePath;
        string destinationPath;
        string canonicalSource;
        string canonicalDestination;
        FileIdentity sourceRoot;
        FileIdentity destinationRoot;
        FileCrossVolumeMoveTerminalState? terminalState;
        using (var reader = operation.ExecuteReader())
        {
            if (!reader.Read())
            {
                return null;
            }

            queuedTicks = reader.GetInt64(0);
            validatedTicks = reader.GetInt64(1);
            startedTicks = reader.GetInt64(2);
            completedTicks = reader.IsDBNull(3) ? null : reader.GetInt64(3);
            collisionPolicy = (FileOperationCollisionPolicy)reader.GetInt32(4);
            sourcePath = reader.GetString(5);
            destinationPath = reader.GetString(6);
            canonicalSource = reader.GetString(7);
            canonicalDestination = reader.GetString(8);
            sourceRoot = new FileIdentity(
                FromSqliteInteger(reader.GetInt64(9)),
                FromSqliteInteger(reader.GetInt64(10)));
            destinationRoot = new FileIdentity(
                FromSqliteInteger(reader.GetInt64(11)),
                FromSqliteInteger(reader.GetInt64(12)));
            terminalState = reader.IsDBNull(13)
                ? null
                : (FileCrossVolumeMoveTerminalState)reader.GetInt32(13);
        }

        var entries = new List<FileCrossVolumeMoveActionEntry>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT
                    ordinal,
                    entry_path,
                    entry_name,
                    is_directory,
                    canonical_source_path,
                    canonical_destination_path,
                    state,
                    source_volume_serial,
                    source_file_reference,
                    destination_volume_serial,
                    destination_file_reference,
                    destination_fingerprint_algorithm,
                    destination_fingerprint_hex,
                    copy_mutation_started_utc_ticks,
                    destination_committed_utc_ticks,
                    source_delete_started_utc_ticks,
                    completed_utc_ticks,
                    failure_code,
                    failure_message,
                    failure_path,
                    failure_retryable
                FROM file_cross_volume_move_entries
                WHERE operation_id = @operation_id
                ORDER BY ordinal;
                """;
            command.Parameters.AddWithValue("@operation_id", operationKey);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var destinationIdentity = reader.IsDBNull(9)
                    ? (FileIdentity?)null
                    : new FileIdentity(
                        FromSqliteInteger(reader.GetInt64(9)),
                        FromSqliteInteger(reader.GetInt64(10)));
                FileContentFingerprint? fingerprint = null;
                if (!reader.IsDBNull(11) && !reader.IsDBNull(12))
                {
                    fingerprint = new FileContentFingerprint(
                        (FileContentFingerprintAlgorithm)reader.GetInt32(11),
                        reader.GetString(12));
                }

                FileOperationFailure? failure = null;
                if (!reader.IsDBNull(17))
                {
                    failure = new FileOperationFailure(
                        reader.GetString(17),
                        reader.GetString(18),
                        reader.IsDBNull(19) ? null : reader.GetString(19),
                        !reader.IsDBNull(20) && reader.GetInt32(20) != 0);
                }

                entries.Add(new FileCrossVolumeMoveActionEntry(
                    reader.GetInt32(0),
                    new FileOperationEntry(
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetInt32(3) != 0),
                    reader.GetString(4),
                    reader.GetString(5),
                    (FileCrossVolumeMoveEntryState)reader.GetInt32(6),
                    new FileIdentity(
                        FromSqliteInteger(reader.GetInt64(7)),
                        FromSqliteInteger(reader.GetInt64(8))),
                    destinationIdentity,
                    fingerprint,
                    FromNullableTicks(reader, 13),
                    FromNullableTicks(reader, 14),
                    FromNullableTicks(reader, 15),
                    FromNullableTicks(reader, 16),
                    failure));
            }
        }

        return new FileCrossVolumeMoveActionHistory(
            Guid.ParseExact(operationKey, "N"),
            FromUtcTicks(queuedTicks),
            FromUtcTicks(validatedTicks),
            FromUtcTicks(startedTicks),
            completedTicks.HasValue ? FromUtcTicks(completedTicks.Value) : null,
            collisionPolicy,
            sourcePath,
            destinationPath,
            canonicalSource,
            canonicalDestination,
            sourceRoot,
            destinationRoot,
            terminalState,
            entries);
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS file_cross_volume_move_schema(
                singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1),
                version INTEGER NOT NULL);

            INSERT OR IGNORE INTO file_cross_volume_move_schema(singleton, version)
            VALUES(1, 1);

            CREATE TABLE IF NOT EXISTS file_cross_volume_move_actions(
                operation_id TEXT NOT NULL PRIMARY KEY,
                queued_utc_ticks INTEGER NOT NULL,
                validated_utc_ticks INTEGER NOT NULL,
                started_utc_ticks INTEGER NOT NULL,
                completed_utc_ticks INTEGER NULL,
                collision_policy INTEGER NOT NULL CHECK(collision_policy BETWEEN 0 AND 2),
                source_directory_path TEXT NOT NULL,
                destination_directory_path TEXT NOT NULL,
                canonical_source_directory_path TEXT NOT NULL,
                canonical_destination_directory_path TEXT NOT NULL,
                source_root_volume_serial INTEGER NOT NULL,
                source_root_file_reference INTEGER NOT NULL,
                destination_root_volume_serial INTEGER NOT NULL,
                destination_root_file_reference INTEGER NOT NULL,
                terminal_state INTEGER NULL CHECK(terminal_state BETWEEN 0 AND 3),
                CHECK(source_root_volume_serial <> destination_root_volume_serial),
                CHECK((terminal_state IS NULL AND completed_utc_ticks IS NULL) OR
                      (terminal_state IS NOT NULL AND completed_utc_ticks IS NOT NULL))
            );

            CREATE TABLE IF NOT EXISTS file_cross_volume_move_entries(
                operation_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
                entry_path TEXT NOT NULL,
                entry_name TEXT NOT NULL,
                is_directory INTEGER NOT NULL CHECK(is_directory = 0),
                canonical_source_path TEXT NOT NULL,
                canonical_destination_path TEXT NOT NULL,
                state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 7),
                source_volume_serial INTEGER NOT NULL,
                source_file_reference INTEGER NOT NULL,
                destination_volume_serial INTEGER NULL,
                destination_file_reference INTEGER NULL,
                destination_fingerprint_algorithm INTEGER NULL,
                destination_fingerprint_hex TEXT NULL,
                copy_mutation_started_utc_ticks INTEGER NULL,
                destination_committed_utc_ticks INTEGER NULL,
                source_delete_started_utc_ticks INTEGER NULL,
                completed_utc_ticks INTEGER NULL,
                failure_code TEXT NULL,
                failure_message TEXT NULL,
                failure_path TEXT NULL,
                failure_retryable INTEGER NULL CHECK(failure_retryable IN (0, 1)),
                PRIMARY KEY(operation_id, ordinal),
                FOREIGN KEY(operation_id)
                    REFERENCES file_cross_volume_move_actions(operation_id)
                    ON DELETE CASCADE,
                CHECK((destination_volume_serial IS NULL) = (destination_file_reference IS NULL)),
                CHECK((destination_volume_serial IS NULL) = (destination_fingerprint_algorithm IS NULL)),
                CHECK((destination_volume_serial IS NULL) = (destination_fingerprint_hex IS NULL))
            );

            CREATE INDEX IF NOT EXISTS idx_file_cross_volume_move_actions_started
            ON file_cross_volume_move_actions(started_utc_ticks DESC);
            """;
        command.ExecuteNonQuery();

        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText =
            "SELECT version FROM file_cross_volume_move_schema WHERE singleton = 1;";
        var value = versionCommand.ExecuteScalar();
        if (value is not long version || version != SchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported cross-volume Move journal schema version '{Convert.ToString(value, CultureInfo.InvariantCulture)}'.");
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static FileCrossVolumeMoveActionEntry RequireEntry(
        FileCrossVolumeMoveActionHistory history,
        int ordinal)
    {
        if (ordinal < 0 || ordinal >= history.Entries.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }
        return history.Entries[ordinal];
    }

    private static void RequireState(
        FileCrossVolumeMoveActionEntry entry,
        FileCrossVolumeMoveEntryState expected)
    {
        if (entry.State != expected)
        {
            throw new InvalidOperationException(
                $"Cross-volume Move entry {entry.Ordinal} is {entry.State}; expected {expected}.");
        }
    }

    private static void BindFailure(SqliteCommand command, FileOperationFailure failure)
    {
        command.Parameters.AddWithValue("@failure_code", failure.Code);
        command.Parameters.AddWithValue("@failure_message", failure.Message);
        command.Parameters.AddWithValue("@failure_path", failure.Path ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@failure_retryable", failure.Retryable ? 1 : 0);
    }

    private static void ValidateFailure(FileOperationFailure failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failure.Code);
        ArgumentException.ThrowIfNullOrWhiteSpace(failure.Message);
    }

    private static void ValidateOrdinal(int ordinal)
    {
        if (ordinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }
    }

    private static void RequireOne(
        int affected,
        Guid operationId,
        int? ordinal,
        string action)
    {
        if (affected != 1)
        {
            throw new InvalidOperationException(
                ordinal.HasValue
                    ? $"Cross-volume Move history could not {action} for operation {operationId}, entry {ordinal.Value}; the durable state changed."
                    : $"Cross-volume Move history could not {action} for operation {operationId}; the durable state changed.");
        }
    }

    private static string FormatOperationId(Guid operationId) =>
        operationId.ToString("N", CultureInfo.InvariantCulture);

    private static long ToUtcTicks(DateTimeOffset value) =>
        NormalizeUtc(value).UtcTicks;

    private static DateTimeOffset NormalizeUtc(DateTimeOffset value) =>
        value.ToUniversalTime();

    private static DateTimeOffset FromUtcTicks(long ticks) =>
        new(ticks, TimeSpan.Zero);

    private static DateTimeOffset? FromNullableTicks(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : FromUtcTicks(reader.GetInt64(ordinal));

    private static long ToSqliteInteger(ulong value) => unchecked((long)value);

    private static ulong FromSqliteInteger(long value) => unchecked((ulong)value);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
