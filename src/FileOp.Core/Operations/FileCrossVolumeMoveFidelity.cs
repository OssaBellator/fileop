using System;
using System.Collections.Generic;

namespace FileOp.Core.Operations;

[Flags]
public enum FileCrossVolumeMoveFidelityBlocker
{
    None = 0,
    SourceContentChanged = 1 << 0,
    DestinationContentChanged = 1 << 1,
    StableBasicMetadataMismatch = 1 << 2,
    SourceUnsupportedAttributes = 1 << 3,
    DestinationUnsupportedAttributes = 1 << 4,
    SourceNamedDataStreams = 1 << 7,
    SourceExtendedAttributes = 1 << 11,
}

/// <summary>
/// Evidence collected while the exact source and committed destination objects are
/// simultaneously pinned. A positive classification is evidence only; it does not
/// grant source-delete authority. Core mints delete authority separately and only
/// after the durable source-delete mutation barrier has been recorded.
///
/// Security-descriptor equivalence is intentionally absent. FileOp's selected
/// cross-volume Move contract follows Windows destination-default/inherited security
/// semantics rather than promising preservation of the source security descriptor.
/// Hard-link count remains observable evidence but is not an equivalence requirement:
/// a cross-volume Move removes the selected source directory entry and cannot recreate
/// same-volume hard-link topology on another volume.
/// </summary>
public sealed record FileCrossVolumeMoveFidelityEvidence(
    FileContentFingerprint CommittedDestinationContentFingerprint,
    FileContentFingerprint CurrentSourceContentFingerprint,
    FileContentFingerprint CurrentDestinationContentFingerprint,
    FileBasicMetadataEvidence CurrentSourceBasicMetadata,
    FileBasicMetadataEvidence CurrentDestinationBasicMetadata,
    int SourceNamedDataStreamCount,
    int DestinationNamedDataStreamCount,
    uint SourceHardLinkCount,
    uint DestinationHardLinkCount,
    uint SourceExtendedAttributeSize,
    uint DestinationExtendedAttributeSize);

public sealed record FileCrossVolumeMoveFidelityClassification(
    bool CanDeleteSourceAfterDurableBarrier,
    IReadOnlyList<FileCrossVolumeMoveFidelityBlocker> Blockers,
    string Summary);

/// <summary>
/// Defines the deliberately narrow checkpoint evidence in which destructive
/// cross-volume Move may proceed to its separately durable source-delete boundary.
/// Main-stream content is the destructive invariant. Unsupported source semantics that
/// the current Copy path does not carry are refused when observed. Metadata comparison
/// remains useful checkpoint evidence, but the product contract does not pretend unrelated
/// metadata writers are atomically frozen across a cross-volume copy/delete transaction.
///
/// Read-only files are deliberately refused before the source-delete barrier for this
/// draft. The current raw Windows disposition does not request
/// FILE_DISPOSITION_IGNORE_READONLY_ATTRIBUTE, so treating ReadOnly as supported here
/// would turn a predictable delete refusal into recovery after SourceDeleteStarted.
/// </summary>
public static class FileCrossVolumeMoveFidelityClassifier
{
    private const uint FileAttributeReadOnly = 0x00000001u;
    private const uint FileAttributeNormal = 0x00000080u;
    private const uint AllowedOrdinaryFileAttributes =
        (FileBasicMetadataEvidence.StableCopiedAttributesMask & ~FileAttributeReadOnly) |
        FileAttributeNormal;

    public static FileCrossVolumeMoveFidelityClassification Classify(
        FileCrossVolumeMoveFidelityEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(evidence.CommittedDestinationContentFingerprint);
        ArgumentNullException.ThrowIfNull(evidence.CurrentSourceContentFingerprint);
        ArgumentNullException.ThrowIfNull(evidence.CurrentDestinationContentFingerprint);
        ArgumentNullException.ThrowIfNull(evidence.CurrentSourceBasicMetadata);
        ArgumentNullException.ThrowIfNull(evidence.CurrentDestinationBasicMetadata);

        var blockers = new List<FileCrossVolumeMoveFidelityBlocker>();

        if (!FingerprintsEqual(
                evidence.CommittedDestinationContentFingerprint,
                evidence.CurrentSourceContentFingerprint))
        {
            blockers.Add(FileCrossVolumeMoveFidelityBlocker.SourceContentChanged);
        }

        if (!FingerprintsEqual(
                evidence.CommittedDestinationContentFingerprint,
                evidence.CurrentDestinationContentFingerprint))
        {
            blockers.Add(FileCrossVolumeMoveFidelityBlocker.DestinationContentChanged);
        }

        if (!FileOperationRecoveryBasicMetadataComparer.Compare(
                evidence.CurrentSourceBasicMetadata,
                evidence.CurrentDestinationBasicMetadata).SameStableMetadata)
        {
            blockers.Add(FileCrossVolumeMoveFidelityBlocker.StableBasicMetadataMismatch);
        }

        if (HasUnsupportedAttributes(evidence.CurrentSourceBasicMetadata.FileAttributes))
        {
            blockers.Add(FileCrossVolumeMoveFidelityBlocker.SourceUnsupportedAttributes);
        }

        if (HasUnsupportedAttributes(evidence.CurrentDestinationBasicMetadata.FileAttributes))
        {
            blockers.Add(FileCrossVolumeMoveFidelityBlocker.DestinationUnsupportedAttributes);
        }

        if (FileCrossVolumeMovePreservationPolicy.RequiresNoSourceNamedDataStreamsAtProof &&
            evidence.SourceNamedDataStreamCount != 0)
        {
            blockers.Add(FileCrossVolumeMoveFidelityBlocker.SourceNamedDataStreams);
        }

        if (FileCrossVolumeMovePreservationPolicy.RequiresNoSourceExtendedAttributesAtProof &&
            evidence.SourceExtendedAttributeSize != 0)
        {
            blockers.Add(FileCrossVolumeMoveFidelityBlocker.SourceExtendedAttributes);
        }

        if (blockers.Count == 0)
        {
            return new FileCrossVolumeMoveFidelityClassification(
                CanDeleteSourceAfterDurableBarrier: true,
                Array.Empty<FileCrossVolumeMoveFidelityBlocker>(),
                "Pinned source and destination satisfy FileOp's current cross-volume Move checkpoint contract. Main-stream content matches the durable Copy fingerprint; unsupported source stream/EA and file-attribute semantics are absent; security follows destination-default Windows semantics; hard-link topology is path-entry state rather than cross-volume preservation state. This grants no delete authority before the durable source-delete barrier.");
        }

        return new FileCrossVolumeMoveFidelityClassification(
            CanDeleteSourceAfterDurableBarrier: false,
            blockers.AsReadOnly(),
            "Cross-volume Move must retain the source because the current checkpoint evidence does not satisfy FileOp's supported cross-volume Move preservation contract.");
    }

    private static bool FingerprintsEqual(
        FileContentFingerprint left,
        FileContentFingerprint right) =>
        left.Algorithm == right.Algorithm &&
        string.Equals(left.HexDigest, right.HexDigest, StringComparison.Ordinal);

    private static bool HasUnsupportedAttributes(uint attributes) =>
        (attributes & ~AllowedOrdinaryFileAttributes) != 0;
}
