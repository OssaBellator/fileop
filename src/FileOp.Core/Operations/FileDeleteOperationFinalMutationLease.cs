using System;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;

namespace FileOp.Core.Operations;

/// <summary>
/// Requests one final provider reacquisition for the exact session-only delete authorization
/// after the earlier read-only preparation lease has been successfully released.
/// Only Core can construct this request; callers cannot mint or recover one from value evidence.
/// </summary>
public sealed class FileDeleteOperationFinalMutationLeaseRequest
{
    internal FileDeleteOperationFinalMutationLeaseRequest(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        int ordinal)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (!authorization.UserAuthorizedAttempt || authorization.DeleteMutationAuthorized)
        {
            throw new ArgumentException(
                "Final delete leasing requires an explicit session-only user authorization that remains non-authorizing for mutation.",
                nameof(authorization));
        }
        if (ordinal < 0 || ordinal >= authorization.Items.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }
        if (authorization.Items.Count != authorization.Plan.Intent.Entries.Count)
        {
            throw new ArgumentException(
                "Final delete leasing requires complete ordered authorization evidence for the captured plan.",
                nameof(authorization));
        }

        var authorizedItem = authorization.Items[ordinal];
        var plannedEntry = authorization.Plan.Intent.Entries[ordinal];
        if (authorizedItem.Entry != plannedEntry || plannedEntry.IsDirectory)
        {
            throw new ArgumentException(
                "Final delete leasing requires the exact authorized file entry in captured plan order.",
                nameof(authorization));
        }

        Authorization = authorization;
        Ordinal = ordinal;
        AuthorizedItem = authorizedItem;
    }

    public FileDeleteOperationUserAuthorizationReceipt Authorization { get; }

    public int Ordinal { get; }

    public FileDeleteOperationUserAuthorizationItem AuthorizedItem { get; }

    public FileOperationEntry Entry => AuthorizedItem.Entry;

    public bool DeleteMutationAuthorized => false;

    public bool PriorReadOnlyLeaseReleaseProven => false;
}

/// <summary>
/// Value-only final namespace/identity evidence. Construction validates exact authorization
/// provenance but deliberately does not claim that a provider acquired a DELETE-capable handle
/// or that any such live lease remains held. The coordinator-only request is not exposed.
/// </summary>
public sealed class FileDeleteOperationFinalMutationLeaseEvidence
{
    private readonly FileDeleteOperationFinalMutationLeaseRequest _request;

    public FileDeleteOperationFinalMutationLeaseEvidence(
        FileDeleteOperationFinalMutationLeaseRequest request,
        string canonicalSourceDirectoryPath,
        FileIdentity sourceDirectoryIdentity,
        string canonicalSourcePath,
        FileIdentity sourceIdentity)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalSourceDirectoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalSourcePath);

        if (request.DeleteMutationAuthorized || request.Authorization.DeleteMutationAuthorized)
        {
            throw new ArgumentException(
                "Final delete lease evidence cannot be created from mutation-authorizing input.",
                nameof(request));
        }
        if (!PathEquals(canonicalSourceDirectoryPath, request.Authorization.CanonicalSourceDirectoryPath) ||
            sourceDirectoryIdentity != request.Authorization.SourceDirectoryIdentity)
        {
            throw new ArgumentException(
                "Final delete root evidence must exactly match the root identity reviewed by the user.",
                nameof(request));
        }
        if (!PathEquals(canonicalSourcePath, request.AuthorizedItem.CanonicalPath) ||
            sourceIdentity != request.AuthorizedItem.Identity)
        {
            throw new ArgumentException(
                "Final delete file evidence must exactly match the file identity reviewed by the user.",
                nameof(request));
        }

        _request = request;
        CanonicalSourceDirectoryPath = canonicalSourceDirectoryPath;
        SourceDirectoryIdentity = sourceDirectoryIdentity;
        CanonicalSourcePath = canonicalSourcePath;
        SourceIdentity = sourceIdentity;
    }

    public FileDeleteOperationUserAuthorizationReceipt Authorization => _request.Authorization;

    public int Ordinal => _request.Ordinal;

    public FileOperationEntry Entry => _request.Entry;

    public string CanonicalSourceDirectoryPath { get; }

    public FileIdentity SourceDirectoryIdentity { get; }

    public string CanonicalSourcePath { get; }

    public FileIdentity SourceIdentity { get; }

    public bool DeleteMutationAuthorized => false;

    public bool ProviderAcquisitionProven => false;

    public bool DeleteAccessCapabilityProven => false;

    public bool LeaseLivenessProven => false;

    public bool IsBoundTo(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        int ordinal) =>
        ReferenceEquals(Authorization, authorization) && Ordinal == ordinal;

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Provider-owned final capability for one exact authorized file. A future destructive primitive
/// must consume this same live lease rather than reopen the pathname. This contract exposes no
/// raw operating-system handle and does not itself authorize or perform mutation.
/// </summary>
public interface IFileDeleteOperationFinalMutationLease : IAsyncDisposable
{
    FileDeleteOperationFinalMutationLeaseEvidence Evidence { get; }

