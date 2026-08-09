using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;

namespace FileOp.Windows.Operations;

/// <summary>
/// Metadata-aware composition for the unchanged Copy executor. One verified
/// commit-bound observation carries both hard-link count and basic metadata.
/// </summary>
public sealed class WindowsFileOperationActionHistoryBasicMetadataEvidenceStore :
    IFileOperationActionHistoryBasicMetadataEvidenceStore
{
    private readonly IFileOperationActionHistoryBasicMetadataEvidenceStore _inner;
    private readonly IFileCopyDestinationCommitBasicMetadataEvidenceSource _evidenceSource;
    private readonly object _gate = new();
    private readonly Dictionary<(Guid OperationId, int Ordinal), PendingEvidence> _pending = new();

    public WindowsFileOperationActionHistoryBasicMetadataEvidenceStore(
        IFileOperationActionHistoryBasicMetadataEvidenceStore inner,
        IFileCopyDestinationCommitBasicMetadataEvidenceSource evidenceSource)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _evidenceSource = evidenceSource ?? throw new ArgumentNullException(nameof(evidenceSource));
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

    public async ValueTask<FileOperationActionHistory> CommitCopyAsync(
        Guid operationId,
        int ordinal,
        FileIdentity destinationIdentity,
        FileContentFingerprint destinationContentFingerprint,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destinationContentFingerprint);
        var history = await _inner.GetAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Action history for operation {operationId} does not exist.");
        var evidence = await _evidenceSource
            .ReadVerifiedEvidenceAsync(history, ordinal, destinationIdentity, cancellationToken)
            .ConfigureAwait(false);

        var key = (operationId, ordinal);
        var pending = new PendingEvidence(destinationIdentity, destinationContentFingerprint, evidence);
        lock (_gate)
        {
            _pending[key] = pending;
        }

        try
        {
            var committed = await _inner.CommitCopyWithBasicMetadataEvidenceAsync(
                    operationId,
                    ordinal,
                    destinationIdentity,
                    destinationContentFingerprint,
                    evidence,
                    committedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            RemovePending(key);
            return committed;
        }
        catch
        {
            throw;
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
        if (destinationIdentity.HasValue != (destinationContentFingerprint is not null))
        {
            throw new ArgumentException(
                "Recovery destination identity and content fingerprint must be supplied together or omitted together.");
        }

        var key = (operationId, ordinal);
        if (destinationIdentity is not FileIdentity identity ||
            destinationContentFingerprint is not FileContentFingerprint fingerprint)
        {
            RemovePending(key);
            return await _inner.MarkMutationRecoveryRequiredAsync(
                    operationId,
                    ordinal,
                    failure,
                    failedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        PendingEvidence? pending;
        lock (_gate)
        {
            _pending.TryGetValue(key, out pending);
        }

        if (pending is null ||
            pending.DestinationIdentity != identity ||
            pending.DestinationContentFingerprint != fingerprint)
        {
            RemovePending(key);
            return await _inner.MarkMutationRecoveryRequiredAsync(
                    operationId,
                    ordinal,
                    failure,
                    failedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            var recovered = await _inner.MarkMutationRecoveryRequiredWithBasicMetadataEvidenceAsync(
                    operationId,
                    ordinal,
                    failure,
                    failedAtUtc,
                    identity,
                    fingerprint,
                    pending.Evidence,
                    cancellationToken)
                .ConfigureAwait(false);
            RemovePending(key);
            return recovered;
        }
        catch
        {
            // Keep the in-memory observation available for an in-process retry.
            throw;
        }
    }

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

    public async ValueTask<FileOperationActionHistory> CompleteAsync(
        Guid operationId,
        FileOperationActionTerminalState terminalState,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var completed = await _inner.CompleteAsync(operationId, terminalState, completedAtUtc, cancellationToken)
            .ConfigureAwait(false);
        ClearPending(operationId);
        return completed;
    }

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

    private void RemovePending((Guid OperationId, int Ordinal) key)
    {
        lock (_gate)
        {
            _pending.Remove(key);
        }
    }

    private void ClearPending(Guid operationId)
    {
        lock (_gate)
        {
            foreach (var key in _pending.Keys.Where(key => key.OperationId == operationId).ToArray())
            {
                _pending.Remove(key);
            }
        }
    }

    private sealed record PendingEvidence(
        FileIdentity DestinationIdentity,
        FileContentFingerprint DestinationContentFingerprint,
        FileCopyDestinationCommitBasicMetadataEvidence Evidence);
}
