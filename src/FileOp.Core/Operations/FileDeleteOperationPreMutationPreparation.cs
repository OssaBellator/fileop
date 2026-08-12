using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileOp.Core.Operations;

/// <summary>
/// Owns one provider-acquired read-only stability lease while the matching delete-history
/// snapshot remains only pre-mutation evidence. The scope exposes no handle or mutation primitive.
/// </summary>
public sealed class FileDeleteOperationPreMutationPreparationScope : IAsyncDisposable
{
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private IFileDeleteOperationStabilityLease? _lease;

    internal FileDeleteOperationPreMutationPreparationScope(
        FileDeleteOperationHistoryBindingEvidence bindingEvidence,
        IFileDeleteOperationStabilityLease lease)
    {
        ArgumentNullException.ThrowIfNull(bindingEvidence);
        ArgumentNullException.ThrowIfNull(lease);

        BindingEvidence = bindingEvidence;
        _lease = lease;
    }

    public FileDeleteOperationHistoryBindingEvidence BindingEvidence { get; }

    public FileDeleteOperationUserAuthorizationReceipt Authorization => BindingEvidence.Authorization;

    public FileDeleteOperationStabilityLeaseEvidence StabilityEvidence => BindingEvidence.StabilityEvidence;

    public FileDeleteOperationActionHistory HistorySnapshot => BindingEvidence.HistorySnapshot;

    public int Ordinal => BindingEvidence.Ordinal;

    public bool DeleteMutationAuthorized => false;

    public bool MutationBarrierSatisfied => false;

    public bool StabilityLeaseProviderAcquisitionObserved => true;

    public bool HistoryStoreReadObserved => true;

    public bool StabilityLeaseHeld => Volatile.Read(ref _lease) is not null;

    public bool IsDisposed => !StabilityLeaseHeld;

    public async ValueTask DisposeAsync()
    {
        await _disposeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var lease = Volatile.Read(ref _lease);
            if (lease is null)
            {
                return;
            }

            // Do not clear ownership until disposal succeeds. If an arbitrary lease
            // implementation throws, the scope must not falsely claim that its held
            // resource was released; a later caller may retry disposal.
            await lease.DisposeAsync().ConfigureAwait(false);
            Volatile.Write(ref _lease, null);
        }
        finally
        {
            _disposeGate.Release();
        }
    }
}

/// <summary>
/// Acquires and privately owns the existing read-only stability lease, then validates the
/// current history snapshot against that provider-returned evidence. This deliberately stops
/// before the durable Pending -> MutationStarted barrier and grants no delete authority.
/// </summary>
public static class FileDeleteOperationPreMutationPreparation
{
    public static async ValueTask<FileDeleteOperationPreMutationPreparationScope> PrepareAsync(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        int ordinal,
        IFileDeleteOperationStabilityLeaseProvider stabilityLeaseProvider,
        IFileDeleteOperationActionHistoryStore historyStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(stabilityLeaseProvider);
        ArgumentNullException.ThrowIfNull(historyStore);
        cancellationToken.ThrowIfCancellationRequested();

        var request = new FileDeleteOperationStabilityLeaseRequest(authorization, ordinal);
        var lease = await stabilityLeaseProvider
            .AcquireAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (lease is null)
        {
            throw new InvalidOperationException(
                "Delete pre-mutation preparation provider returned no stability lease.");
        }

        try
        {
            if (lease.DeleteMutationAuthorized)
            {
                throw new InvalidOperationException(
                    "Delete pre-mutation preparation cannot accept a mutation-authorizing stability lease.");
            }

            var stabilityEvidence = lease.Evidence
                ?? throw new InvalidOperationException(
                    "Delete pre-mutation preparation stability lease returned no evidence.");
            if (stabilityEvidence.DeleteMutationAuthorized ||
                !stabilityEvidence.IsBoundTo(authorization, ordinal))
            {
                throw new InvalidOperationException(
                    "Delete pre-mutation preparation requires provider evidence bound to the exact authorization receipt and ordinal.");
            }

            var history = await historyStore
                .GetAsync(authorization.PlanId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "Delete pre-mutation preparation requires an existing current action-history operation.");

            var binding = FileDeleteOperationHistoryBinding.Validate(
                authorization,
                stabilityEvidence,
                history,
                ordinal);
            if (binding.DeleteMutationAuthorized || binding.MutationBarrierSatisfied)
            {
                throw new InvalidOperationException(
                    "Delete pre-mutation preparation binding unexpectedly crossed the mutation boundary.");
            }

            return new FileDeleteOperationPreMutationPreparationScope(binding, lease);
        }
        catch (Exception preparationException)
        {
            try
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception disposalException)
            {
                throw new AggregateException(
                    "Delete pre-mutation preparation failed and releasing its read-only stability lease also failed.",
                    preparationException,
                    disposalException);
            }

            throw;
        }
    }
}
