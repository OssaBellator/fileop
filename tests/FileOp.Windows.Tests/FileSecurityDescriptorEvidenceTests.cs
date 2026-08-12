using System;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileSecurityDescriptorEvidenceTests
{
    private const string A = "abababababababababababababababababababababababababababababababab";
    private const string B = "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd";

    [TestMethod]
    public void ExactOwnerGroupDaclMaskAndDigestAreNormalized()
    {
        var evidence = new FileSecurityDescriptorEvidence(
            FileSecurityDescriptorEvidence.QueriedSecurityInformationMask,
            A.ToUpperInvariant());

        Assert.AreEqual(0x00000007u, evidence.SecurityInformation);
        Assert.AreEqual(A, evidence.Sha256HexDigest);
    }

    [TestMethod]
    public void OtherSecurityInformationMasksAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FileSecurityDescriptorEvidence(0x0000000fu, A));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FileSecurityDescriptorEvidence(0x00000003u, A));
    }

    [TestMethod]
    public void InvalidSha256DigestsAreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new FileSecurityDescriptorEvidence(0x7u, "abcd"));
        Assert.Throws<ArgumentException>(() =>
            new FileSecurityDescriptorEvidence(0x7u, new string('z', 64)));
    }

    [TestMethod]
    public void EqualQueriedBytesCompareSame()
    {
        var recorded = Evidence(A);
        var current = Evidence(A);

        var comparison = FileOperationRecoverySecurityDescriptorComparer.Compare(recorded, current);

        Assert.AreEqual(
            FileOperationRecoverySecurityDescriptorStatus.SameQueriedDescriptorBytes,
            comparison.Status);
        Assert.AreEqual(true, comparison.SecurityInformationMaskMatches);
        Assert.AreEqual(true, comparison.Sha256Matches);
    }

    [TestMethod]
    public void DifferentDigestIsReportedSeparately()
    {
        var comparison = FileOperationRecoverySecurityDescriptorComparer.Compare(Evidence(A), Evidence(B));

        Assert.AreEqual(
            FileOperationRecoverySecurityDescriptorStatus.DifferentQueriedDescriptorBytes,
            comparison.Status);
        Assert.AreEqual(true, comparison.SecurityInformationMaskMatches);
        Assert.AreEqual(false, comparison.Sha256Matches);
    }

    [TestMethod]
    public void MissingEvidenceDoesNotUpgradeCurrentObservation()
    {
        Assert.AreEqual(
            FileOperationRecoverySecurityDescriptorStatus.NoRecordedEvidence,
            FileOperationRecoverySecurityDescriptorComparer.Compare(null, Evidence(A)).Status);
        Assert.AreEqual(
            FileOperationRecoverySecurityDescriptorStatus.Unavailable,
            FileOperationRecoverySecurityDescriptorComparer.Compare(Evidence(A), null).Status);
    }

    private static FileSecurityDescriptorEvidence Evidence(string digest) =>
        new(FileSecurityDescriptorEvidence.QueriedSecurityInformationMask, digest);
}
