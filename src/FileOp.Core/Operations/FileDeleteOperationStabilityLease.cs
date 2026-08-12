using System;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;

namespace FileOp.Core.Operations;

public sealed class FileDeleteOperationStabilityLeaseRequest
{
    public FileDeleteOperationStabilityLeaseRequest(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        int ordinal)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (!authorization.UserAuthorizedAttempt || authorization.DeleteMutationAuthorized)
        {
            throw new ArgumentException(
                "Delete stability leasing requires an explicit user-authorization receipt that remains non-authorizing for mutation.",
                nameof(authorization));
        }
        if (ordinal < 0 || ordinal >= authorization.Items.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }
        if (authorization.Items.Count != authorization.Plan.Intent.Entries.Count)
        {
            throw new ArgumentException(
                "Delete stability leasing requires complete authorization evidence for the captured plan.",
                nameof(authorization));
        }

        var authorizedItem = authorization.Items[ordinal];
        var plannedEntry = authorization.Plan.Intent.Entries[ordinal];
        if (authorizedItem.Entry != plannedEntry || plannedEntry.IsDirectory)
        {
            throw new ArgumentException(
                "Delete stability leasing requires the exact authorized file entry in captured plan order.",
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
}

public sealed class FileDeleteOperationStabilityLeaseEvidence
{
    public FileDeleteOperationStabilityLeaseEvidence(
        FileDeleteOperationStabilityLeaseRequest request,
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
                "Delete stability evidence cannot be created from mutation-authorizing input.",
                nameof(request));
        }
        if (!string.Equals(
                canonicalSourceDirectoryPath,
                request.Authorization.CanonicalSourceDirectoryPath,
                StringComparison.OrdinalIgnoreCase) ||
            sourceDirectoryIdentity != request.Authorization.SourceDirectoryIdentity)
        {
            throw new ArgumentException(
                "Delete stability root evidence must exactly match the root identity reviewed by the user.",
                nameof(request));
        }
        if (!string.Equals(
                canonicalSourcePath,
                request.AuthorizedItem.CanonicalPath,
                StringComparison.OrdinalIgnoreCase) ||
            sourceIdentity != request.AuthorizedItem.Identity)
        {
            throw new ArgumentException(
                "Delete stability file evidence must exactly match the file identity reviewed by the user.",
                nameof(request));
        }

        Request = request;
        CanonicalSourceDirectoryPath = canonicalSourceDirectoryPath;
        SourceDirectoryIdentity = sourceDirectoryIdentity;
        CanonicalSourcePath = canonicalSourcePath;
        SourceIdentity = sourceIdentity;
    }

    public FileDeleteOperationStabilityLeaseRequest Request { get; }

    public FileDeleteOperationUserAuthorizationReceipt Authorization => Request.Authorization;

    public Guid AuthorizationId => Authorization.AuthorizationId;

    public Guid PlanId => Authorization.PlanId;

    public int Ordinal => Request.Ordinal;

    public FileOperationEntry Entry => Request.Entry;

    public string CanonicalSourceDirectoryPath { get; }

    public FileIdentity SourceDirectoryIdentity { get; }

    public string CanonicalSourcePath { get; }

    public FileIdentity SourceIdentity { get; }

    public bool DeleteMutationAuthorized => false;

    public bool IsBoundTo(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        int ordinal) =>
        ReferenceEquals(Authorization, authorization) && Ordinal == ordinal;
}

/// <summary>
/// Holds read-only namespace/identity handles for one explicitly authorized file.
/// The lease deliberately exposes no mutation primitive and grants no delete authority.
/// </summary>
public interface IFileDeleteOperationStabilityLease : IAsyncDisposable
{
    FileDeleteOperationStabilityLeaseEvidence Evidence { get; }

    bool DeleteMutationAuthorized { get; }
}

public interface IFileDeleteOperationStabilityLeaseProvider
{
    ValueTask<IFileDeleteOperationStabilityLease> AcquireAsync(
        FileDeleteOperationStabilityLeaseRequest request,
        CancellationToken cancellationToken = default);
}
