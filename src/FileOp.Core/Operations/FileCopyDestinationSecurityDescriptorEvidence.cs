using System;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;

namespace FileOp.Core.Operations;

/// <summary>
/// One commit-bound observation containing the existing hard-link/basic-metadata
/// evidence plus an owner/group/DACL descriptor digest sampled through the same leaf.
/// </summary>
public sealed record FileCopyDestinationCommitSecurityDescriptorEvidence
{
    public FileCopyDestinationCommitSecurityDescriptorEvidence(
        FileCopyDestinationCommitBasicMetadataEvidence basicMetadataEvidence,
        FileSecurityDescriptorEvidence securityDescriptor)
    {
        BasicMetadataEvidence = basicMetadataEvidence
            ?? throw new ArgumentNullException(nameof(basicMetadataEvidence));
        SecurityDescriptor = securityDescriptor
            ?? throw new ArgumentNullException(nameof(securityDescriptor));
    }

    public FileCopyDestinationCommitBasicMetadataEvidence BasicMetadataEvidence { get; }

    public FileSecurityDescriptorEvidence SecurityDescriptor { get; }

    public uint HardLinkCount => BasicMetadataEvidence.HardLinkCount;

    public FileBasicMetadataEvidence BasicMetadata => BasicMetadataEvidence.BasicMetadata;
}

public interface IFileCopyDestinationCommitSecurityDescriptorEvidenceSource
{
    ValueTask<FileCopyDestinationCommitSecurityDescriptorEvidence> ReadVerifiedEvidenceAsync(
        FileOperationActionHistory history,
        int ordinal,
        FileIdentity expectedDestinationIdentity,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional stronger history capability for atomically persisting the existing
/// identity/content/topology/basic-metadata proof plus owner/group/DACL digest.
/// Existing weaker history-store interfaces remain source-compatible.
/// </summary>
public interface IFileOperationActionHistorySecurityDescriptorEvidenceStore :
    IFileOperationActionHistoryBasicMetadataEvidenceStore
{
    ValueTask<FileOperationActionHistory> CommitCopyWithSecurityDescriptorEvidenceAsync(
        Guid operationId,
        int ordinal,
        FileIdentity destinationIdentity,
        FileContentFingerprint destinationContentFingerprint,
        FileCopyDestinationCommitSecurityDescriptorEvidence destinationEvidence,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<FileOperationActionHistory> MarkMutationRecoveryRequiredWithSecurityDescriptorEvidenceAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        DateTimeOffset failedAtUtc,
        FileIdentity destinationIdentity,
        FileContentFingerprint destinationContentFingerprint,
        FileCopyDestinationCommitSecurityDescriptorEvidence destinationEvidence,
        CancellationToken cancellationToken = default);

    ValueTask<FileSecurityDescriptorEvidence?> GetDestinationSecurityDescriptorEvidenceAsync(
        Guid operationId,
        int ordinal,
        CancellationToken cancellationToken = default);
}
