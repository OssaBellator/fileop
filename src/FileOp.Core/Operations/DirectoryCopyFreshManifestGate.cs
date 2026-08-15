using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileOp.Core.Operations;

public enum DirectoryCopyFreshManifestGateStatus
{
    ReadyForDurableHistory,
    AcquisitionUnsupported,
    AcquisitionUnavailable,
    ReviewedTreeChanged,
}

/// <summary>
/// Immutable evidence that a reviewed directory manifest was freshly reacquired and compared.
/// A ReadyForDurableHistory result authorizes only creation of separate durable action history;
/// it is not filesystem mutation authority.
/// </summary>
public sealed record DirectoryCopyFreshManifestGateResult
{
    public DirectoryCopyFreshManifestGateResult(
        DirectoryCopyFreshManifestGateStatus status,
        DirectoryOperationTreeManifest reviewedManifest,
        DirectoryOperationTreeManifestAcquisitionResult acquisition,
        DirectoryOperationTreeManifestRevalidationResult? revalidation,
        string summary)
    {
        ArgumentNullException.ThrowIfNull(reviewedManifest);
        ArgumentNullException.ThrowIfNull(acquisition);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);

        var readyAcquisition =
            acquisition.Status == DirectoryOperationTreeManifestAcquisitionStatus.Ready &&
            acquisition.Manifest is not null &&
            acquisition.CanRevalidateReviewedManifest;
        var boundRevalidation =
            revalidation is not null &&
            ReferenceEquals(revalidation.Initial, reviewedManifest) &&
            ReferenceEquals(revalidation.Fresh, acquisition.Manifest);

        switch (status)
        {
            case DirectoryCopyFreshManifestGateStatus.ReadyForDurableHistory:
                if (!readyAcquisition || !boundRevalidation || revalidation!.EvidenceStillMatches != true)
                {
                    throw new ArgumentException(
                        "Ready directory Copy gate evidence must bind the exact reviewed manifest to the exact freshly acquired matching manifest.",
                        nameof(revalidation));
                }
                break;
            case DirectoryCopyFreshManifestGateStatus.ReviewedTreeChanged:
                if (!readyAcquisition || !boundRevalidation || revalidation!.EvidenceStillMatches)
                {
                    throw new ArgumentException(
                        "Changed directory Copy gate evidence must bind a non-matching revalidation of the exact reviewed/fresh manifests.",
                        nameof(revalidation));
                }
                break;
            case DirectoryCopyFreshManifestGateStatus.AcquisitionUnsupported:
                if (acquisition.Status != DirectoryOperationTreeManifestAcquisitionStatus.Unsupported || revalidation is not null)
                {
                    throw new ArgumentException(
                        "Unsupported directory Copy gate evidence must come directly from unsupported acquisition without revalidation.",
                        nameof(acquisition));
                }
                break;
            case DirectoryCopyFreshManifestGateStatus.AcquisitionUnavailable:
                if (acquisition.Status == DirectoryOperationTreeManifestAcquisitionStatus.Ready || revalidation is not null)
                {
                    throw new ArgumentException(
                        "Unavailable directory Copy gate evidence must not publish Ready acquisition or revalidation evidence.",
                        nameof(acquisition));
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown directory Copy gate status.");
        }

        Status = status;
        ReviewedManifest = reviewedManifest;
        Acquisition = acquisition;
        Revalidation = revalidation;
        Summary = summary;
    }

    public DirectoryCopyFreshManifestGateStatus Status { get; }

    public DirectoryOperationTreeManifest ReviewedManifest { get; }

    public DirectoryOperationTreeManifestAcquisitionResult Acquisition { get; }

    public DirectoryOperationTreeManifestRevalidationResult? Revalidation { get; }

    public string Summary { get; }

    public bool CanBeginDurableHistory =>
        Status == DirectoryCopyFreshManifestGateStatus.ReadyForDurableHistory &&
        Acquisition.CanRevalidateReviewedManifest &&
        Revalidation?.EvidenceStillMatches == true &&
        ReferenceEquals(Revalidation.Initial, ReviewedManifest) &&
        ReferenceEquals(Revalidation.Fresh, Acquisition.Manifest);

    public bool GrantsMutationAuthority => false;

    public bool GrantsCopyAuthority => false;

    public bool GrantsCreateAuthority => false;

    public bool GrantsDeleteAuthority => false;
}

public sealed class DirectoryCopyFreshManifestGate
{
    private readonly IDirectoryOperationTreeManifestAcquirer _acquirer;

    public DirectoryCopyFreshManifestGate(IDirectoryOperationTreeManifestAcquirer acquirer)
    {
        _acquirer = acquirer ?? throw new ArgumentNullException(nameof(acquirer));
    }

    public async ValueTask<DirectoryCopyFreshManifestGateResult> PrepareAsync(
        DirectoryOperationTreeManifest reviewedManifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reviewedManifest);
        cancellationToken.ThrowIfCancellationRequested();

        var acquisition = await _acquirer
            .AcquireFreshAsync(reviewedManifest, cancellationToken)
            .ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(acquisition);
        cancellationToken.ThrowIfCancellationRequested();

        if (acquisition.Status == DirectoryOperationTreeManifestAcquisitionStatus.Unsupported)
        {
            return new DirectoryCopyFreshManifestGateResult(
                DirectoryCopyFreshManifestGateStatus.AcquisitionUnsupported,
                reviewedManifest,
                acquisition,
                revalidation: null,
                "Directory Copy cannot begin durable history because the fresh tree has unsupported fidelity: " +
                acquisition.Summary);
        }

        if (acquisition.Status != DirectoryOperationTreeManifestAcquisitionStatus.Ready ||
            acquisition.Manifest is null)
        {
            return new DirectoryCopyFreshManifestGateResult(
                DirectoryCopyFreshManifestGateStatus.AcquisitionUnavailable,
                reviewedManifest,
                acquisition,
                revalidation: null,
                "Directory Copy cannot begin durable history because fresh tree evidence is unavailable: " +
                acquisition.Summary);
        }

        var revalidation = DirectoryOperationTreeManifestRevalidator.Compare(
            reviewedManifest,
            acquisition.Manifest);
        if (!revalidation.EvidenceStillMatches)
        {
            return new DirectoryCopyFreshManifestGateResult(
                DirectoryCopyFreshManifestGateStatus.ReviewedTreeChanged,
                reviewedManifest,
                acquisition,
                revalidation,
                $"Directory Copy cannot begin durable history because fresh tree evidence differs in {revalidation.Changes.Count:N0} reviewed binding(s). Acquire/review a new manifest before retrying.");
        }

        return new DirectoryCopyFreshManifestGateResult(
            DirectoryCopyFreshManifestGateStatus.ReadyForDurableHistory,
            reviewedManifest,
            acquisition,
            revalidation,
            "Fresh directory tree evidence exactly matches the reviewed manifest. Separate durable directory-Copy history may now begin; no filesystem mutation authority was created by this gate.");
    }
}