    bool DeleteAccessCapabilityHeld { get; }

    bool DeleteMutationAuthorized { get; }
}

public interface IFileDeleteOperationFinalMutationLeaseProvider
{
    ValueTask<IFileDeleteOperationFinalMutationLease> AcquireAsync(
        FileDeleteOperationFinalMutationLeaseRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns one provider-acquired final delete-capable lease after Core observed successful release
/// of the earlier #130 read-only preparation lease. The scope remains pre-barrier and exposes no
/// mutation primitive, raw handle, underlying lease, or reusable final-acquisition request.
/// </summary>
public sealed class FileDeleteOperationFinalMutationLeaseScope : IAsyncDisposable
{
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private IFileDeleteOperationFinalMutationLease? _lease;

    internal FileDeleteOperationFinalMutationLeaseScope(
        FileDeleteOperationHistoryBindingEvidence priorBindingEvidence,
        FileDeleteOperationFinalMutationLeaseEvidence finalEvidence,
        IFileDeleteOperationFinalMutationLease lease)
    {
        ArgumentNullException.ThrowIfNull(priorBindingEvidence);
        ArgumentNullException.ThrowIfNull(finalEvidence);
        ArgumentNullException.ThrowIfNull(lease);

        PriorBindingEvidence = priorBindingEvidence;
        FinalEvidence = finalEvidence;
        _lease = lease;
    }

    public FileDeleteOperationHistoryBindingEvidence PriorBindingEvidence { get; }

    public FileDeleteOperationFinalMutationLeaseEvidence FinalEvidence { get; }

    public FileDeleteOperationUserAuthorizationReceipt Authorization => PriorBindingEvidence.Authorization;

    public int Ordinal => PriorBindingEvidence.Ordinal;

    public bool PriorReadOnlyLeaseReleaseObserved => true;

    public bool FinalLeaseProviderAcquisitionObserved => true;

    public bool DeleteMutationAuthorized => false;

    public bool MutationBarrierSatisfied => false;

    public bool DeleteMutationPerformed => false;

    public bool FinalLeaseHeld => Volatile.Read(ref _lease) is not null;

    public bool DeleteAccessCapabilityHeld
    {
        get
        {
            var lease = Volatile.Read(ref _lease);
            return lease is not null && lease.DeleteAccessCapabilityHeld;
        }
    }

    /// <summary>
    /// Transfers the privately held final capability to the next Core-owned lifecycle scope.
    /// This uses the same gate as disposal, so a retained alias can either dispose first or
    /// become inert after transfer; it can never race into a second owner of the same lease.
    /// </summary>
    internal async ValueTask<IFileDeleteOperationFinalMutationLease> DetachLeaseAsync()
    {
        await _disposeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var lease = Volatile.Read(ref _lease)
                ?? throw new InvalidOperationException(
                    "Final delete lease ownership is no longer available for transfer.");
            if (lease.DeleteMutationAuthorized ||
                !lease.DeleteAccessCapabilityHeld ||
                !ReferenceEquals(lease.Evidence, FinalEvidence))
            {
                throw new InvalidOperationException(
                    "Final delete lease ownership can transfer only while the exact accepted capability remains live and non-authorizing.");
            }

            Volatile.Write(ref _lease, null);
            return lease;
        }
        finally
        {
            _disposeGate.Release();
        }
    }

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
/// Releases the exact provider-acquired read-only preparation lease before asking a final
/// provider to reacquire/revalidate the same authorized root/file under its final capability.
/// This deliberately stops before the durable Pending -> MutationStarted barrier.
/// </summary>
public static class FileDeleteOperationFinalMutationLeasePreparation
{
    public static async ValueTask<FileDeleteOperationFinalMutationLeaseScope> AcquireAsync(
        FileDeleteOperationPreMutationPreparationScope readOnlyPreparationScope,
        IFileDeleteOperationFinalMutationLeaseProvider finalLeaseProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readOnlyPreparationScope);
        ArgumentNullException.ThrowIfNull(finalLeaseProvider);
        cancellationToken.ThrowIfCancellationRequested();

        if (readOnlyPreparationScope.DeleteMutationAuthorized ||
            readOnlyPreparationScope.MutationBarrierSatisfied ||
            !readOnlyPreparationScope.StabilityLeaseProviderAcquisitionObserved ||
            !readOnlyPreparationScope.HistoryStoreReadObserved ||
            !readOnlyPreparationScope.StabilityLeaseHeld)
        {
            throw new InvalidOperationException(
                "Final delete leasing requires one live, provider-acquired read-only preparation scope that remains before the mutation barrier.");
        }

        var priorBinding = readOnlyPreparationScope.BindingEvidence;
        var authorization = priorBinding.Authorization;
        var ordinal = priorBinding.Ordinal;

        // The #130 scope retains ownership if disposal fails. Do not invoke the final provider
        // unless Core observes successful release of that exact earlier provider-acquired lease.
        await readOnlyPreparationScope.DisposeAsync().ConfigureAwait(false);
        if (readOnlyPreparationScope.StabilityLeaseHeld || !readOnlyPreparationScope.IsDisposed)
        {
            throw new InvalidOperationException(
                "Final delete leasing cannot reacquire while the earlier read-only stability lease remains held.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var request = new FileDeleteOperationFinalMutationLeaseRequest(authorization, ordinal);
        var lease = await finalLeaseProvider
            .AcquireAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (lease is null)
        {
            throw new InvalidOperationException(
                "Final delete lease provider returned no lease after read-only release.");
        }

        try
        {
            if (lease.DeleteMutationAuthorized || !lease.DeleteAccessCapabilityHeld)
            {
                throw new InvalidOperationException(
                    "Final delete lease must hold its delete-access capability without claiming mutation authorization.");
            }

            var evidence = lease.Evidence
                ?? throw new InvalidOperationException(
                    "Final delete lease returned no identity evidence.");
            if (evidence.DeleteMutationAuthorized ||
                evidence.ProviderAcquisitionProven ||
                evidence.DeleteAccessCapabilityProven ||
                evidence.LeaseLivenessProven ||
                !evidence.IsBoundTo(authorization, ordinal))
            {
                throw new InvalidOperationException(
                    "Final delete lease evidence must remain value-only and bound to the exact authorization receipt and ordinal.");
            }

            if (!PathEquals(evidence.CanonicalSourceDirectoryPath, authorization.CanonicalSourceDirectoryPath) ||
                evidence.SourceDirectoryIdentity != authorization.SourceDirectoryIdentity ||
                !PathEquals(evidence.CanonicalSourcePath, authorization.Items[ordinal].CanonicalPath) ||
                evidence.SourceIdentity != authorization.Items[ordinal].Identity)
            {
                throw new InvalidOperationException(
                    "Final delete lease provider evidence does not match the exact authorized root/file identity.");
            }

            return new FileDeleteOperationFinalMutationLeaseScope(priorBinding, evidence, lease);
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
                    "Final delete lease preparation failed and releasing its final capability also failed.",
                    preparationException,
                    disposalException);
            }

            throw;
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
