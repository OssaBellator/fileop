using System;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileNamedDataStreamTopologyEvidenceTests
{
    [TestMethod]
    public void EvidenceNormalizesDigestAndRequiresCurrentFormat()
    {
        var upper = new string('A', 64);
        var evidence = new FileNamedDataStreamTopologyEvidence(
            FileNamedDataStreamTopologyEvidence.CurrentFormatVersion,
            2,
            upper);

        Assert.AreEqual(1, evidence.FormatVersion);
        Assert.AreEqual(2, evidence.NamedStreamCount);
        Assert.AreEqual(new string('a', 64), evidence.Sha256HexDigest);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new FileNamedDataStreamTopologyEvidence(2, 0, upper));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new FileNamedDataStreamTopologyEvidence(1, -1, upper));
        Assert.ThrowsException<ArgumentException>(() =>
            new FileNamedDataStreamTopologyEvidence(1, 0, new string('z', 64)));
    }

    [TestMethod]
    public void ComparerSeparatesMissingUnavailableAndChangedTopology()
    {
        var recorded = Evidence(1, 'a');
        var same = Evidence(1, 'a');
        var differentDigest = Evidence(1, 'b');
        var differentCount = Evidence(2, 'a');

        Assert.AreEqual(
            FileOperationRecoveryNamedDataStreamTopologyStatus.NoRecordedEvidence,
            FileOperationRecoveryNamedDataStreamTopologyComparer.Compare(null, same).Status);
        Assert.AreEqual(
            FileOperationRecoveryNamedDataStreamTopologyStatus.Unavailable,
            FileOperationRecoveryNamedDataStreamTopologyComparer.Compare(recorded, null).Status);
        Assert.AreEqual(
            FileOperationRecoveryNamedDataStreamTopologyStatus.SameNamesAndSizes,
            FileOperationRecoveryNamedDataStreamTopologyComparer.Compare(recorded, same).Status);
        Assert.AreEqual(
            FileOperationRecoveryNamedDataStreamTopologyStatus.DifferentNamesOrSizes,
            FileOperationRecoveryNamedDataStreamTopologyComparer.Compare(recorded, differentDigest).Status);
        Assert.AreEqual(
            FileOperationRecoveryNamedDataStreamTopologyStatus.DifferentNamesOrSizes,
            FileOperationRecoveryNamedDataStreamTopologyComparer.Compare(recorded, differentCount).Status);
    }

    private static FileNamedDataStreamTopologyEvidence Evidence(int count, char digestCharacter) =>
        new(1, count, new string(digestCharacter, 64));
}
