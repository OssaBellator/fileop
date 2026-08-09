using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileOperationRecoveryBasicMetadataComparerTests
{
    [TestMethod]
    public void EqualStableMetadataIsSameEvenWhenIgnoredBitsMatchOrDiffer()
    {
        var recorded = new FileBasicMetadataEvidence(
            CreationTimeFileTime: 100,
            LastAccessTimeFileTime: 200,
            LastWriteTimeFileTime: 300,
            FileAttributes: FileBasicMetadataEvidence.StableCopiedAttributesMask | 0x00001000u);
        var current = recorded with
        {
            FileAttributes = FileBasicMetadataEvidence.StableCopiedAttributesMask | 0x00000100u,
        };

        var result = FileOperationRecoveryBasicMetadataComparer.Compare(recorded, current);

        Assert.AreEqual(FileOperationRecoveryBasicMetadataStatus.SameStableMetadata, result.Status);
        Assert.IsTrue(result.SameStableMetadata);
        Assert.AreEqual(true, result.CreationTimeMatches);
        Assert.AreEqual(true, result.LastWriteTimeMatches);
        Assert.AreEqual(true, result.StableCopiedAttributesMatch);
    }

    [TestMethod]
    public void LastAccessDifferenceIsDiagnosticOnly()
    {
        var recorded = new FileBasicMetadataEvidence(100, 200, 300, 0x00000021u);
        var current = recorded with { LastAccessTimeFileTime = 201 };

        var result = FileOperationRecoveryBasicMetadataComparer.Compare(recorded, current);

        Assert.AreEqual(FileOperationRecoveryBasicMetadataStatus.SameStableMetadata, result.Status);
        Assert.AreEqual(false, result.LastAccessTimeMatchesDiagnostic);
        Assert.IsTrue(result.SameStableMetadata);
    }

    [TestMethod]
    public void CreationDifferenceIsStableMetadataDifference()
    {
        var recorded = new FileBasicMetadataEvidence(100, 200, 300, 0x00000020u);
        var current = recorded with { CreationTimeFileTime = 101 };

        var result = FileOperationRecoveryBasicMetadataComparer.Compare(recorded, current);

        Assert.AreEqual(FileOperationRecoveryBasicMetadataStatus.DifferentStableMetadata, result.Status);
        Assert.AreEqual(false, result.CreationTimeMatches);
        Assert.AreEqual(true, result.LastWriteTimeMatches);
        Assert.AreEqual(true, result.StableCopiedAttributesMatch);
    }

    [TestMethod]
    public void LastWriteDifferenceIsStableMetadataDifference()
    {
        var recorded = new FileBasicMetadataEvidence(100, 200, 300, 0x00000020u);
        var current = recorded with { LastWriteTimeFileTime = 301 };

        var result = FileOperationRecoveryBasicMetadataComparer.Compare(recorded, current);

        Assert.AreEqual(FileOperationRecoveryBasicMetadataStatus.DifferentStableMetadata, result.Status);
        Assert.AreEqual(false, result.LastWriteTimeMatches);
    }

    [TestMethod]
    public void CopiedSafeAttributeDifferenceIsStableMetadataDifference()
    {
        var recorded = new FileBasicMetadataEvidence(100, 200, 300, 0x00000020u);
        var current = recorded with { FileAttributes = 0x00000021u };

        var result = FileOperationRecoveryBasicMetadataComparer.Compare(recorded, current);

        Assert.AreEqual(FileOperationRecoveryBasicMetadataStatus.DifferentStableMetadata, result.Status);
        Assert.AreEqual(false, result.StableCopiedAttributesMatch);
    }

    [TestMethod]
    public void NoRecordedEvidenceDoesNotUpgradeCurrentObservation()
    {
        var current = new FileBasicMetadataEvidence(100, 200, 300, 0x00000020u);

        var result = FileOperationRecoveryBasicMetadataComparer.Compare(recorded: null, current);

        Assert.AreEqual(FileOperationRecoveryBasicMetadataStatus.NoRecordedEvidence, result.Status);
        Assert.IsFalse(result.SameStableMetadata);
        Assert.IsNull(result.CreationTimeMatches);
    }

    [TestMethod]
    public void MissingCurrentObservationIsUnavailable()
    {
        var recorded = new FileBasicMetadataEvidence(100, 200, 300, 0x00000020u);

        var result = FileOperationRecoveryBasicMetadataComparer.Compare(recorded, current: null);

        Assert.AreEqual(FileOperationRecoveryBasicMetadataStatus.Unavailable, result.Status);
        Assert.IsFalse(result.SameStableMetadata);
        Assert.IsNull(result.LastAccessTimeMatchesDiagnostic);
    }

    [TestMethod]
    public void StableAttributeMaskMatchesCopyContract()
    {
        Assert.AreEqual(0x00002027u, FileBasicMetadataEvidence.StableCopiedAttributesMask);
    }
}
