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
/// Schema-v1 additive decorator whose stronger transitions atomically persist
/// destination identity, content SHA-256, hard-link count, basic metadata and the
/// owner/group/DACL security-descriptor digest. Legacy methods delegate unchanged.
/// </summary>
public sealed class SqliteFileOperationActionHistorySecurityDescriptorEvidenceStore :
    IFileOperationActionHistorySecurityDescriptorEvidenceStore,
    IDisposable
{
    private readonly SqliteFileOperationActionHistoryBasicMetadataEvidenceStore _inner;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _disposed;

    public SqliteFileOperationActionHistorySecurityDescriptorEvidenceStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        _inner = new SqliteFileOperationActionHistoryBasicMetadataEvidenceStore(fullPath);
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

    public ValueTask<FileOperationActionHistory> MarkMutationStartedAsync(
        Guid operationId,
        int ordinal,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default) =>
        _inner.MarkMutationStartedAsync(operationId, ordinal, startedAtUtc, cancellationToken);

    public ValueTask<FileOperationActionHistory> MarkEntryFailedBeforeMutationAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken = default) =>
        _inner.MarkEntryFailedBeforeMutationAsync(operationId, ordinal, failure, failedAtUtc, cancellationToken);

    public ValueTask<FileOperationActionHistory> CommitCopyAsync(
        Guid operationId,
        int ordinal,
        FileIdentity destinationIdentity,
        FileContentFingerprint destinationContentFingerprint,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default) =>
        _inner.CommitCopyAsync(
            operationId,
            ordinal,
            destinationIdentity,
            destinationContentFingerprint,
            committedAtUtc,
            cancellationToken);

    public ValueTask<FileOperationActionHistory> MarkMutationRecoveryRequiredAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken = default,
        FileIdentity? destinationIdentity = null,
        FileContentFingerprint? destinationContentFingerprint = null) =>
        _inner.MarkMutationRecoveryRequiredAsync(
            operationId,
            ordinal,
            failure,
            failedAtUtc,
            cancellationToken,
            destinationIdentity,
            destinationContentFingerprint);

    public ValueTask<FileOperationActionHistory> CommitCopyWithHardLinkEvidenceAsync(
        Guid operationId,
        int ordinal,
        FileIdentity destinationIdentity,
        FileContentFingerprint destinationContentFingerprint,
        uint destinationHardLinkCount,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default) =>
        _inner.CommitCopyWithHardLinkEvidenceAsync(
            operationId,
            ordinal,
            destinationIdentity,
            destinationContentFingerprint,
            destinationHardLinkCount,
            committedAtUtc,
            cancellationToken);

    public ValueTask<FileOperationActionHistory> MarkMutationRecoveryRequiredWithHardLinkEvidenceAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        DateTimeOffset failedAtUtc,
        FileIdentity destinationIdentity,
        FileContentFingerprint destinationContentFingerprint,
        uint destinationHardLinkCount,
        CancellationToken cancellationToken = default) =>
        _inner.MarkMutationRecoveryRequiredWithHardLinkEvidenceAsync(
            operationId,
            ordinal,
            failure,
            failedAtUtc,
            destinationIdentity,
            destinationContentFingerprint,
            destinationHardLinkCount,
            cancellationToken);

    public ValueTask<FileOperationActionHistory> CommitCopyWithBasicMetadataEvidenceAsync(
        Guid operationId,
        int ordinal,
        FileIdentity destinationIdentity,
        FileContentFingerprint destinationContentFingerprint,
        FileCopyDestinationCommitBasicMetadataEvidence destinationEvidence,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default) =>
        _inner.CommitCopyWithBasicMetadataEvidenceAsync(
            operationId,
            ordinal,
            destinationIdentity,
            destinationContentFingerprint,
            destinationEvidence,
            committedAtUtc,
            cancellationToken);

    public ValueTask<FileOperationActionHistory> MarkMutationRecoveryRequiredWithBasicMetadataEvidenceAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        DateTimeOffset failedAtUtc,
        FileIdentity destinationIdentity,
        FileContentFingerprint destinationContentFingerprint,
        FileCopyDestinationCommitBasicMetadataEvidence destinationEvidence,
        CancellationToken cancellationToken = default) =>
        _inner.MarkMutationRecoveryRequiredWithBasicMetadataEvidenceAsync(
            operationId,
            ordinal,
            failure,
            failedAtUtc,
            destinationIdentity,
            destinationContentFingerprint,
            destinationEvidence,
            cancellationToken);

    public async ValueTask<FileOperationActionHistory> CommitCopyWithSecurityDescriptorEvidenceAsync(
        Guid operationId,
        int ordinal,
        FileIdentity destinationIdentity,
        FileContentFingerprint destinationContentFingerprint,
        FileCopyDestinationCommitSecurityDescriptorEvidence destinationEvidence,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destinationContentFingerprint);
        ArgumentNullException.ThrowIfNull(destinationEvidence);
        ValidateOrdinal(ordinal);
        var operationKey = operationId.ToString("D");
        var committedAt = committedAtUtc.ToUniversalTime();

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = await LoadRequiredAsync(operationId, cancellationToken).ConfigureAwait(false);
            ValidateTransition(before, ordinal, FileOperationActionEntryState.MutationStarted);

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
            await PersistCombinedEvidenceAsync(
                connection,
                transaction,
                operationKey,
                ordinal,
                destinationContentFingerprint,
                destinationEvidence,
                cancellationToken).ConfigureAwait(false);
            transaction.Commit();

            return ApplyCommittedEvidence(
                before,
                ordinal,
                destinationIdentity,
                destinationContentFingerprint,
                destinationEvidence.HardLinkCount,
                committedAt);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<FileOperationActionHistory> MarkMutationRecoveryRequiredWithSecurityDescriptorEvidenceAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        DateTimeOffset failedAtUtc,
        FileIdentity destinationIdentity,
        FileContentFingerprint destinationContentFingerprint,
        FileCopyDestinationCommitSecurityDescriptorEvidence destinationEvidence,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateFailure(failure);
        ArgumentNullException.ThrowIfNull(destinationContentFingerprint);
        ArgumentNullException.ThrowIfNull(destinationEvidence);
        ValidateOrdinal(ordinal);
        var operationKey = operationId.ToString("D");
        var failedAt = failedAtUtc.ToUniversalTime();

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = await LoadRequiredAsync(operationId, cancellationToken).ConfigureAwait(false);
            ValidateTransition(before, ordinal, FileOperationActionEntryState.MutationStarted);

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
            await PersistCombinedEvidenceAsync(
                connection,
                transaction,
                operationKey,
                ordinal,
                destinationContentFingerprint,
                destinationEvidence,
                cancellationToken).ConfigureAwait(false);
            transaction.Commit();

            return ApplyRecoveryEvidence(
                before,
                ordinal,
                failure,
                destinationIdentity,
                destinationContentFingerprint,
                destinationEvidence.HardLinkCount,
                failedAt);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ValueTask<FileOperationActionHistory> CompleteAsync(
        Guid operationId,
        FileOperationActionTerminalState terminalState,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default) =>
        _inner.CompleteAsync(operationId, terminalState, completedAtUtc, cancellationToken);

    public ValueTask<FileOperationActionHistory?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        _inner.GetAsync(operationId, cancellationToken);

    public ValueTask<IReadOnlyList<FileOperationActionHistory>> GetRecentAsync(
        int limit = 100,
        CancellationToken cancellationToken = default) =>
        _inner.GetRecentAsync(limit, cancellationToken);

    public ValueTask<FileBasicMetadataEvidence?> GetDestinationBasicMetadataEvidenceAsync(
        Guid operationId,
        int ordinal,
        CancellationToken cancellationToken = default) =>
        _inner.GetDestinationBasicMetadataEvidenceAsync(operationId, ordinal, cancellationToken);

    public async ValueTask<FileSecurityDescriptorEvidence?> GetDestinationSecurityDescriptorEvidenceAsync(
        Guid operationId,
        int ordinal,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateOrdinal(ordinal);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT security.security_information, security.sha256_hex_digest
            FROM file_operation_action_entry_security_descriptor_evidence AS security
            JOIN file_operation_action_entries AS entry
              ON entry.operation_id = security.operation_id
             AND entry.ordinal = security.ordinal
            JOIN file_operation_action_entry_content_fingerprints AS fingerprint
              ON fingerprint.operation_id = security.operation_id
             AND fingerprint.ordinal = security.ordinal
            JOIN file_operation_action_entry_hard_link_evidence AS hard_link
              ON hard_link.operation_id = security.operation_id
             AND hard_link.ordinal = security.ordinal
            JOIN file_operation_action_entry_basic_metadata_evidence AS metadata
              ON metadata.operation_id = security.operation_id
             AND metadata.ordinal = security.ordinal
            WHERE security.operation_id = @operation_id
              AND security.ordinal = @ordinal
              AND entry.state IN (@committed, @recovery_required)
              AND entry.destination_volume_serial IS NOT NULL
              AND entry.destination_file_reference IS NOT NULL;
            """;
        command.Parameters.AddWithValue("@operation_id", operationId.ToString("D"));
        command.Parameters.AddWithValue("@ordinal", ordinal);
        command.Parameters.AddWithValue("@committed", (int)FileOperationActionEntryState.Committed);
        command.Parameters.AddWithValue("@recovery_required", (int)FileOperationActionEntryState.RecoveryRequired);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var information = reader.GetInt64(0);
        if (information < 0 || information > uint.MaxValue)
        {
            throw new InvalidDataException("Persisted security-information mask is outside the UInt32 range.");
        }

        var evidence = new FileSecurityDescriptorEvidence(
            checked((uint)information),
            reader.GetString(1));
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("Persisted security-descriptor evidence is duplicated.");
        }

        return evidence;
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
            CREATE TABLE IF NOT EXISTS file_operation_action_entry_security_descriptor_evidence(
                operation_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL CHECK(ordinal >= 0),
                security_information INTEGER NOT NULL CHECK(security_information = 7),
                sha256_hex_digest TEXT NOT NULL CHECK(length(sha256_hex_digest) = 64),
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

    private async ValueTask<FileOperationActionHistory> LoadRequiredAsync(
        Guid operationId,
        CancellationToken cancellationToken) =>
        await _inner.GetAsync(operationId, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException($"Action history for operation {operationId} does not exist.");

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
        command.Parameters.AddWithValue("@completed_utc_ticks", committedAt.UtcDateTime.Ticks);
        command.Parameters.AddWithValue("@destination_volume_serial", unchecked((long)destinationIdentity.VolumeSerialNumber));
        command.Parameters.AddWithValue("@destination_file_reference", unchecked((long)destinationIdentity.FileReferenceNumber));
        command.Parameters.AddWithValue("@undo_kind", (int)FileOperationUndoKind.DeleteCreatedDestination);
        command.Parameters.AddWithValue("@operation_id", operationKey);
        command.Parameters.AddWithValue("@ordinal", ordinal);
        command.Parameters.AddWithValue("@mutation_started", (int)FileOperationActionEntryState.MutationStarted);
        command.Parameters.AddWithValue("@copy_kind", (int)FileOperationKind.Copy);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("A security-aware Copy entry can be committed only once after MutationStarted while its operation remains active.");
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
        command.Parameters.AddWithValue("@completed_utc_ticks", failedAt.UtcDateTime.Ticks);
        command.Parameters.AddWithValue("@destination_volume_serial", unchecked((long)destinationIdentity.VolumeSerialNumber));
        command.Parameters.AddWithValue("@destination_file_reference", unchecked((long)destinationIdentity.FileReferenceNumber));
        command.Parameters.AddWithValue("@undo_kind", (int)FileOperationUndoKind.None);
        command.Parameters.AddWithValue("@failure_code", failure.Code);
        command.Parameters.AddWithValue("@failure_message", failure.Message);
        command.Parameters.AddWithValue("@failure_path", failure.Path is { } path ? path : DBNull.Value);
        command.Parameters.AddWithValue("@failure_retryable", failure.Retryable ? 1 : 0);
        command.Parameters.AddWithValue("@operation_id", operationKey);
        command.Parameters.AddWithValue("@ordinal", ordinal);
        command.Parameters.AddWithValue("@mutation_started", (int)FileOperationActionEntryState.MutationStarted);
        command.Parameters.AddWithValue("@copy_kind", (int)FileOperationKind.Copy);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("A security-aware Copy entry can require recovery only after MutationStarted while its operation remains active.");
        }
    }

    private static async ValueTask PersistCombinedEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationKey,
        int ordinal,
        FileContentFingerprint fingerprint,
        FileCopyDestinationCommitSecurityDescriptorEvidence evidence,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO file_operation_action_entry_content_fingerprints(
                operation_id, ordinal, algorithm, digest_hex)
            VALUES(@operation_id, @ordinal, @algorithm, @content_digest);

            INSERT INTO file_operation_action_entry_hard_link_evidence(
                operation_id, ordinal, hard_link_count)
            VALUES(@operation_id, @ordinal, @hard_link_count);

            INSERT INTO file_operation_action_entry_basic_metadata_evidence(
                operation_id, ordinal, creation_time_filetime, last_access_time_filetime,
                last_write_time_filetime, file_attributes)
            VALUES(@operation_id, @ordinal, @creation_time, @last_access_time,
                @last_write_time, @file_attributes);

            INSERT INTO file_operation_action_entry_security_descriptor_evidence(
                operation_id, ordinal, security_information, sha256_hex_digest)
            VALUES(@operation_id, @ordinal, @security_information, @security_digest);
            """;
        command.Parameters.AddWithValue("@operation_id", operationKey);
        command.Parameters.AddWithValue("@ordinal", ordinal);
        command.Parameters.AddWithValue("@algorithm", (int)fingerprint.Algorithm);
        command.Parameters.AddWithValue("@content_digest", fingerprint.HexDigest);
        command.Parameters.AddWithValue("@hard_link_count", (long)evidence.HardLinkCount);
        command.Parameters.AddWithValue("@creation_time", unchecked((long)evidence.BasicMetadata.CreationTimeFileTime));
        command.Parameters.AddWithValue("@last_access_time", unchecked((long)evidence.BasicMetadata.LastAccessTimeFileTime));
        command.Parameters.AddWithValue("@last_write_time", unchecked((long)evidence.BasicMetadata.LastWriteTimeFileTime));
        command.Parameters.AddWithValue("@file_attributes", (long)evidence.BasicMetadata.FileAttributes);
        command.Parameters.AddWithValue("@security_information", (long)evidence.SecurityDescriptor.SecurityInformation);
        command.Parameters.AddWithValue("@security_digest", evidence.SecurityDescriptor.Sha256HexDigest);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 4)
        {
            throw new InvalidOperationException("Combined Copy recovery evidence persistence did not write exactly four rows.");
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

    private static FileOperationActionHistory CloneHistory(
        FileOperationActionHistory history,
        IReadOnlyList<FileOperationActionEntry> entries) =>
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

    private static void ValidateTransition(
        FileOperationActionHistory history,
        int ordinal,
        FileOperationActionEntryState expectedState)
    {
        if (history.Kind != FileOperationKind.Copy ||
            history.TerminalState.HasValue ||
            ordinal >= history.Entries.Count ||
            history.Entries[ordinal].Ordinal != ordinal ||
            history.Entries[ordinal].State != expectedState)
        {
            throw new InvalidOperationException(
                $"Entry {ordinal} is not in the expected active Copy state {expectedState}.");
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
            throw new ObjectDisposedException(nameof(SqliteFileOperationActionHistorySecurityDescriptorEvidenceStore));
        }
    }
}
