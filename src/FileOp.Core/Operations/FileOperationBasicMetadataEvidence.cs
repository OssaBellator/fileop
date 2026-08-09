using System;

namespace FileOp.Core.Operations;

/// <summary>
/// Raw handle-observed basic metadata. FILETIME values are stored as their exact
/// unsigned 64-bit bit patterns; attributes use Windows FILE_ATTRIBUTE_* bits.
/// </summary>
public sealed record FileBasicMetadataEvidence(
    ulong CreationTimeFileTime,
    ulong LastAccessTimeFileTime,
    ulong LastWriteTimeFileTime,
    uint FileAttributes)
{
    // This is exactly the source-owned safe attribute set preserved by the
    // handle-bound Copy metadata layer. Provider/filesystem-managed bits such as
    // Offline, Temporary, Sparse, Compressed and Encrypted remain outside this comparison.
    public const uint StableCopiedAttributesMask =
        0x00000001u | // ReadOnly
        0x00000002u | // Hidden
        0x00000004u | // System
        0x00000020u | // Archive
        0x00002000u;  // NotContentIndexed

    public uint StableCopiedAttributes =>
        FileAttributes & StableCopiedAttributesMask;
}

public enum FileOperationRecoveryBasicMetadataStatus
{
    NoRecordedEvidence,
    SameStableMetadata,
    DifferentStableMetadata,
    Unavailable,
}

/// <summary>
/// Comparison of post-Copy durable basic metadata with a current handle observation.
/// Last-access equality is diagnostic only and never participates in Status because
/// a read/filesystem/provider may update last-access as a side effect of verification.
/// </summary>
public sealed record FileOperationRecoveryBasicMetadataComparison(
    FileOperationRecoveryBasicMetadataStatus Status,
    FileBasicMetadataEvidence? Recorded,
    FileBasicMetadataEvidence? Current,
    bool? CreationTimeMatches,
    bool? LastWriteTimeMatches,
    bool? StableCopiedAttributesMatch,
    bool? LastAccessTimeMatchesDiagnostic)
{
    public bool SameStableMetadata =>
        Status == FileOperationRecoveryBasicMetadataStatus.SameStableMetadata;
}

public static class FileOperationRecoveryBasicMetadataComparer
{
    public static FileOperationRecoveryBasicMetadataComparison Compare(
        FileBasicMetadataEvidence? recorded,
        FileBasicMetadataEvidence? current)
    {
        if (recorded is null)
        {
            return new FileOperationRecoveryBasicMetadataComparison(
                FileOperationRecoveryBasicMetadataStatus.NoRecordedEvidence,
                Recorded: null,
                Current: current,
                CreationTimeMatches: null,
                LastWriteTimeMatches: null,
                StableCopiedAttributesMatch: null,
                LastAccessTimeMatchesDiagnostic: null);
        }

        if (current is null)
        {
            return new FileOperationRecoveryBasicMetadataComparison(
                FileOperationRecoveryBasicMetadataStatus.Unavailable,
                recorded,
                Current: null,
                CreationTimeMatches: null,
                LastWriteTimeMatches: null,
                StableCopiedAttributesMatch: null,
                LastAccessTimeMatchesDiagnostic: null);
        }

        var creationMatches = recorded.CreationTimeFileTime == current.CreationTimeFileTime;
        var lastWriteMatches = recorded.LastWriteTimeFileTime == current.LastWriteTimeFileTime;
        var attributesMatch =
            recorded.StableCopiedAttributes == current.StableCopiedAttributes;
        var lastAccessMatches = recorded.LastAccessTimeFileTime == current.LastAccessTimeFileTime;
        var stableMatches = creationMatches && lastWriteMatches && attributesMatch;

        return new FileOperationRecoveryBasicMetadataComparison(
            stableMatches
                ? FileOperationRecoveryBasicMetadataStatus.SameStableMetadata
                : FileOperationRecoveryBasicMetadataStatus.DifferentStableMetadata,
            recorded,
            current,
            creationMatches,
            lastWriteMatches,
            attributesMatch,
            lastAccessMatches);
    }
}
