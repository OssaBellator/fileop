using System;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Operations;

namespace FileOp.Windows.Operations;

/// <summary>
/// Injectable evidence source for the destructive cross-volume Move fidelity gate.
/// Implementations return evidence classification only; they never mint delete authority.
/// </summary>
internal interface IFileCrossVolumeMoveFidelityVerifier
{
    ValueTask<FileCrossVolumeMoveFidelityClassification> VerifyAsync(
        FileCrossVolumeMoveSourceDeleteRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Adds a destructive-fidelity gate around the reviewed identity-bound source-delete
/// primitive. The inner lease is acquired first, so its exact source/destination handles
/// already deny ordinary write/delete sharing while this wrapper reads current evidence.
/// The same evidence is checked again after Core's durable source-delete barrier and before
/// the inner primitive receives mutation authority. Evidence never grants delete authority.
/// </summary>
public sealed class WindowsFidelityVerifiedFileCrossVolumeMoveSourceDeletePrimitive :
    IFileCrossVolumeMoveSourceDeletePrimitive
{
    private readonly IFileCrossVolumeMoveSourceDeletePrimitive _inner;
    private readonly IFileCrossVolumeMoveFidelityVerifier _fidelityVerifier;

    public WindowsFidelityVerifiedFileCrossVolumeMoveSourceDeletePrimitive()
        : this(
            new WindowsFileCrossVolumeMoveSourceDeletePrimitive(),
            new WindowsFileCrossVolumeMoveFidelityVerifier())
    {
    }

    internal WindowsFidelityVerifiedFileCrossVolumeMoveSourceDeletePrimitive(
        IFileCrossVolumeMoveSourceDeletePrimitive inner,
        IFileCrossVolumeMoveFidelityVerifier fidelityVerifier)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _fidelityVerifier = fidelityVerifier ?? throw new ArgumentNullException(nameof(fidelityVerifier));
    }

    public async ValueTask<IFileCrossVolumeMoveSourceDeleteLease> AcquireAsync(
        FileCrossVolumeMoveSourceDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        IFileCrossVolumeMoveSourceDeleteLease? innerLease =
            await _inner.AcquireAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            var classification = await _fidelityVerifier
                .VerifyAsync(request, cancellationToken)
                .ConfigureAwait(false);
            ThrowIfBlocked(classification, "before the source-delete barrier");

            var lease = new FidelityLease(innerLease, request, _fidelityVerifier);
            innerLease = null;
            return lease;
        }
        finally
        {
            if (innerLease is not null)
            {
                await innerLease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static void ThrowIfBlocked(
        FileCrossVolumeMoveFidelityClassification classification,
        string phase)
    {
        ArgumentNullException.ThrowIfNull(classification);
        if (classification.CanDeleteSourceAfterDurableBarrier)
        {
            return;
        }

        throw new NotSupportedException(
            $"Cross-volume Move retained the source because destructive fidelity proof failed {phase}. " +
            $"Blocking evidence: {string.Join(", ", classification.Blockers)}. " +
            "The committed destination remains a Copy result and grants no source-delete authority.");
    }

    private sealed class FidelityLease : IFileCrossVolumeMoveSourceDeleteLease
    {
        private IFileCrossVolumeMoveSourceDeleteLease? _inner;
        private readonly FileCrossVolumeMoveSourceDeleteRequest _request;
        private readonly IFileCrossVolumeMoveFidelityVerifier _fidelityVerifier;
        private int _mutationAttempted;

        internal FidelityLease(
            IFileCrossVolumeMoveSourceDeleteLease inner,
            FileCrossVolumeMoveSourceDeleteRequest request,
            IFileCrossVolumeMoveFidelityVerifier fidelityVerifier)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _request = request ?? throw new ArgumentNullException(nameof(request));
            _fidelityVerifier = fidelityVerifier ??
                throw new ArgumentNullException(nameof(fidelityVerifier));
            if (!ReferenceEquals(inner.Evidence.Request, request))
            {
                throw new InvalidOperationException(
                    "The fidelity wrapper requires the exact source-delete request bound to the inner live lease.");
            }
        }

        public FileCrossVolumeMoveSourceDeleteEvidence Evidence =>
            Volatile.Read(ref _inner)?.Evidence ??
            throw new ObjectDisposedException(nameof(FidelityLease));

        public bool DeleteAccessCapabilityHeld =>
            Volatile.Read(ref _inner)?.DeleteAccessCapabilityHeld == true;

        public bool SourceDeleteMutationPerformed =>
            Volatile.Read(ref _inner)?.SourceDeleteMutationPerformed == true;

        public async ValueTask MarkDeletePendingAsync(
            FileCrossVolumeMoveSourceDeleteAuthorization authorization,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(authorization);
            cancellationToken.ThrowIfCancellationRequested();
            var inner = Volatile.Read(ref _inner)
                ?? throw new ObjectDisposedException(nameof(FidelityLease));
            if (!authorization.SourceDeleteBarrierSatisfied ||
                !authorization.SourceDeleteMutationAuthorized ||
                !authorization.IsBoundTo(inner.Evidence))
            {
                throw new UnauthorizedAccessException(
                    "Cross-volume Move fidelity verification cannot substitute for the exact Core-minted post-barrier source-delete authority.");
            }

            if (Interlocked.CompareExchange(ref _mutationAttempted, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "Cross-volume Move source deletion is single-use even when fidelity revalidation refuses mutation.");
            }

            // This is already inside the durable source-delete critical section. Do not turn
            // a late cancellation into a partially-authorized destructive retry. Revalidate
            // to a terminal proof/refusal, then let the inner reviewed primitive perform the
            // exact same-handle disposition without a cancellable gap.
            var classification = await _fidelityVerifier
                .VerifyAsync(_request, CancellationToken.None)
                .ConfigureAwait(false);
            ThrowIfBlocked(classification, "after the durable source-delete barrier");

            await inner.MarkDeletePendingAsync(authorization, CancellationToken.None)
                .ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            var inner = Interlocked.Exchange(ref _inner, null);
            if (inner is not null)
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
