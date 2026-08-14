using System;
using System.Linq;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileCrossVolumeMoveFidelityTests
{
    private static readonly FileContentFingerprint CommittedFingerprint = Fingerprint('a');

    [TestMethod]
    public void EquivalentOrdinaryPinnedFilesMayReachLaterDeleteBarrier()
    {
        var classification = FileCrossVolumeMoveFidelityClassifier.Classify(BaselineEvidence());

        Assert.IsTrue(classification.CanDeleteSourceAfterDurableBarrier);
        Assert.AreEqual(0, classification.Blockers.Count);
    }

    [TestMethod]
    public void ContentDriftOnEitherPinnedObjectBlocksSourceDeletion()
    {
        var sourceChanged = FileCrossVolumeMoveFidelityClassifier.Classify(
            BaselineEvidence() with { CurrentSourceContentFingerprint = Fingerprint('b') });
        var destinationChanged = FileCrossVolumeMoveFidelityClassifier.Classify(
            BaselineEvidence() with { CurrentDestinationContentFingerprint = Fingerprint('c') });

        AssertBlocker(sourceChanged, FileCrossVolumeMoveFidelityBlocker.SourceContentChanged);
        AssertBlocker(destinationChanged, FileCrossVolumeMoveFidelityBlocker.DestinationContentChanged);
    }

    [TestMethod]
    public void MetadataOrSecurityUncertaintyBlocksSourceDeletion()
    {
        var metadataChanged = FileCrossVolumeMoveFidelityClassifier.Classify(
            BaselineEvidence() with
            {
                CurrentDestinationBasicMetadata = new FileBasicMetadataEvidence(
                    CreationTimeFileTime: 1,
                    LastAccessTimeFileTime: 2,
                    LastWriteTimeFileTime: 99,
                    FileAttributes: 0x20u),
            });
        var unsupportedAttribute = FileCrossVolumeMoveFidelityClassifier.Classify(
            BaselineEvidence() with
            {
                CurrentSourceBasicMetadata = new FileBasicMetadataEvidence(
                    CreationTimeFileTime: 1,
                    LastAccessTimeFileTime: 2,
                    LastWriteTimeFileTime: 3,
                    FileAttributes: 0x00000820u), // Archive + Compressed
            });
        var securityUnavailable = FileCrossVolumeMoveFidelityClassifier.Classify(
            BaselineEvidence() with
            {
                SecurityDescriptorEvidenceComplete = false,
                SecurityDescriptorEquivalent = false,
            });
        var securityChanged = FileCrossVolumeMoveFidelityClassifier.Classify(
            BaselineEvidence() with { SecurityDescriptorEquivalent = false });

        AssertBlocker(metadataChanged, FileCrossVolumeMoveFidelityBlocker.StableBasicMetadataMismatch);
        AssertBlocker(unsupportedAttribute, FileCrossVolumeMoveFidelityBlocker.SourceUnsupportedAttributes);
        AssertBlocker(securityUnavailable, FileCrossVolumeMoveFidelityBlocker.SecurityDescriptorEvidenceIncomplete);
        Assert.IsFalse(securityUnavailable.Blockers.Contains(FileCrossVolumeMoveFidelityBlocker.SecurityDescriptorMismatch));
        AssertBlocker(securityChanged, FileCrossVolumeMoveFidelityBlocker.SecurityDescriptorMismatch);
    }

    [TestMethod]
    public void StreamsHardLinksAndExtendedAttributesBlockDestructiveCompletion()
    {
        var evidence = BaselineEvidence() with
        {
            SourceNamedDataStreamCount = 1,
            DestinationNamedDataStreamCount = 1,
            SourceHardLinkCount = 2,
            DestinationHardLinkCount = 2,
            SourceExtendedAttributeSize = 12,
            DestinationExtendedAttributeSize = 16,
        };

        var classification = FileCrossVolumeMoveFidelityClassifier.Classify(evidence);

        AssertBlocker(classification, FileCrossVolumeMoveFidelityBlocker.SourceNamedDataStreams);
        AssertBlocker(classification, FileCrossVolumeMoveFidelityBlocker.DestinationNamedDataStreams);
        AssertBlocker(classification, FileCrossVolumeMoveFidelityBlocker.SourceHardLinks);
        AssertBlocker(classification, FileCrossVolumeMoveFidelityBlocker.DestinationHardLinks);
        AssertBlocker(classification, FileCrossVolumeMoveFidelityBlocker.SourceExtendedAttributes);
        AssertBlocker(classification, FileCrossVolumeMoveFidelityBlocker.DestinationExtendedAttributes);
    }

    [TestMethod]
    public void NormalAttributeMayStandInForEmptyStableAttributeSet()
    {
        var classification = FileCrossVolumeMoveFidelityClassifier.Classify(
            BaselineEvidence() with
            {
                CurrentSourceBasicMetadata = new FileBasicMetadataEvidence(1, 2, 3, 0x80u),
                CurrentDestinationBasicMetadata = new FileBasicMetadataEvidence(1, 4, 3, 0x80u),
            });

        Assert.IsTrue(classification.CanDeleteSourceAfterDurableBarrier);
    }

    private static FileCrossVolumeMoveFidelityEvidence BaselineEvidence() =>
        new(
            CommittedDestinationContentFingerprint: CommittedFingerprint,
            CurrentSourceContentFingerprint: CommittedFingerprint,
            CurrentDestinationContentFingerprint: CommittedFingerprint,
            CurrentSourceBasicMetadata: new FileBasicMetadataEvidence(1, 2, 3, 0x20u),
            CurrentDestinationBasicMetadata: new FileBasicMetadataEvidence(1, 4, 3, 0x20u),
            SourceNamedDataStreamCount: 0,
            DestinationNamedDataStreamCount: 0,
            SourceHardLinkCount: 1,
            DestinationHardLinkCount: 1,
            SourceExtendedAttributeSize: 0,
            DestinationExtendedAttributeSize: 0,
            SecurityDescriptorEvidenceComplete: true,
            SecurityDescriptorEquivalent: true);

    private static FileContentFingerprint Fingerprint(char digit) =>
        new(FileContentFingerprintAlgorithm.Sha256, new string(digit, FileContentFingerprint.Sha256HexLength));

    private static void AssertBlocker(
        FileCrossVolumeMoveFidelityClassification classification,
        FileCrossVolumeMoveFidelityBlocker blocker)
    {
        Assert.IsFalse(classification.CanDeleteSourceAfterDurableBarrier);
        Assert.IsTrue(
            classification.Blockers.Contains(blocker),
            $"Expected blocker {blocker}; actual blockers: {string.Join(", ", classification.Blockers)}");
    }
}
