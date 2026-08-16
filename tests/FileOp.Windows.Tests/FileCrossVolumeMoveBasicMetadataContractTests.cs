using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileCrossVolumeMoveBasicMetadataContractTests
{
    private const uint FileAttributeReadOnly = 0x00000001u;
    private const uint FileAttributeHidden = 0x00000002u;
    private const uint FileAttributeSystem = 0x00000004u;
    private const uint FileAttributeArchive = 0x00000020u;
    private const uint FileAttributeNormal = 0x00000080u;
    private const uint FileAttributeTemporary = 0x00000100u;
    private const uint FileAttributeOffline = 0x00001000u;
    private const uint FileAttributeNotContentIndexed = 0x00002000u;

    [TestMethod]
    public void StableCopiedAttributeMaskMatchesReviewedCopyWriterContract()
    {
        const uint expected =
            FileAttributeReadOnly |
            FileAttributeHidden |
            FileAttributeSystem |
            FileAttributeArchive |
            FileAttributeNotContentIndexed;

        Assert.AreEqual(expected, FileBasicMetadataEvidence.StableCopiedAttributesMask);
    }

    [TestMethod]
    public void CopyMergeReplacesOnlyStableCopiedBitsAndRetainsDestinationOwnedBits()
    {
        var sourceAttributes = FileAttributeHidden | FileAttributeArchive;
        var destinationAttributes =
            FileAttributeReadOnly |
            FileAttributeTemporary |
            FileAttributeOffline;

        var merged = WindowsFileCopyBasicMetadata.MergeDestinationAttributes(
            sourceAttributes,
            destinationAttributes);

        Assert.AreEqual(
            FileAttributeHidden |
            FileAttributeArchive |
            FileAttributeTemporary |
            FileAttributeOffline,
            merged);
        Assert.AreEqual(
            sourceAttributes,
            merged & FileBasicMetadataEvidence.StableCopiedAttributesMask);
    }

    [TestMethod]
    public void RecoveryComparerTreatsCreationLastWriteAndStableAttributesAsAuthoritative()
    {
        var expected = new FileBasicMetadataEvidence(
            CreationTimeFileTime: 10,
            LastAccessTimeFileTime: 20,
            LastWriteTimeFileTime: 30,
            FileAttributes: FileAttributeHidden | FileAttributeArchive);

        var onlyLastAccessDiffers = expected with { LastAccessTimeFileTime = 999 };
        Assert.IsTrue(
            FileOperationRecoveryBasicMetadataComparer.Compare(expected, onlyLastAccessDiffers)
                .SameStableMetadata,
            "Last-access time is diagnostic and is not part of the reviewed stable Copy/Move metadata contract.");

        Assert.IsFalse(
            FileOperationRecoveryBasicMetadataComparer.Compare(
                expected,
                expected with { CreationTimeFileTime = 11 }).SameStableMetadata);
        Assert.IsFalse(
            FileOperationRecoveryBasicMetadataComparer.Compare(
                expected,
                expected with { LastWriteTimeFileTime = 31 }).SameStableMetadata);
        Assert.IsFalse(
            FileOperationRecoveryBasicMetadataComparer.Compare(
                expected,
                expected with { FileAttributes = FileAttributeSystem | FileAttributeArchive }).SameStableMetadata);
    }

    [TestMethod]
    public void DestinationOwnedTemporaryOfflineBitsDoNotChangeStableMetadataComparison()
    {
        var source = new FileBasicMetadataEvidence(
            CreationTimeFileTime: 10,
            LastAccessTimeFileTime: 20,
            LastWriteTimeFileTime: 30,
            FileAttributes: FileAttributeReadOnly | FileAttributeArchive);
        var destination = source with
        {
            FileAttributes = source.FileAttributes | FileAttributeTemporary | FileAttributeOffline,
        };

        var comparison = FileOperationRecoveryBasicMetadataComparer.Compare(source, destination);

        Assert.IsTrue(comparison.SameStableMetadata);
        Assert.AreEqual(
            source.PreservedStableAttributes,
            destination.PreservedStableAttributes);
    }

    [TestMethod]
    public void NormalAttributeDoesNotSurviveWhenCopyMergeProducesOtherAttributes()
    {
        var merged = WindowsFileCopyBasicMetadata.MergeDestinationAttributes(
            FileAttributeHidden,
            FileAttributeNormal | FileAttributeTemporary);

        Assert.AreEqual(0u, merged & FileAttributeNormal);
        Assert.AreNotEqual(0u, merged & FileAttributeHidden);
        Assert.AreNotEqual(0u, merged & FileAttributeTemporary);
    }
}
