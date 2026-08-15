using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileCrossVolumeMoveSourcePreflightAttributeTests
{
    [TestMethod]
    public void SparseCompressedEncryptedIntegrityAndNoScrubAttributesBlockBeforeCopy()
    {
        var unsupportedAttributes = new[]
        {
            0x00000200u, // SparseFile
            0x00000800u, // Compressed
            0x00004000u, // Encrypted
            0x00008000u, // IntegrityStream
            0x00020000u, // NoScrubData
        };

        foreach (var attributes in unsupportedAttributes)
        {
            var classification = FileCrossVolumeMoveSourcePreflightClassifier.Classify(
                new FileCrossVolumeMoveSourcePreflightEvidence(
                    new FileBasicMetadataEvidence(1, 2, 3, attributes),
                    SourceNamedDataStreamCount: 0,
                    SourceExtendedAttributeSize: 0));

            Assert.IsFalse(
                classification.CanStartCopy,
                $"Attribute mask 0x{attributes:X8} must be refused before cross-volume Copy.");
            Assert.AreEqual(1, classification.Blockers.Count);
            Assert.AreEqual(
                FileCrossVolumeMoveSourcePreflightBlocker.SourceUnsupportedAttributes,
                classification.Blockers[0]);
        }
    }

    [TestMethod]
    public void ReviewedStableNonReadOnlyAttributesRemainEligibleForLaterProof()
    {
        const uint hiddenSystemArchiveNotContentIndexed =
            0x00000002u |
            0x00000004u |
            0x00000020u |
            0x00002000u;

        var classification = FileCrossVolumeMoveSourcePreflightClassifier.Classify(
            new FileCrossVolumeMoveSourcePreflightEvidence(
                new FileBasicMetadataEvidence(1, 2, 3, hiddenSystemArchiveNotContentIndexed),
                SourceNamedDataStreamCount: 0,
                SourceExtendedAttributeSize: 0));

        Assert.IsTrue(classification.CanStartCopy);
        Assert.AreEqual(0, classification.Blockers.Count);
    }
}
