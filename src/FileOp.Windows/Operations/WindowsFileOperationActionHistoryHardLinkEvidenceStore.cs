using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;

namespace FileOp.Windows.Operations;

/// <summary>
/// Adapts the existing Copy executor's action-history calls to the stronger
/// topology-aware store. Commit-time hard-link evidence is collected while the
/// executor still holds the mutation lease; failed durable commit can reuse the
/// exact observation for the immediately following recovery transition.
/// </summary>
public sealed class WindowsFileOperationActionHistoryHardLinkEvidenceStore :
    IFileOperationActionHistoryHardLinkEvidenceStore
{
    private readonly IFileOperationActionHistoryHardLinkEvidenceStore _inner;
    private readonly IFileCopyDestinationHardLinkEvidenceSource _evidenceSource;
    private readonly object _gate = new();
    private readonly Dictionary<(Guid OperationId, int Ordinal), PendingEvidence> _pending = new();

    public WindowsFileOperationActionHistoryHardLinkEvidenceStore(
        IFileOperationActionHistoryHardLinkEvidenceStore inner,
        IFileCopyDestinationHardLinkEvidenceSource evidenceSource)
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
        var hardLinkCount = await _evidenceSource
            .ReadVerifiedHardLinkCountAsync(history, ordinal, destinationIdentity, cancellationToken)
            .ConfigureAwait(false);
        if (hardLinkCount == 0)
        {
            throw new InvalidOperationException("Verified destination hard-link evidence must be positive.");
        }

        var key = (operationId, ordinal);
        var evidence = new PendingEvidence(destinationIdentity, destinationContentFingerprint, hardLinkCount);
        lock (_gate)
        {
            _pending[key] = evidence;
        }

        try
        {
            var committed = await _inner.CommitCopyWithHardLinkEvidenceAsync(
                    operationId,
                    ordinal,
                    destinationIdentity,
                    destinationContentFingerprint,
                    hardLinkCount,
                    committedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            RemovePending(key);
            return committed;
        }
        catch
        {
            // Preserve the exact observation for the executor's immediate
            // validated-receipt / failed-commit recovery transition.
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
            // A validated receipt exists, but topology evidence did not survive
            // this exact commit attempt. Persist recovery without partial proof.
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
            var recovered = await _inner.MarkMutationRecoveryRequiredWithHardLinkEvidenceAsync(
                    operationId,
                    ordinal,
                    failure,
                    failedAtUtc,
                    identity,
                    fingerprint,
                    pending.HardLinkCount,
                    cancellationToken)
                .ConfigureAwait(false);
            RemovePending(key);
            return recovered;
        }
        catch
        {
            // Keep the memory-only observation available for a caller retry. A
            // process crash still leaves only the durable MutationStarted state.
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
        uint HardLinkCount);
}
