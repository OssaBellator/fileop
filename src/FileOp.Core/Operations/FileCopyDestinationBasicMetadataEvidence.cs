using System;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;

namespace FileOp.Core.Operations;

/// <summary>
/// One commit-bound observation from the verified destination object. The link
/// count and basic metadata are intentionally sampled through the same handle.
/// </summary>
public sealed record FileCopyDestinationCommitBasicMetadataEvidence
{
    public FileCopyDestinationCommitBasicMetadataEvidence(
        uint hardLinkCount,
        FileBasicMetadataEvidence basicMetadata)
    {
        if (hardLinkCount == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(hardLinkCount),
                "A verified destination hard-link count must be greater than zero.");
        }

        HardLinkCount = hardLinkCount;
        BasicMetadata = basicMetadata ?? throw new ArgumentNullException(nameof(basicMetadata));
    }

    public uint HardLinkCount { get; }

    public FileBasicMetadataEvidence BasicMetadata { get; }
}

public interface IFileCopyDestinationCommitBasicMetadataEvidenceSource
{
    ValueTask<FileCopyDestinationCommitBasicMetadataEvidence> ReadVerifiedEvidenceAsync(
        FileOperationActionHistory history,
        int ordinal,
        FileIdentity expectedDestinationIdentity,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional stronger action-history capability for atomically persisting the
/// existing destination identity/SHA-256/link-count proof plus basic metadata.
/// </summary>
public interface IFileOperationActionHistoryBasicMetadataEvidenceStore :
    IFileOperationActionHistoryHardLinkEvidenceStore
{
    ValueTask<FileOperationActionHistory> CommitCopyWithBasicMetadataEvidenceAsync(
        Guid operationId,
        int ordinal,
        FileIdentity destinationIdentity,
        FileContentFingerprint destinationContentFingerprint,
        FileCopyDestinationCommitBasicMetadataEvidence destinationEvidence,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<FileOperationActionHistory> MarkMutationRecoveryRequiredWithBasicMetadataEvidenceAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        DateTimeOffset failedAtUtc,
        FileIdentity destinationIdentity,
        FileContentFingerprint destinationContentFingerprint,
        FileCopyDestinationCommitBasicMetadataEvidence destinationEvidence,
        CancellationToken cancellationToken = default);

    ValueTask<FileBasicMetadataEvidence?> GetDestinationBasicMetadataEvidenceAsync(
        Guid operationId,
        int ordinal,
        CancellationToken cancellationToken = default);
}
