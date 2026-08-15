using System;
using System.Threading;
using System.Threading.Tasks;

namespace FileOp.Core.Operations;

public enum DirectoryOperationTreeManifestAcquisitionStatus
{
    Ready,
    Unsupported,
    Unavailable,
}

/// <summary>
/// Result of a read-only attempt to acquire a fresh immutable directory-tree manifest.
/// Acquisition evidence never authorizes Copy, creation, deletion or any other mutation.
/// </summary>
public sealed record DirectoryOperationTreeManifestAcquisitionResult
{
    private DirectoryOperationTreeManifestAcquisitionResult(
        DirectoryOperationTreeManifestAcquisitionStatus status,
        DirectoryOperationTreeManifest? manifest,
        DirectoryOperationFidelityEvidence? fidelityEvidence,
        string summary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        if (status == DirectoryOperationTreeManifestAcquisitionStatus.Ready)
        {
            ArgumentNullException.ThrowIfNull(manifest);
            ArgumentNullException.ThrowIfNull(fidelityEvidence);
            var fidelity = DirectoryOperationFidelityClassifier.Classify(fidelityEvidence);
            if (!fidelity.CanEnterFutureMutationBoundary)
            {
                throw new ArgumentException(
                    "Ready directory manifest acquisition requires complete plain-tree fidelity evidence.",
                    nameof(fidelityEvidence));
            }
        }
        else if (manifest is not null)
        {
            throw new ArgumentException(
                "Unsupported/unavailable directory manifest acquisition must not publish a manifest as usable fresh evidence.",
                nameof(manifest));
        }

        Status = status;
        Manifest = manifest;
        FidelityEvidence = fidelityEvidence;
        Summary = summary;
    }

    public DirectoryOperationTreeManifestAcquisitionStatus Status { get; }

    public DirectoryOperationTreeManifest? Manifest { get; }

    public DirectoryOperationFidelityEvidence? FidelityEvidence { get; }

    public string Summary { get; }

    public bool CanRevalidateReviewedManifest =>
        Status == DirectoryOperationTreeManifestAcquisitionStatus.Ready && Manifest is not null;

    public bool GrantsMutationAuthority => false;

    public bool GrantsCopyAuthority => false;

    public bool GrantsCreateAuthority => false;

    public bool GrantsDeleteAuthority => false;

    public static DirectoryOperationTreeManifestAcquisitionResult Ready(
        DirectoryOperationTreeManifest manifest,
        DirectoryOperationFidelityEvidence fidelityEvidence,
        string summary) =>
        new(
            DirectoryOperationTreeManifestAcquisitionStatus.Ready,
            manifest,
            fidelityEvidence,
            summary);

    public static DirectoryOperationTreeManifestAcquisitionResult Unsupported(
        DirectoryOperationFidelityEvidence? fidelityEvidence,
        string summary) =>
        new(
            DirectoryOperationTreeManifestAcquisitionStatus.Unsupported,
            manifest: null,
            fidelityEvidence,
            summary);

    public static DirectoryOperationTreeManifestAcquisitionResult Unavailable(
        DirectoryOperationFidelityEvidence? fidelityEvidence,
        string summary) =>
        new(
            DirectoryOperationTreeManifestAcquisitionStatus.Unavailable,
            manifest: null,
            fidelityEvidence,
            summary);
}

/// <summary>
/// Read-only acquisition seam used immediately before a future recursive mutation boundary.
/// Implementations must acquire new filesystem evidence; returning the caller's reviewed
/// manifest without fresh enumeration/metadata inspection violates this contract.
/// </summary>
public interface IDirectoryOperationTreeManifestAcquirer
{
    ValueTask<DirectoryOperationTreeManifestAcquisitionResult> AcquireFreshAsync(
        DirectoryOperationTreeManifest reviewedManifest,
        CancellationToken cancellationToken = default);
}
