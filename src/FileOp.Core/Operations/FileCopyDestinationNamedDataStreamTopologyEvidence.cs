using System;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;

namespace FileOp.Core.Operations;

/// <summary>
/// One commit-bound observation containing the prior security-aware evidence plus
/// a versioned digest of named $DATA stream names and logical sizes.
/// </summary>
public sealed record FileCopyDestinationCommitNamedDataStreamTopologyEvidence
{
    public FileCopyDestinationCommitNamedDataStreamTopologyEvidence(
        FileCopyDestinationCommitSecurityDescriptorEvidence securityDescriptorEvidence,
        FileNamedDataStreamTopologyEvidence namedDataStreamTopology)
    {
        SecurityDescriptorEvidence = securityDescriptorEvidence
            ?? throw new ArgumentNullException(nameof(securityDescriptorEvidence));
        NamedDataStreamTopology = namedDataStreamTopology
            ?? throw new ArgumentNullException(nameof(namedDataStreamTopology));
    }

    public FileCopyDestinationCommitSecurityDescriptorEvidence SecurityDescriptorEvidence { get; }

    public FileNamedDataStreamTopologyEvidence NamedDataStreamTopology { get; }

    public uint HardLinkCount => SecurityDescriptorEvidence.HardLinkCount;

    public FileBasicMetadataEvidence BasicMetadata => SecurityDescriptorEvidence.BasicMetadata;

    public FileSecurityDescriptorEvidence SecurityDescriptor =>
        SecurityDescriptorEvidence.SecurityDescriptor;
}

public interface IFileCopyDestinationCommitNamedDataStreamTopologyEvidenceSource
{
    ValueTask<FileCopyDestinationCommitNamedDataStreamTopologyEvidence> ReadVerifiedEvidenceAsync(
        FileOperationActionHistory history,
        int ordinal,
        FileIdentity expectedDestinationIdentity,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional stronger history capability for atomically persisting identity,
/// primary-stream SHA-256, hard-link count, basic metadata, owner/group/DACL
/// digest, and named-data-stream topology/size evidence.
/// </summary>
public interface IFileOperationActionHistoryNamedDataStreamTopologyEvidenceStore :
    IFileOperationActionHistorySecurityDescriptorEvidenceStore
{
    ValueTask<FileOperationActionHistory> CommitCopyWithNamedDataStreamTopologyEvidenceAsync(
        Guid operationId,
        int ordinal,
        FileIdentity destinationIdentity,
        FileContentFingerprint destinationContentFingerprint,
        FileCopyDestinationCommitNamedDataStreamTopologyEvidence destinationEvidence,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask<FileOperationActionHistory> MarkMutationRecoveryRequiredWithNamedDataStreamTopologyEvidenceAsync(
        Guid operationId,
        int ordinal,
        FileOperationFailure failure,
        DateTimeOffset failedAtUtc,
        FileIdentity destinationIdentity,
        FileContentFingerprint destinationContentFingerprint,
        FileCopyDestinationCommitNamedDataStreamTopologyEvidence destinationEvidence,
        CancellationToken cancellationToken = default);

    ValueTask<FileNamedDataStreamTopologyEvidence?> GetDestinationNamedDataStreamTopologyEvidenceAsync(
        Guid operationId,
        int ordinal,
        CancellationToken cancellationToken = default);
}
