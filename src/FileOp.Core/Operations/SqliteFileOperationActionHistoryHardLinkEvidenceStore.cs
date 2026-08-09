using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using Microsoft.Data.Sqlite;

namespace FileOp.Core.Operations;

/// <summary>
/// Compatibility-preserving topology-aware decorator for schema-v1 action history.
/// Stronger Copy methods atomically persist state, identity, SHA-256 and hard-link count.
/// </summary>
public sealed class SqliteFileOperationActionHistoryHardLinkEvidenceStore :
    IFileOperationActionHistoryHardLinkEvidenceStore,
    IDisposable
{
    private const int MaximumRecentLimit = 4_096;

    private readonly SqliteFileOperationActionHistoryStore _inner;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _disposed;

    public SqliteFileOperationActionHistoryHardLinkEvidenceStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        _inner = new SqliteFileOperationActionHistoryStore(fullPath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 5,
        }.ToString();
        InitializeEvidenceTable();
    }

    public ValueTask<FileOperationActionHistory> BeginAsync(
        FileOperationExecutionValidationResult validation,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default) =>
        _inner.BeginAsync(validation, startedAtUtc, cancellationToken);

    public async ValueTask<FileOperationActionHistory> MarkMutationStartedAsync(
        Guid operationId,
        int ordinal,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var before = await GetAsync(operationId, cancellationToken).ConfigureAwait(false);
        var after = await _inner.MarkMutationStartedAsync(operationId, ordinal, startedAtUtc, cancellationToken)
            .ConfigureAwait(false);
        return before is null ? after : CarryPriorEvidence(before, after);
    }

    public async ValueTask<FileOperationActionHistory> MarkEntryFailedBeforeMutationAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var before = await GetAsync(operationId, cancellationToken).ConfigureAwait(false);
        var after = await _inner.MarkEntryFailedBeforeMutationAsync(
                operationId,
                ordinal,
                failure,
                failedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
        return before is null ? after : CarryPriorEvidence(before, after);
    }

    /// <summary>Legacy commit path retained for source/API compatibility.</summary>
    public async ValueTask<FileOperationActionHistory> CommitCopyAsync(
        Guid operationId,
        int ordinal,
        FileIdentity destinationIdentity,
        FileContentFingerprint destinationContentFingerprint,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var before = await GetAsync(operationId, cancellationToken).ConfigureAwait(false);
        var after = await _inner.CommitCopyAsync(
                operationId,
                ordinal,
                destinationIdentity,
                destinationContentFingerprint,
                committedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
        return before is null ? after : CarryPriorEvidence(before, after);
    }

    /// <summary>Legacy recovery path retained for source/API compatibility.</summary>
    public async ValueTask<FileOperationActionHistory> MarkMutationRecoveryRequiredAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken = default,
        FileIdentity? destinationIdentity = null,
        FileContentFingerprint? destinationContentFingerprint = null)
    {
        var before = await GetAsync(operationId, cancellationToken).ConfigureAwait(false);
        var after = await _inner.MarkMutationRecoveryRequiredAsync(
                operationId,
                ordinal,
                failure,
                failedAtUtc,
                cancellationToken,
                destinationIdentity,
                destinationContentFingerprint)
            .ConfigureAwait(false);
        return before is null ? after : CarryPriorEvidence(before, after);
    }

    public async ValueTask<FileOperationActionHistory> CommitCopyWithHardLinkEvidenceAsync(
        Guid operationId,
        int ordinal,
        FileIdentity destinationIdentity,
        FileContentFingerprint destinationContentFingerprint,
        uint destinationHardLinkCount,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateEvidence(ordinal, destinationContentFingerprint, destinationHardLinkCount);
        var operationKey = FormatOperationId(operationId);
        var committedAt = NormalizeUtc(committedAtUtc);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Overlay prior counts before the durable transition. After Commit(),
            // only pure in-memory snapshot construction is allowed.
            var before = await LoadRequiredOverlayAsync(operationId, cancellationToken).ConfigureAwait(false);
            ValidateTransitionSnapshot(before, ordinal, FileOperationActionEntryState.MutationStarted);

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            await UpdateCommittedEntryAsync(
                connection,
                transaction,
                operationKey,
                ordinal,
                destinationIdentity,
                committedAt,
                cancellationToken).ConfigureAwait(false);
            await PersistFingerprintAsync(
                connection,
                transaction,
                operationKey,
                ordinal,
                destinationContentFingerprint,
                cancellationToken).ConfigureAwait(false);
            await PersistHardLinkCountAsync(
                connection,
                transaction,
                operationKey,
                ordinal,
                destinationHardLinkCount,
                cancellationToken).ConfigureAwait(false);
            transaction.Commit();

            return ApplyCommittedEvidence(
                before,
                ordinal,
                destinationIdentity,
                destinationContentFingerprint,
                destinationHardLinkCount,
                committedAt);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<FileOperationActionHistory> MarkMutationRecoveryRequiredWithHardLinkEvidenceAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        DateTimeOffset failedAtUtc,
        FileIdentity destinationIdentity,
        FileContentFingerprint destinationContentFingerprint,
        uint destinationHardLinkCount,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateFailure(failure);
        ValidateEvidence(ordinal, destinationContentFingerprint, destinationHardLinkCount);
        var operationKey = FormatOperationId(operationId);
        var failedAt = NormalizeUtc(failedAtUtc);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = await LoadRequiredOverlayAsync(operationId, cancellationToken).ConfigureAwait(false);
            ValidateTransitionSnapshot(before, ordinal, FileOperationActionEntryState.MutationStarted);

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            await UpdateRecoveryEntryAsync(
                connection,
                transaction,
                operationKey,
                ordinal,
                failure,
                destinationIdentity,
                failedAt,
                cancellationToken).ConfigureAwait(false);
            await PersistFingerprintAsync(
                connection,
                transaction,
                operationKey,
                ordinal,
                destinationContentFingerprint,
                cancellationToken).ConfigureAwait(false);
            await PersistHardLinkCountAsync(
                connection,
                transaction,
                operationKey,
                ordinal,
                destinationHardLinkCount,
                cancellationToken).ConfigureAwait(false);
            transaction.Commit();

            return ApplyRecoveryEvidence(
                before,
                ordinal,
                failure,
                destinationIdentity,
                destinationContentFingerprint,
                destinationHardLinkCount,
                failedAt);
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
        var before = await GetAsync(operationId, cancellationToken).ConfigureAwait(false);
        var after = await _inner.CompleteAsync(operationId, terminalState, completedAtUtc, cancellationToken)
            .ConfigureAwait(false);
        return before is null ? after : CarryPriorEvidence(before, after);
    }

    public async ValueTask<FileOperationActionHistory?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var history = await _inner.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
        return history is null ? null : await OverlayAsync(history, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<FileOperationActionHistory>> GetRecentAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (limit <= 0 || limit > MaximumRecentLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var histories = await _inner.GetRecentAsync(limit, cancellationToken).ConfigureAwait(false);
        var result = new List<FileOperationActionHistory>(histories.Count);
        foreach (var history in histories)
        {
            result.Add(await OverlayAsync(history, cancellationToken).ConfigureAwait(false));
        }

        return result.AsReadOnly();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _inner.Dispose();
        _writeGate.Dispose();
    }

    private void InitializeEvidenceTable()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS file_operation_action_entry_hard_link_evidence(
                operation_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
                hard_link_count INTEGER NOT NULL CHECK(hard_link_count BETWEEN 1 AND 4294967295),
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
        ThrowIfDisposed();
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

    private async ValueTask<FileOperationActionHistory> LoadRequiredOverlayAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var history = await _inner.GetAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Action history for operation {operationId} does not exist.");
        return await OverlayAsync(history, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<FileOperationActionHistory> OverlayAsync(
        FileOperationActionHistory history,
        CancellationToken cancellationToken)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ordinal, hard_link_count
            FROM file_operation_action_entry_hard_link_evidence
            WHERE operation_id = @operation_id
            ORDER BY ordinal;
            """;
        command.Parameters.AddWithValue("@operation_id", FormatOperationId(history.OperationId));

        var counts = new Dictionary<int, uint>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var ordinal = checked((int)reader.GetInt64(0));
                var rawCount = reader.GetInt64(1);
                if (rawCount <= 0 || rawCount > uint.MaxValue || !counts.TryAdd(ordinal, checked((uint)rawCount)))
                {
                    throw new InvalidDataException("Persisted hard-link count evidence is invalid or duplicated.");
                }
            }
        }

        if (counts.Count == 0)
        {
            return history;
        }

        var entries = history.Entries.ToArray();
        foreach (var pair in counts)
        {
            if (pair.Key < 0 || pair.Key >= entries.Length || entries[pair.Key].Ordinal != pair.Key)
            {
                throw new InvalidDataException("Persisted hard-link count evidence references a missing action entry.");
            }

            var entry = entries[pair.Key];
            ValidateEntryAllowsHardLinkEvidence(entry);
            entries[pair.Key] = entry with { DestinationHardLinkCount = pair.Value };
        }

        return CloneHistory(history, entries);
    }

    private static FileOperationActionHistory CarryPriorEvidence(
        FileOperationActionHistory before,
        FileOperationActionHistory after)
    {
        if (before.Entries.Count != after.Entries.Count)
        {
            throw new InvalidDataException("Action-history entry count changed while carrying hard-link evidence.");
        }

        var entries = after.Entries.ToArray();
        for (var index = 0; index < entries.Length; index++)
        {
            if (before.Entries[index].Ordinal != entries[index].Ordinal)
            {
                throw new InvalidDataException("Action-history ordinals changed while carrying hard-link evidence.");
            }

            if (before.Entries[index].DestinationHardLinkCount is uint count)
            {
                entries[index] = entries[index] with { DestinationHardLinkCount = count };
            }
        }

        return CloneHistory(after, entries);
    }

    private static async ValueTask UpdateCommittedEntryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationKey,
        int ordinal,
        FileIdentity destinationIdentity,
        DateTimeOffset committedAt,
        CancellationToken cancellationToken)
    {
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
                  SELECT 1 FROM file_operation_actions AS action
                  WHERE action.operation_id = @operation_id
                    AND action.terminal_state IS NULL
                    AND action.kind = @copy_kind
              );
            """;
        command.Parameters.AddWithValue("@committed", (int)FileOperationActionEntryState.Committed);
        command.Parameters.AddWithValue("@completed_utc_ticks", ToUtcTicks(committedAt));
        command.Parameters.AddWithValue("@destination_volume_serial", ToSqliteInteger(destinationIdentity.VolumeSerialNumber));
        command.Parameters.AddWithValue("@destination_file_reference", ToSqliteInteger(destinationIdentity.FileReferenceNumber));
        command.Parameters.AddWithValue("@undo_kind", (int)FileOperationUndoKind.DeleteCreatedDestination);
        command.Parameters.AddWithValue("@operation_id", operationKey);
        command.Parameters.AddWithValue("@ordinal", ordinal);
        command.Parameters.AddWithValue("@mutation_started", (int)FileOperationActionEntryState.MutationStarted);
        command.Parameters.AddWithValue("@copy_kind", (int)FileOperationKind.Copy);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("A topology-aware Copy entry can be committed only once after MutationStarted while its operation remains active.");
        }
    }

    private static async ValueTask UpdateRecoveryEntryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationKey,
        int ordinal,
        FileOperationFailure failure,
        FileIdentity destinationIdentity,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
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
                  SELECT 1 FROM file_operation_actions AS action
                  WHERE action.operation_id = @operation_id
                    AND action.terminal_state IS NULL
                    AND action.kind = @copy_kind
              );
            """;
        command.Parameters.AddWithValue("@recovery_required", (int)FileOperationActionEntryState.RecoveryRequired);
        command.Parameters.AddWithValue("@completed_utc_ticks", ToUtcTicks(failedAt));
        command.Parameters.AddWithValue("@destination_volume_serial", ToSqliteInteger(destinationIdentity.VolumeSerialNumber));
        command.Parameters.AddWithValue("@destination_file_reference", ToSqliteInteger(destinationIdentity.FileReferenceNumber));
        command.Parameters.AddWithValue("@undo_kind", (int)FileOperationUndoKind.None);
        SetFailureParameters(command, failure);
        command.Parameters.AddWithValue("@operation_id", operationKey);
        command.Parameters.AddWithValue("@ordinal", ordinal);
        command.Parameters.AddWithValue("@mutation_started", (int)FileOperationActionEntryState.MutationStarted);
        command.Parameters.AddWithValue("@copy_kind", (int)FileOperationKind.Copy);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("A topology-aware Copy entry can require recovery only after MutationStarted while its operation remains active.");
        }
    }

    private static async ValueTask PersistFingerprintAsync(
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
            INSERT INTO file_operation_action_entry_content_fingerprints(operation_id, ordinal, algorithm, digest_hex)
            VALUES(@operation_id, @ordinal, @algorithm, @digest_hex);
            """;
        command.Parameters.AddWithValue("@operation_id", operationKey);
        command.Parameters.AddWithValue("@ordinal", ordinal);
        command.Parameters.AddWithValue("@algorithm", (int)fingerprint.Algorithm);
        command.Parameters.AddWithValue("@digest_hex", fingerprint.HexDigest);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("Destination content fingerprint persistence did not write exactly one row.");
        }
    }

    private static async ValueTask PersistHardLinkCountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationKey,
        int ordinal,
        uint hardLinkCount,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO file_operation_action_entry_hard_link_evidence(operation_id, ordinal, hard_link_count)
            VALUES(@operation_id, @ordinal, @hard_link_count);
            """;
        command.Parameters.AddWithValue("@operation_id", operationKey);
        command.Parameters.AddWithValue("@ordinal", ordinal);
        command.Parameters.AddWithValue("@hard_link_count", (long)hardLinkCount);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("Destination hard-link count persistence did not write exactly one row.");
        }
    }

    private static FileOperationActionHistory ApplyCommittedEvidence(
        FileOperationActionHistory before,
        int ordinal,
        FileIdentity destinationIdentity,
        FileContentFingerprint fingerprint,
        uint hardLinkCount,
        DateTimeOffset committedAt)
    {
        var entries = before.Entries.ToArray();
        entries[ordinal] = entries[ordinal] with
        {
            State = FileOperationActionEntryState.Committed,
            CompletedAtUtc = committedAt,
            DestinationIdentity = destinationIdentity,
            UndoKind = FileOperationUndoKind.DeleteCreatedDestination,
            Failure = null,
            DestinationContentFingerprint = fingerprint,
            DestinationHardLinkCount = hardLinkCount,
        };
        return CloneHistory(before, entries);
    }

    private static FileOperationActionHistory ApplyRecoveryEvidence(
        FileOperationActionHistory before,
        int ordinal,
        FileOperationFailure failure,
        FileIdentity destinationIdentity,
        FileContentFingerprint fingerprint,
        uint hardLinkCount,
        DateTimeOffset failedAt)
    {
        var entries = before.Entries.ToArray();
        entries[ordinal] = entries[ordinal] with
        {
            State = FileOperationActionEntryState.RecoveryRequired,
            CompletedAtUtc = failedAt,
            DestinationIdentity = destinationIdentity,
            UndoKind = FileOperationUndoKind.None,
            Failure = failure,
            DestinationContentFingerprint = fingerprint,
            DestinationHardLinkCount = hardLinkCount,
        };
        return CloneHistory(before, entries);
    }

    private static FileOperationActionHistory CloneHistory(FileOperationActionHistory history, IReadOnlyList<FileOperationActionEntry> entries) =>
        new(
            history.OperationId,
            history.QueuedAtUtc,
            history.ValidatedAtUtc,
            history.StartedAtUtc,
            history.CompletedAtUtc,
            history.Kind,
            history.CollisionPolicy,
            history.SourceDirectoryPath,
            history.DestinationDirectoryPath,
            history.CanonicalSourceDirectoryPath,
            history.CanonicalDestinationDirectoryPath,
            history.TerminalState,
            entries,
            history.SourceDirectoryIdentity,
            history.DestinationDirectoryIdentity);

    private static void ValidateTransitionSnapshot(FileOperationActionHistory history, int ordinal, FileOperationActionEntryState expectedState)
    {
        if (history.Kind != FileOperationKind.Copy ||
            history.TerminalState.HasValue ||
            ordinal < 0 ||
            ordinal >= history.Entries.Count ||
            history.Entries[ordinal].Ordinal != ordinal ||
            history.Entries[ordinal].State != expectedState)
        {
            throw new InvalidOperationException($"Entry {ordinal} is not in the expected active Copy state {expectedState}.");
        }
    }

    private static void ValidateEntryAllowsHardLinkEvidence(FileOperationActionEntry entry)
    {
        if (entry.State is not FileOperationActionEntryState.Committed and
                not FileOperationActionEntryState.RecoveryRequired ||
            !entry.DestinationIdentity.HasValue ||
            entry.DestinationContentFingerprint is null)
        {
            throw new InvalidDataException("Persisted hard-link count evidence requires a committed/recovery Copy destination identity and content fingerprint.");
        }
    }

    private static void ValidateEvidence(int ordinal, FileContentFingerprint fingerprint, uint hardLinkCount)
    {
        if (ordinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        ArgumentNullException.ThrowIfNull(fingerprint);
        if (hardLinkCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hardLinkCount), "A verified destination hard-link count must be greater than zero.");
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

    private static void SetFailureParameters(SqliteCommand command, FileOperationFailure failure)
    {
        command.Parameters.AddWithValue("@failure_code", failure.Code);
        command.Parameters.AddWithValue("@failure_message", failure.Message);
        command.Parameters.AddWithValue("@failure_path", failure.Path is { } path ? path : DBNull.Value);
        command.Parameters.AddWithValue("@failure_retryable", failure.Retryable ? 1 : 0);
    }

    private static string FormatOperationId(Guid operationId) => operationId.ToString("D");
    private static DateTimeOffset NormalizeUtc(DateTimeOffset value) => value.ToUniversalTime();
    private static long ToUtcTicks(DateTimeOffset value) => NormalizeUtc(value).UtcDateTime.Ticks;
    private static long ToSqliteInteger(ulong value) => unchecked((long)value);

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SqliteFileOperationActionHistoryHardLinkEvidenceStore));
        }
    }
}
