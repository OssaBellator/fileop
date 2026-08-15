using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using Microsoft.Data.Sqlite;

namespace FileOp.Core.Operations;

/// <summary>
/// Separate durable journal for identity-preserving same-volume directory rename.
/// It intentionally does not share FileOperationActionHistory schema v1.
/// </summary>
public sealed class SqliteDirectorySameVolumeMoveActionHistoryStore :
    IDirectorySameVolumeMoveActionHistoryStore,
    IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _disposed;

    public SqliteDirectorySameVolumeMoveActionHistoryStore(string databasePath)
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

    public async ValueTask<DirectorySameVolumeMoveActionHistory> BeginAsync(
        FileOperationExecutionValidationResult validation,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validation);
        ThrowIfDisposed();
        var classification = DirectorySameVolumeMoveExecutionStrategyClassifier.Classify(validation);
        if (classification.Strategy is not DirectorySameVolumeMoveExecutionStrategy.SameVolumeDirectoryRenameRequired and
            not DirectorySameVolumeMoveExecutionStrategy.SkipOnly)
        {
            throw new InvalidOperationException(
                "Durable directory Move history can begin only for a validated same-volume directory rename or skip-only plan.");
        }

        var sourceRootIdentity = RequireIdentity(validation.SourceDirectory, "source root");
        var destinationRootIdentity = RequireIdentity(validation.DestinationDirectory, "destination root");
        var plan = validation.Plan;
        var operationId = plan.Id.ToString("D", CultureInfo.InvariantCulture);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using (var operation = connection.CreateCommand())
            {
                operation.Transaction = transaction;
                operation.CommandText = """
                    INSERT INTO directory_same_volume_move_operations(
                        operation_id, queued_utc_ticks, validated_utc_ticks, started_utc_ticks,
                        completed_utc_ticks, collision_policy,
                        source_directory_path, destination_directory_path,
                        canonical_source_directory_path, canonical_destination_directory_path,
                        source_root_volume_serial, source_root_file_reference,
                        destination_root_volume_serial, destination_root_file_reference,
                        terminal_state)
                    VALUES(
                        @operation_id, @queued, @validated, @started,
                        NULL, @collision,
                        @source_path, @destination_path,
                        @canonical_source, @canonical_destination,
                        @source_serial, @source_ref,
                        @destination_serial, @destination_ref,
                        NULL);
                    """;
                operation.Parameters.AddWithValue("@operation_id", operationId);
                operation.Parameters.AddWithValue("@queued", ToUtcTicks(plan.QueuedAtUtc));
                operation.Parameters.AddWithValue("@validated", ToUtcTicks(validation.ValidatedAtUtc));
                operation.Parameters.AddWithValue("@started", ToUtcTicks(startedAtUtc));
                operation.Parameters.AddWithValue("@collision", (int)plan.CollisionPolicy);
                operation.Parameters.AddWithValue("@source_path", plan.Intent.SourceDirectoryPath);
                operation.Parameters.AddWithValue("@destination_path", plan.Intent.DestinationDirectoryPath);
                operation.Parameters.AddWithValue("@canonical_source", validation.SourceDirectory.CanonicalPath);
                operation.Parameters.AddWithValue("@canonical_destination", validation.DestinationDirectory.CanonicalPath);
                operation.Parameters.AddWithValue("@source_serial", ToSqlInteger(sourceRootIdentity.VolumeSerialNumber));
                operation.Parameters.AddWithValue("@source_ref", ToSqlInteger(sourceRootIdentity.FileReferenceNumber));
                operation.Parameters.AddWithValue("@destination_serial", ToSqlInteger(destinationRootIdentity.VolumeSerialNumber));
                operation.Parameters.AddWithValue("@destination_ref", ToSqlInteger(destinationRootIdentity.FileReferenceNumber));
                await operation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            for (var ordinal = 0; ordinal < validation.Items.Count; ordinal++)
            {
                var item = validation.Items[ordinal];
                var sourceIdentity = item.Source.Identity
                    ?? throw new InvalidOperationException("Validated directory Move source has no stable identity.");
                var skipped = item.Decision == FileOperationExecutionValidationDecision.Skip;
                using var entry = connection.CreateCommand();
                entry.Transaction = transaction;
                entry.CommandText = """
                    INSERT INTO directory_same_volume_move_entries(
                        operation_id, ordinal, entry_path, entry_name,
                        canonical_source_path, canonical_destination_path,
                        source_volume_serial, source_file_reference,
                        destination_volume_serial, destination_file_reference,
                        state, mutation_started_utc_ticks, completed_utc_ticks,
                        failure_code, failure_message, failure_path, failure_retryable)
                    VALUES(
                        @operation_id, @ordinal, @entry_path, @entry_name,
                        @canonical_source, @canonical_destination,
                        @source_serial, @source_ref,
                        @destination_serial, @destination_ref,
                        @state, NULL, @completed,
                        NULL, NULL, NULL, NULL);
                    """;
                entry.Parameters.AddWithValue("@operation_id", operationId);
                entry.Parameters.AddWithValue("@ordinal", ordinal);
                entry.Parameters.AddWithValue("@entry_path", item.Entry.Path);
                entry.Parameters.AddWithValue("@entry_name", item.Entry.Name);
                entry.Parameters.AddWithValue("@canonical_source", item.Source.CanonicalPath);
                entry.Parameters.AddWithValue("@canonical_destination", item.Destination.CanonicalPath);
                entry.Parameters.AddWithValue("@source_serial", ToSqlInteger(sourceIdentity.VolumeSerialNumber));
                entry.Parameters.AddWithValue("@source_ref", ToSqlInteger(sourceIdentity.FileReferenceNumber));
                if (skipped && item.Destination.Identity is FileIdentity skippedDestinationIdentity)
                {
                    entry.Parameters.AddWithValue("@destination_serial", ToSqlInteger(skippedDestinationIdentity.VolumeSerialNumber));
                    entry.Parameters.AddWithValue("@destination_ref", ToSqlInteger(skippedDestinationIdentity.FileReferenceNumber));
                }
                else
                {
                    entry.Parameters.AddWithValue("@destination_serial", DBNull.Value);
                    entry.Parameters.AddWithValue("@destination_ref", DBNull.Value);
                }

                entry.Parameters.AddWithValue(
                    "@state",
                    (int)(skipped
                        ? DirectorySameVolumeMoveActionEntryState.Skipped
                        : DirectorySameVolumeMoveActionEntryState.Pending));
                entry.Parameters.AddWithValue("@completed", skipped ? ToUtcTicks(startedAtUtc) : DBNull.Value);
                await entry.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            transaction.Commit();
        }
        finally
        {
            _writeGate.Release();
        }

        return await GetRequiredAsync(plan.Id, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<DirectorySameVolumeMoveActionHistory> MarkMutationStartedAsync(
        Guid operationId,
        int ordinal,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default) =>
        TransitionEntryAsync(
            operationId,
            ordinal,
            DirectorySameVolumeMoveActionEntryState.Pending,
            DirectorySameVolumeMoveActionEntryState.MutationStarted,
            startedAtUtc,
            completedAtUtc: null,
            failure: null,
            destinationIdentity: null,
            cancellationToken);

    public ValueTask<DirectorySameVolumeMoveActionHistory> MarkEntryFailedBeforeMutationAsync(
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
            DirectorySameVolumeMoveActionEntryState.Pending,
            DirectorySameVolumeMoveActionEntryState.Failed,
            mutationStartedAtUtc: null,
            completedAtUtc: failedAtUtc,
            failure,
            destinationIdentity: null,
            cancellationToken);
    }

    public ValueTask<DirectorySameVolumeMoveActionHistory> CommitAsync(
        Guid operationId,
        int ordinal,
        FileIdentity destinationIdentity,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default) =>
        TransitionEntryAsync(
            operationId,
            ordinal,
            DirectorySameVolumeMoveActionEntryState.MutationStarted,
            DirectorySameVolumeMoveActionEntryState.Committed,
            mutationStartedAtUtc: null,
            completedAtUtc: committedAtUtc,
            failure: null,
            destinationIdentity,
            cancellationToken);

    public ValueTask<DirectorySameVolumeMoveActionHistory> MarkRecoveryRequiredAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        DateTimeOffset failedAtUtc,
        FileIdentity? observedDestinationIdentity = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return TransitionEntryAsync(
            operationId,
            ordinal,
            DirectorySameVolumeMoveActionEntryState.MutationStarted,
            DirectorySameVolumeMoveActionEntryState.RecoveryRequired,
            mutationStartedAtUtc: null,
            completedAtUtc: failedAtUtc,
            failure,
            observedDestinationIdentity,
            cancellationToken);
    }

    public async ValueTask<DirectorySameVolumeMoveActionHistory> CompleteAsync(
        Guid operationId,
        DirectorySameVolumeMoveActionTerminalState terminalState,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            ValidateTerminalState(connection, transaction, operationId, terminalState);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE directory_same_volume_move_operations
                SET terminal_state = @terminal, completed_utc_ticks = @completed
                WHERE operation_id = @operation_id AND terminal_state IS NULL;
                """;
            command.Parameters.AddWithValue("@terminal", (int)terminalState);
            command.Parameters.AddWithValue("@completed", ToUtcTicks(completedAtUtc));
            command.Parameters.AddWithValue("@operation_id", operationId.ToString("D", CultureInfo.InvariantCulture));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("Directory Move history is missing or already terminal.");
            }

            transaction.Commit();
        }
        finally
        {
            _writeGate.Release();
        }

        return await GetRequiredAsync(operationId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DirectorySameVolumeMoveActionHistory?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var connection = OpenConnection();
        return await ReadHistoryAsync(connection, operationId, cancellationToken).ConfigureAwait(false);
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

    private async ValueTask<DirectorySameVolumeMoveActionHistory> TransitionEntryAsync(
        Guid operationId,
        int ordinal,
        DirectorySameVolumeMoveActionEntryState expectedState,
        DirectorySameVolumeMoveActionEntryState nextState,
        DateTimeOffset? mutationStartedAtUtc,
        DateTimeOffset? completedAtUtc,
        FileOperationFailure? failure,
        FileIdentity? destinationIdentity,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE directory_same_volume_move_entries
                SET state = @next_state,
                    mutation_started_utc_ticks = COALESCE(@mutation_started, mutation_started_utc_ticks),
                    completed_utc_ticks = @completed,
                    destination_volume_serial = COALESCE(@destination_serial, destination_volume_serial),
                    destination_file_reference = COALESCE(@destination_ref, destination_file_reference),
                    failure_code = @failure_code,
                    failure_message = @failure_message,
                    failure_path = @failure_path,
                    failure_retryable = @failure_retryable
                WHERE operation_id = @operation_id
                  AND ordinal = @ordinal
                  AND state = @expected_state;
                """;
            command.Parameters.AddWithValue("@next_state", (int)nextState);
            command.Parameters.AddWithValue("@mutation_started", mutationStartedAtUtc is { } mutationAt ? ToUtcTicks(mutationAt) : DBNull.Value);
            command.Parameters.AddWithValue("@completed", completedAtUtc is { } completedAt ? ToUtcTicks(completedAt) : DBNull.Value);
            command.Parameters.AddWithValue("@destination_serial", destinationIdentity is { } destination ? ToSqlInteger(destination.VolumeSerialNumber) : DBNull.Value);
            command.Parameters.AddWithValue("@destination_ref", destinationIdentity is { } destinationRef ? ToSqlInteger(destinationRef.FileReferenceNumber) : DBNull.Value);
            command.Parameters.AddWithValue("@failure_code", failure is null ? DBNull.Value : failure.Code);
            command.Parameters.AddWithValue("@failure_message", failure is null ? DBNull.Value : failure.Message);
            command.Parameters.AddWithValue("@failure_path", failure?.Path is { } failurePath ? failurePath : DBNull.Value);
            command.Parameters.AddWithValue("@failure_retryable", failure is null ? DBNull.Value : failure.Retryable ? 1 : 0);
            command.Parameters.AddWithValue("@operation_id", operationId.ToString("D", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@ordinal", ordinal);
            command.Parameters.AddWithValue("@expected_state", (int)expectedState);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Directory Move entry {ordinal} is missing or is not in required state {expectedState}.");
            }

            transaction.Commit();
        }
        finally
        {
            _writeGate.Release();
        }

        return await GetRequiredAsync(operationId, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateTerminalState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid operationId,
        DirectorySameVolumeMoveActionTerminalState terminalState)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT state, COUNT(*)
            FROM directory_same_volume_move_entries
            WHERE operation_id = @operation_id
            GROUP BY state;
            """;
        command.Parameters.AddWithValue("@operation_id", operationId.ToString("D", CultureInfo.InvariantCulture));
        var counts = new Dictionary<DirectorySameVolumeMoveActionEntryState, long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            counts[(DirectorySameVolumeMoveActionEntryState)reader.GetInt32(0)] = reader.GetInt64(1);
        }

        if (counts.Count == 0)
        {
            throw new InvalidOperationException("Directory Move history has no entries.");
        }

        var mutationSensitive = Count(counts, DirectorySameVolumeMoveActionEntryState.MutationStarted) +
            Count(counts, DirectorySameVolumeMoveActionEntryState.RecoveryRequired);
        if (terminalState == DirectorySameVolumeMoveActionTerminalState.Succeeded &&
            counts.Keys.Any(static state =>
                state is not DirectorySameVolumeMoveActionEntryState.Committed and
                    not DirectorySameVolumeMoveActionEntryState.Skipped))
        {
            throw new InvalidOperationException("Directory Move cannot succeed until every entry is committed or skipped.");
        }

        if (terminalState is DirectorySameVolumeMoveActionTerminalState.Failed or
            DirectorySameVolumeMoveActionTerminalState.Cancelled && mutationSensitive != 0)
        {
            throw new InvalidOperationException(
                "A mutation-sensitive directory Move cannot settle as Failed/Cancelled; recovery is required.");
        }

        if (terminalState == DirectorySameVolumeMoveActionTerminalState.RecoveryRequired && mutationSensitive == 0)
        {
            throw new InvalidOperationException(
                "Directory Move cannot be marked RecoveryRequired without a mutation-sensitive entry.");
        }
    }

    private static long Count(
        IReadOnlyDictionary<DirectorySameVolumeMoveActionEntryState, long> counts,
        DirectorySameVolumeMoveActionEntryState state) =>
        counts.TryGetValue(state, out var value) ? value : 0;

    private async ValueTask<DirectorySameVolumeMoveActionHistory> GetRequiredAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var history = await GetAsync(operationId, cancellationToken).ConfigureAwait(false);
        return history ?? throw new InvalidOperationException("Directory Move history was not found after a durable transition.");
    }

    private static async ValueTask<DirectorySameVolumeMoveActionHistory?> ReadHistoryAsync(
        SqliteConnection connection,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        using var operation = connection.CreateCommand();
        operation.CommandText = """
            SELECT queued_utc_ticks, validated_utc_ticks, started_utc_ticks, completed_utc_ticks,
                   collision_policy, source_directory_path, destination_directory_path,
                   canonical_source_directory_path, canonical_destination_directory_path,
                   source_root_volume_serial, source_root_file_reference,
                   destination_root_volume_serial, destination_root_file_reference,
                   terminal_state
            FROM directory_same_volume_move_operations
            WHERE operation_id = @operation_id;
            """;
        operation.Parameters.AddWithValue("@operation_id", operationId.ToString("D", CultureInfo.InvariantCulture));
        using var operationReader = await operation.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await operationReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var queuedAt = FromUtcTicks(operationReader.GetInt64(0));
        var validatedAt = FromUtcTicks(operationReader.GetInt64(1));
        var startedAt = FromUtcTicks(operationReader.GetInt64(2));
        var completedAt = operationReader.IsDBNull(3) ? (DateTimeOffset?)null : FromUtcTicks(operationReader.GetInt64(3));
        var collisionPolicy = (FileOperationCollisionPolicy)operationReader.GetInt32(4);
        var sourcePath = operationReader.GetString(5);
        var destinationPath = operationReader.GetString(6);
        var canonicalSource = operationReader.GetString(7);
        var canonicalDestination = operationReader.GetString(8);
        var sourceRootIdentity = new FileIdentity(
            FromSqlUInt32(operationReader.GetInt64(9)),
            FromSqlUInt64(operationReader.GetInt64(10)));
        var destinationRootIdentity = new FileIdentity(
            FromSqlUInt32(operationReader.GetInt64(11)),
            FromSqlUInt64(operationReader.GetInt64(12)));
        var terminalState = operationReader.IsDBNull(13)
            ? (DirectorySameVolumeMoveActionTerminalState?)null
            : (DirectorySameVolumeMoveActionTerminalState)operationReader.GetInt32(13);
        await operationReader.DisposeAsync().ConfigureAwait(false);

        using var entriesCommand = connection.CreateCommand();
        entriesCommand.CommandText = """
            SELECT ordinal, entry_path, entry_name, canonical_source_path, canonical_destination_path,
                   source_volume_serial, source_file_reference,
                   destination_volume_serial, destination_file_reference,
                   state, mutation_started_utc_ticks, completed_utc_ticks,
                   failure_code, failure_message, failure_path, failure_retryable
            FROM directory_same_volume_move_entries
            WHERE operation_id = @operation_id
            ORDER BY ordinal;
            """;
        entriesCommand.Parameters.AddWithValue("@operation_id", operationId.ToString("D", CultureInfo.InvariantCulture));
        var entries = new List<DirectorySameVolumeMoveActionEntry>();
        using var entryReader = await entriesCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await entryReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var destinationIdentity = entryReader.IsDBNull(7)
                ? (FileIdentity?)null
                : new FileIdentity(
                    FromSqlUInt32(entryReader.GetInt64(7)),
                    FromSqlUInt64(entryReader.GetInt64(8)));
            FileOperationFailure? failure = null;
            if (!entryReader.IsDBNull(12))
            {
                failure = new FileOperationFailure(
                    entryReader.GetString(12),
                    entryReader.GetString(13),
                    entryReader.IsDBNull(14) ? null : entryReader.GetString(14),
                    entryReader.GetInt32(15) != 0);
            }

            entries.Add(new DirectorySameVolumeMoveActionEntry(
                entryReader.GetInt32(0),
                new FileOperationEntry(entryReader.GetString(1), entryReader.GetString(2), IsDirectory: true),
                entryReader.GetString(3),
                entryReader.GetString(4),
                new FileIdentity(
                    FromSqlUInt32(entryReader.GetInt64(5)),
                    FromSqlUInt64(entryReader.GetInt64(6))),
                destinationIdentity,
                (DirectorySameVolumeMoveActionEntryState)entryReader.GetInt32(9),
                entryReader.IsDBNull(10) ? null : FromUtcTicks(entryReader.GetInt64(10)),
                entryReader.IsDBNull(11) ? null : FromUtcTicks(entryReader.GetInt64(11)),
                failure));
        }

        return new DirectorySameVolumeMoveActionHistory(
            operationId,
            queuedAt,
            validatedAt,
            startedAt,
            completedAt,
            collisionPolicy,
            sourcePath,
            destinationPath,
            canonicalSource,
            canonicalDestination,
            sourceRootIdentity,
            destinationRootIdentity,
            terminalState,
            entries);
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS directory_same_volume_move_operations(
                operation_id TEXT PRIMARY KEY,
                queued_utc_ticks INTEGER NOT NULL,
                validated_utc_ticks INTEGER NOT NULL,
                started_utc_ticks INTEGER NOT NULL,
                completed_utc_ticks INTEGER NULL,
                collision_policy INTEGER NOT NULL,
                source_directory_path TEXT NOT NULL,
                destination_directory_path TEXT NOT NULL,
                canonical_source_directory_path TEXT NOT NULL,
                canonical_destination_directory_path TEXT NOT NULL,
                source_root_volume_serial INTEGER NOT NULL,
                source_root_file_reference INTEGER NOT NULL,
                destination_root_volume_serial INTEGER NOT NULL,
                destination_root_file_reference INTEGER NOT NULL,
                terminal_state INTEGER NULL
            );

            CREATE TABLE IF NOT EXISTS directory_same_volume_move_entries(
                operation_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                entry_path TEXT NOT NULL,
                entry_name TEXT NOT NULL,
                canonical_source_path TEXT NOT NULL,
                canonical_destination_path TEXT NOT NULL,
                source_volume_serial INTEGER NOT NULL,
                source_file_reference INTEGER NOT NULL,
                destination_volume_serial INTEGER NULL,
                destination_file_reference INTEGER NULL,
                state INTEGER NOT NULL,
                mutation_started_utc_ticks INTEGER NULL,
                completed_utc_ticks INTEGER NULL,
                failure_code TEXT NULL,
                failure_message TEXT NULL,
                failure_path TEXT NULL,
                failure_retryable INTEGER NULL,
                PRIMARY KEY(operation_id, ordinal),
                FOREIGN KEY(operation_id) REFERENCES directory_same_volume_move_operations(operation_id)
            );
            """;
        command.ExecuteNonQuery();
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

    private static FileIdentity RequireIdentity(FileOperationCanonicalPath path, string description) =>
        path.Identity ?? throw new InvalidOperationException($"Validated directory Move {description} has no stable identity.");

    private static long ToUtcTicks(DateTimeOffset value) => value.ToUniversalTime().UtcDateTime.Ticks;

    private static DateTimeOffset FromUtcTicks(long ticks) => new(new DateTime(ticks, DateTimeKind.Utc));

    private static long ToSqlInteger(uint value) => unchecked((long)value);

    private static long ToSqlInteger(ulong value) => unchecked((long)value);

    private static uint FromSqlUInt32(long value) => unchecked((uint)value);

    private static ulong FromSqlUInt64(long value) => unchecked((ulong)value);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
