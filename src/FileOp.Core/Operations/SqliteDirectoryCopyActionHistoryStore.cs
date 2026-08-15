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
/// Separate durable journal for recursive directory Copy. It never reuses regular-file Copy
/// schema v1 and never grants replay, rollback or cleanup authority.
/// </summary>
public sealed class SqliteDirectoryCopyActionHistoryStore : IDirectoryCopyActionHistoryStore, IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _disposed;

    public SqliteDirectoryCopyActionHistoryStore(string databasePath)
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

    public async ValueTask<DirectoryCopyActionHistory> BeginAsync(
        DirectoryCopyTransactionPlan plan,
        DirectoryCopyFreshManifestGateResult freshGate,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(freshGate);
        ThrowIfDisposed();
        if (!freshGate.CanBeginDurableHistory ||
            !ReferenceEquals(freshGate.ReviewedManifest, plan.ReviewedManifest) ||
            freshGate.Revalidation?.EvidenceStillMatches != true)
        {
            throw new InvalidOperationException(
                "Directory Copy history can begin only from the exact reviewed manifest after successful fresh acquisition/revalidation.");
        }
        var destinationParentIdentity = plan.DestinationParent.Identity
            ?? throw new InvalidOperationException("Directory Copy destination parent has no stable identity.");
        var actions = BuildActions(plan);
        var operationId = plan.OperationId.ToString("D", CultureInfo.InvariantCulture);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO directory_copy_operations(
                        operation_id, queued_utc_ticks, fresh_validated_utc_ticks, started_utc_ticks,
                        completed_utc_ticks, canonical_source_root_path,
                        source_root_volume_serial, source_root_file_reference,
                        canonical_destination_parent_path,
                        destination_parent_volume_serial, destination_parent_file_reference,
                        canonical_destination_root_path, terminal_state)
                    VALUES(
                        @operation_id, @queued, @validated, @started,
                        NULL, @source_root, @source_serial, @source_ref,
                        @destination_parent, @destination_parent_serial, @destination_parent_ref,
                        @destination_root, NULL);
                    """;
                command.Parameters.AddWithValue("@operation_id", operationId);
                command.Parameters.AddWithValue("@queued", ToUtcTicks(plan.QueuedAtUtc));
                command.Parameters.AddWithValue("@validated", ToUtcTicks(DateTimeOffset.UtcNow));
                command.Parameters.AddWithValue("@started", ToUtcTicks(startedAtUtc));
                command.Parameters.AddWithValue("@source_root", plan.ReviewedManifest.CanonicalRootPath);
                command.Parameters.AddWithValue("@source_serial", ToSqlInteger(plan.ReviewedManifest.RootIdentity.VolumeSerialNumber));
                command.Parameters.AddWithValue("@source_ref", ToSqlInteger(plan.ReviewedManifest.RootIdentity.FileReferenceNumber));
                command.Parameters.AddWithValue("@destination_parent", plan.DestinationParent.CanonicalPath);
                command.Parameters.AddWithValue("@destination_parent_serial", ToSqlInteger(destinationParentIdentity.VolumeSerialNumber));
                command.Parameters.AddWithValue("@destination_parent_ref", ToSqlInteger(destinationParentIdentity.FileReferenceNumber));
                command.Parameters.AddWithValue("@destination_root", plan.CanonicalDestinationRootPath);
                command.ExecuteNonQuery();
            }

            foreach (var action in actions)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO directory_copy_entries(
                        operation_id, ordinal, kind, relative_path,
                        canonical_source_path, source_volume_serial, source_file_reference,
                        canonical_destination_path, destination_parent_ordinal,
                        destination_volume_serial, destination_file_reference,
                        destination_fingerprint_algorithm, destination_fingerprint_hex,
                        state, mutation_started_utc_ticks, completed_utc_ticks,
                        failure_code, failure_message, failure_path, failure_retryable)
                    VALUES(
                        @operation_id, @ordinal, @kind, @relative_path,
                        @source_path, @source_serial, @source_ref,
                        @destination_path, @parent_ordinal,
                        NULL, NULL, NULL, NULL,
                        @state, NULL, NULL,
                        NULL, NULL, NULL, NULL);
                    """;
                command.Parameters.AddWithValue("@operation_id", operationId);
                command.Parameters.AddWithValue("@ordinal", action.Ordinal);
                command.Parameters.AddWithValue("@kind", (int)action.Kind);
                command.Parameters.AddWithValue("@relative_path", action.RelativePath);
                command.Parameters.AddWithValue("@source_path", action.CanonicalSourcePath);
                command.Parameters.AddWithValue("@source_serial", ToSqlInteger(action.SourceIdentity.VolumeSerialNumber));
                command.Parameters.AddWithValue("@source_ref", ToSqlInteger(action.SourceIdentity.FileReferenceNumber));
                command.Parameters.AddWithValue("@destination_path", action.CanonicalDestinationPath);
                command.Parameters.AddWithValue("@parent_ordinal", action.DestinationParentOrdinal is int parent ? parent : DBNull.Value);
                command.Parameters.AddWithValue("@state", (int)DirectoryCopyActionEntryState.Pending);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        finally
        {
            _writeGate.Release();
        }
        return await GetRequiredAsync(plan.OperationId, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<DirectoryCopyActionHistory> MarkMutationStartedAsync(
        Guid operationId, int ordinal, DateTimeOffset startedAtUtc, CancellationToken cancellationToken = default) =>
        TransitionAsync(operationId, ordinal, DirectoryCopyActionEntryState.Pending,
            DirectoryCopyActionEntryState.MutationStarted, startedAtUtc, null, null, null, null, cancellationToken);

    public ValueTask<DirectoryCopyActionHistory> CommitAsync(
        Guid operationId, int ordinal, FileIdentity destinationIdentity,
        FileContentFingerprint? destinationContentFingerprint, DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(operationId, ordinal, DirectoryCopyActionEntryState.MutationStarted,
            DirectoryCopyActionEntryState.Committed, null, committedAtUtc, null,
            destinationIdentity, destinationContentFingerprint, cancellationToken);

    public ValueTask<DirectoryCopyActionHistory> MarkFailedBeforeMutationAsync(
        Guid operationId, int ordinal, FileOperationFailure failure, DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return TransitionAsync(operationId, ordinal, DirectoryCopyActionEntryState.Pending,
            DirectoryCopyActionEntryState.Failed, null, failedAtUtc, failure, null, null, cancellationToken);
    }

    public ValueTask<DirectoryCopyActionHistory> MarkRecoveryRequiredAsync(
        Guid operationId, int ordinal, FileOperationFailure failure, DateTimeOffset failedAtUtc,
        FileIdentity? observedDestinationIdentity = null,
        FileContentFingerprint? observedDestinationContentFingerprint = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return TransitionAsync(operationId, ordinal, DirectoryCopyActionEntryState.MutationStarted,
            DirectoryCopyActionEntryState.RecoveryRequired, null, failedAtUtc, failure,
            observedDestinationIdentity, observedDestinationContentFingerprint, cancellationToken);
    }

    public async ValueTask<DirectoryCopyActionHistory> CompleteAsync(
        Guid operationId, DirectoryCopyActionTerminalState terminalState, DateTimeOffset completedAtUtc,
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
                UPDATE directory_copy_operations
                SET terminal_state = @terminal, completed_utc_ticks = @completed
                WHERE operation_id = @operation_id AND terminal_state IS NULL;
                """;
            command.Parameters.AddWithValue("@terminal", (int)terminalState);
            command.Parameters.AddWithValue("@completed", ToUtcTicks(completedAtUtc));
            command.Parameters.AddWithValue("@operation_id", operationId.ToString("D", CultureInfo.InvariantCulture));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("Directory Copy history is missing or already terminal.");
            }
            transaction.Commit();
        }
        finally
        {
            _writeGate.Release();
        }
        return await GetRequiredAsync(operationId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DirectoryCopyActionHistory?> GetAsync(
        Guid operationId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var connection = OpenConnection();
        return await ReadHistoryAsync(connection, operationId, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writeGate.Dispose();
    }

    private async ValueTask<DirectoryCopyActionHistory> TransitionAsync(
        Guid operationId, int ordinal,
        DirectoryCopyActionEntryState expectedState, DirectoryCopyActionEntryState nextState,
        DateTimeOffset? mutationStartedAtUtc, DateTimeOffset? completedAtUtc,
        FileOperationFailure? failure, FileIdentity? destinationIdentity,
        FileContentFingerprint? destinationContentFingerprint,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            ValidateFingerprintTransition(
                connection,
                transaction,
                operationId,
                ordinal,
                nextState,
                destinationContentFingerprint);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE directory_copy_entries
                SET state = @next_state,
                    mutation_started_utc_ticks = COALESCE(@mutation_started, mutation_started_utc_ticks),
                    completed_utc_ticks = @completed,
                    destination_volume_serial = COALESCE(@destination_serial, destination_volume_serial),
                    destination_file_reference = COALESCE(@destination_ref, destination_file_reference),
                    destination_fingerprint_algorithm = COALESCE(@fingerprint_algorithm, destination_fingerprint_algorithm),
                    destination_fingerprint_hex = COALESCE(@fingerprint_hex, destination_fingerprint_hex),
                    failure_code = @failure_code,
                    failure_message = @failure_message,
                    failure_path = @failure_path,
                    failure_retryable = @failure_retryable
                WHERE operation_id = @operation_id AND ordinal = @ordinal AND state = @expected_state;
                """;
            command.Parameters.AddWithValue("@next_state", (int)nextState);
            command.Parameters.AddWithValue("@mutation_started", mutationStartedAtUtc is { } started ? ToUtcTicks(started) : DBNull.Value);
            command.Parameters.AddWithValue("@completed", completedAtUtc is { } completed ? ToUtcTicks(completed) : DBNull.Value);
            command.Parameters.AddWithValue("@destination_serial", destinationIdentity is { } identity ? ToSqlInteger(identity.VolumeSerialNumber) : DBNull.Value);
            command.Parameters.AddWithValue("@destination_ref", destinationIdentity is { } identity2 ? ToSqlInteger(identity2.FileReferenceNumber) : DBNull.Value);
            command.Parameters.AddWithValue("@fingerprint_algorithm", destinationContentFingerprint is { } fingerprint ? (int)fingerprint.Algorithm : DBNull.Value);
            command.Parameters.AddWithValue("@fingerprint_hex", destinationContentFingerprint is { } fingerprint2 ? fingerprint2.HexDigest : DBNull.Value);
            command.Parameters.AddWithValue("@failure_code", failure is null ? DBNull.Value : failure.Code);
            command.Parameters.AddWithValue("@failure_message", failure is null ? DBNull.Value : failure.Message);
            command.Parameters.AddWithValue("@failure_path", failure?.Path is { } path ? path : DBNull.Value);
            command.Parameters.AddWithValue("@failure_retryable", failure is null ? DBNull.Value : failure.Retryable ? 1 : 0);
            command.Parameters.AddWithValue("@operation_id", operationId.ToString("D", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@ordinal", ordinal);
            command.Parameters.AddWithValue("@expected_state", (int)expectedState);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Directory Copy action {ordinal} is missing or is not in required state {expectedState}.");
            }
            transaction.Commit();
        }
        finally
        {
            _writeGate.Release();
        }
        return await GetRequiredAsync(operationId, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateFingerprintTransition(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid operationId,
        int ordinal,
        DirectoryCopyActionEntryState nextState,
        FileContentFingerprint? fingerprint)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT kind FROM directory_copy_entries
            WHERE operation_id = @operation_id AND ordinal = @ordinal;
            """;
        command.Parameters.AddWithValue("@operation_id", operationId.ToString("D", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@ordinal", ordinal);
        var value = command.ExecuteScalar();
        if (value is null || value is DBNull)
        {
            throw new InvalidOperationException($"Directory Copy action {ordinal} was not found.");
        }
        var kind = (DirectoryCopyActionKind)Convert.ToInt32(value, CultureInfo.InvariantCulture);
        if (kind == DirectoryCopyActionKind.CreateDirectory && fingerprint is not null)
        {
            throw new InvalidOperationException("Directory creation history cannot persist file-content fingerprint evidence.");
        }
        if (kind == DirectoryCopyActionKind.CopyFile &&
            nextState == DirectoryCopyActionEntryState.Committed &&
            fingerprint is null)
        {
            throw new InvalidOperationException("Committed directory Copy file history requires destination SHA-256 evidence.");
        }
    }

    private static IReadOnlyList<DirectoryCopyActionEntry> BuildActions(DirectoryCopyTransactionPlan plan)
    {
        var actions = new List<DirectoryCopyActionEntry>
        {
            new(
                0,
                DirectoryCopyActionKind.CreateDirectory,
                string.Empty,
                plan.ReviewedManifest.CanonicalRootPath,
                plan.ReviewedManifest.RootIdentity,
                plan.CanonicalDestinationRootPath,
                null,
                null,
                null,
                DirectoryCopyActionEntryState.Pending,
                null,
                null,
                null),
        };
        var ordinalByRelativePath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = 0,
        };
        foreach (var entry in plan.ReviewedManifest.Entries)
        {
            var parentRelative = Path.GetDirectoryName(entry.RelativePath) ?? string.Empty;
            if (!ordinalByRelativePath.TryGetValue(parentRelative, out var parentOrdinal))
            {
                throw new InvalidOperationException(
                    $"Directory Copy manifest action '{entry.RelativePath}' has no earlier directory parent action.");
            }
            var ordinal = actions.Count;
            var destination = Path.GetFullPath(Path.Combine(plan.CanonicalDestinationRootPath, entry.RelativePath));
            actions.Add(new DirectoryCopyActionEntry(
                ordinal,
                entry.Kind == DirectoryOperationTreeEntryKind.Directory
                    ? DirectoryCopyActionKind.CreateDirectory
                    : DirectoryCopyActionKind.CopyFile,
                entry.RelativePath,
                entry.CanonicalPath,
                entry.Identity,
                destination,
                parentOrdinal,
                null,
                null,
                DirectoryCopyActionEntryState.Pending,
                null,
                null,
                null));
            if (entry.Kind == DirectoryOperationTreeEntryKind.Directory)
            {
                ordinalByRelativePath[entry.RelativePath] = ordinal;
            }
        }
        return actions;
    }

    private static void ValidateTerminalState(
        SqliteConnection connection, SqliteTransaction transaction,
        Guid operationId, DirectoryCopyActionTerminalState terminalState)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT state, COUNT(*) FROM directory_copy_entries
            WHERE operation_id = @operation_id GROUP BY state;
            """;
        command.Parameters.AddWithValue("@operation_id", operationId.ToString("D", CultureInfo.InvariantCulture));
        var counts = new Dictionary<DirectoryCopyActionEntryState, long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            counts[(DirectoryCopyActionEntryState)reader.GetInt32(0)] = reader.GetInt64(1);
        }
        if (counts.Count == 0)
        {
            throw new InvalidOperationException("Directory Copy history has no actions.");
        }
        var sensitive = Count(counts, DirectoryCopyActionEntryState.MutationStarted) +
            Count(counts, DirectoryCopyActionEntryState.RecoveryRequired);
        if (terminalState == DirectoryCopyActionTerminalState.Succeeded &&
            (counts.Count != 1 || !counts.ContainsKey(DirectoryCopyActionEntryState.Committed)))
        {
            throw new InvalidOperationException("Directory Copy cannot succeed until every recursive action is committed.");
        }
        if (terminalState is DirectoryCopyActionTerminalState.Failed or DirectoryCopyActionTerminalState.Cancelled && sensitive != 0)
        {
            throw new InvalidOperationException(
                "A mutation-sensitive directory Copy cannot settle as Failed/Cancelled; recovery is required.");
        }
        if (terminalState == DirectoryCopyActionTerminalState.RecoveryRequired && sensitive == 0)
        {
            throw new InvalidOperationException("Directory Copy cannot require recovery without a mutation-sensitive action.");
        }
    }

    private static long Count(
        IReadOnlyDictionary<DirectoryCopyActionEntryState, long> counts,
        DirectoryCopyActionEntryState state) =>
        counts.TryGetValue(state, out var count) ? count : 0;

    private async ValueTask<DirectoryCopyActionHistory> GetRequiredAsync(
        Guid operationId,
        CancellationToken cancellationToken) =>
        await GetAsync(operationId, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("Directory Copy history was not found after a durable transition.");

    private static async ValueTask<DirectoryCopyActionHistory?> ReadHistoryAsync(
        SqliteConnection connection,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        using var operation = connection.CreateCommand();
        operation.CommandText = """
            SELECT queued_utc_ticks, fresh_validated_utc_ticks, started_utc_ticks, completed_utc_ticks,
                   canonical_source_root_path, source_root_volume_serial, source_root_file_reference,
                   canonical_destination_parent_path, destination_parent_volume_serial, destination_parent_file_reference,
                   canonical_destination_root_path, terminal_state
            FROM directory_copy_operations WHERE operation_id = @operation_id;
            """;
        operation.Parameters.AddWithValue("@operation_id", operationId.ToString("D", CultureInfo.InvariantCulture));
        using var reader = await operation.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        var queued = FromUtcTicks(reader.GetInt64(0));
        var validated = FromUtcTicks(reader.GetInt64(1));
        var started = FromUtcTicks(reader.GetInt64(2));
        DateTimeOffset? completed = reader.IsDBNull(3) ? null : FromUtcTicks(reader.GetInt64(3));
        var sourceRoot = reader.GetString(4);
        var sourceIdentity = new FileIdentity(FromSqlUInt32(reader.GetInt64(5)), FromSqlUInt64(reader.GetInt64(6)));
        var destinationParent = reader.GetString(7);
        var destinationParentIdentity = new FileIdentity(FromSqlUInt32(reader.GetInt64(8)), FromSqlUInt64(reader.GetInt64(9)));
        var destinationRoot = reader.GetString(10);
        DirectoryCopyActionTerminalState? terminal = reader.IsDBNull(11)
            ? null
            : (DirectoryCopyActionTerminalState)reader.GetInt32(11);
        await reader.DisposeAsync().ConfigureAwait(false);

        using var entriesCommand = connection.CreateCommand();
        entriesCommand.CommandText = """
            SELECT ordinal, kind, relative_path, canonical_source_path,
                   source_volume_serial, source_file_reference, canonical_destination_path,
                   destination_parent_ordinal, destination_volume_serial, destination_file_reference,
                   destination_fingerprint_algorithm, destination_fingerprint_hex,
                   state, mutation_started_utc_ticks, completed_utc_ticks,
                   failure_code, failure_message, failure_path, failure_retryable
            FROM directory_copy_entries WHERE operation_id = @operation_id ORDER BY ordinal;
            """;
        entriesCommand.Parameters.AddWithValue("@operation_id", operationId.ToString("D", CultureInfo.InvariantCulture));
        var entries = new List<DirectoryCopyActionEntry>();
        using var entryReader = await entriesCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await entryReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            FileIdentity? destinationIdentity = entryReader.IsDBNull(8)
                ? null
                : new FileIdentity(
                    FromSqlUInt32(entryReader.GetInt64(8)),
                    FromSqlUInt64(entryReader.GetInt64(9)));
            FileContentFingerprint? fingerprint = entryReader.IsDBNull(10)
                ? null
                : new FileContentFingerprint(
                    (FileContentFingerprintAlgorithm)entryReader.GetInt32(10),
                    entryReader.GetString(11));
            FileOperationFailure? failure = entryReader.IsDBNull(15)
                ? null
                : new FileOperationFailure(
                    entryReader.GetString(15),
                    entryReader.IsDBNull(16) ? string.Empty : entryReader.GetString(16),
                    entryReader.IsDBNull(17) ? null : entryReader.GetString(17),
                    !entryReader.IsDBNull(18) && entryReader.GetInt32(18) != 0);
            entries.Add(new DirectoryCopyActionEntry(
                entryReader.GetInt32(0),
                (DirectoryCopyActionKind)entryReader.GetInt32(1),
                entryReader.GetString(2),
                entryReader.GetString(3),
                new FileIdentity(
                    FromSqlUInt32(entryReader.GetInt64(4)),
                    FromSqlUInt64(entryReader.GetInt64(5))),
                entryReader.GetString(6),
                entryReader.IsDBNull(7) ? null : entryReader.GetInt32(7),
                destinationIdentity,
                fingerprint,
                (DirectoryCopyActionEntryState)entryReader.GetInt32(12),
                entryReader.IsDBNull(13) ? null : FromUtcTicks(entryReader.GetInt64(13)),
                entryReader.IsDBNull(14) ? null : FromUtcTicks(entryReader.GetInt64(14)),
                failure));
        }
        return new DirectoryCopyActionHistory(
            operationId,
            queued,
            validated,
            started,
            completed,
            sourceRoot,
            sourceIdentity,
            destinationParent,
            destinationParentIdentity,
            destinationRoot,
            terminal,
            entries);
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS directory_copy_operations(
                operation_id TEXT PRIMARY KEY,
                queued_utc_ticks INTEGER NOT NULL,
                fresh_validated_utc_ticks INTEGER NOT NULL,
                started_utc_ticks INTEGER NOT NULL,
                completed_utc_ticks INTEGER NULL,
                canonical_source_root_path TEXT NOT NULL,
                source_root_volume_serial INTEGER NOT NULL,
                source_root_file_reference INTEGER NOT NULL,
                canonical_destination_parent_path TEXT NOT NULL,
                destination_parent_volume_serial INTEGER NOT NULL,
                destination_parent_file_reference INTEGER NOT NULL,
                canonical_destination_root_path TEXT NOT NULL,
                terminal_state INTEGER NULL
            );
            CREATE TABLE IF NOT EXISTS directory_copy_entries(
                operation_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                kind INTEGER NOT NULL,
                relative_path TEXT NOT NULL,
                canonical_source_path TEXT NOT NULL,
                source_volume_serial INTEGER NOT NULL,
                source_file_reference INTEGER NOT NULL,
                canonical_destination_path TEXT NOT NULL,
                destination_parent_ordinal INTEGER NULL,
                destination_volume_serial INTEGER NULL,
                destination_file_reference INTEGER NULL,
                destination_fingerprint_algorithm INTEGER NULL,
                destination_fingerprint_hex TEXT NULL,
                state INTEGER NOT NULL,
                mutation_started_utc_ticks INTEGER NULL,
                completed_utc_ticks INTEGER NULL,
                failure_code TEXT NULL,
                failure_message TEXT NULL,
                failure_path TEXT NULL,
                failure_retryable INTEGER NULL,
                PRIMARY KEY(operation_id, ordinal),
                FOREIGN KEY(operation_id) REFERENCES directory_copy_operations(operation_id)
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

    private static long ToUtcTicks(DateTimeOffset value) => value.ToUniversalTime().UtcDateTime.Ticks;
    private static DateTimeOffset FromUtcTicks(long ticks) => new(new DateTime(ticks, DateTimeKind.Utc));
    private static long ToSqlInteger(uint value) => unchecked((long)value);
    private static long ToSqlInteger(ulong value) => unchecked((long)value);
    private static uint FromSqlUInt32(long value) => unchecked((uint)value);
    private static ulong FromSqlUInt64(long value) => unchecked((ulong)value);
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
