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
public sealed record DirectoryCopyFreshManifestGateResult(
    DirectoryCopyFreshManifestGateStatus Status,
    DirectoryOperationTreeManifest ReviewedManifest,
    DirectoryOperationTreeManifestAcquisitionResult Acquisition,
    DirectoryOperationTreeManifestRevalidationResult? Revalidation,
    string Summary)
{
    public bool CanBeginDurableHistory =>
        Status == DirectoryCopyFreshManifestGateStatus.ReadyForDurableHistory &&
        Acquisition.CanRevalidateReviewedManifest &&
        Revalidation?.EvidenceStillMatches == true;

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
                Revalidation: null,
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
                Revalidation: null,
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
