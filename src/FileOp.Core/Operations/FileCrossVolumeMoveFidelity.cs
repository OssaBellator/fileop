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
    DestinationNamedDataStreams = 1 << 8,
    SourceHardLinks = 1 << 9,
    DestinationHardLinks = 1 << 10,
    SourceExtendedAttributes = 1 << 11,
    DestinationExtendedAttributes = 1 << 12,
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
/// Defines the deliberately narrow fidelity subset in which a destructive
/// cross-volume Move may delete its source after Copy has durably committed.
/// Anything FileOp's current Copy primitive does not preserve under the selected
/// platform contract is blocked. Security is handled by the explicit
/// <see cref="FileCrossVolumeMoveSecurityPolicy"/> instead of equivalence checking.
/// </summary>
public static class FileCrossVolumeMoveFidelityClassifier
{
    private const uint FileAttributeNormal = 0x00000080u;
    private const uint AllowedOrdinaryFileAttributes =
        FileBasicMetadataEvidence.StableCopiedAttributesMask |
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

        if (evidence.SourceNamedDataStreamCount != 0)
        {
            blockers.Add(FileCrossVolumeMoveFidelityBlocker.SourceNamedDataStreams);
        }

        if (evidence.DestinationNamedDataStreamCount != 0)
        {
            blockers.Add(FileCrossVolumeMoveFidelityBlocker.DestinationNamedDataStreams);
        }

        if (evidence.SourceHardLinkCount != 1)
        {
            blockers.Add(FileCrossVolumeMoveFidelityBlocker.SourceHardLinks);
        }

        if (evidence.DestinationHardLinkCount != 1)
        {
            blockers.Add(FileCrossVolumeMoveFidelityBlocker.DestinationHardLinks);
        }

        if (evidence.SourceExtendedAttributeSize != 0)
        {
            blockers.Add(FileCrossVolumeMoveFidelityBlocker.SourceExtendedAttributes);
        }

        if (evidence.DestinationExtendedAttributeSize != 0)
        {
            blockers.Add(FileCrossVolumeMoveFidelityBlocker.DestinationExtendedAttributes);
        }

        if (blockers.Count == 0)
        {
            return new FileCrossVolumeMoveFidelityClassification(
                CanDeleteSourceAfterDurableBarrier: true,
                Array.Empty<FileCrossVolumeMoveFidelityBlocker>(),
                "Pinned source and destination evidence is equivalent within FileOp's current destructive cross-volume Move data/metadata fidelity contract. Security follows the explicit destination-default Windows cross-volume Move policy. This still grants no delete authority before the durable source-delete barrier.");
        }

        return new FileCrossVolumeMoveFidelityClassification(
            CanDeleteSourceAfterDurableBarrier: false,
            blockers.AsReadOnly(),
            "Cross-volume Move must retain the source because the committed destination does not prove the complete supported data/metadata fidelity subset for the currently pinned source object.");
    }

    private static bool FingerprintsEqual(
        FileContentFingerprint left,
        FileContentFingerprint right) =>
        left.Algorithm == right.Algorithm &&
        string.Equals(left.HexDigest, right.HexDigest, StringComparison.Ordinal);

    private static bool HasUnsupportedAttributes(uint attributes) =>
        (attributes & ~AllowedOrdinaryFileAttributes) != 0;
}
