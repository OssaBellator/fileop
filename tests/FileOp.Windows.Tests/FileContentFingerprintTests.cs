using System;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileContentFingerprintTests
{
    [TestMethod]
    public void Sha256FingerprintNormalizesHexToLowercase()
    {
        var fingerprint = new FileContentFingerprint(
            FileContentFingerprintAlgorithm.Sha256,
            "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789");

        Assert.AreEqual(FileContentFingerprintAlgorithm.Sha256, fingerprint.Algorithm);
        Assert.AreEqual(
            "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
            fingerprint.HexDigest);
    }

    [TestMethod]
    public void Sha256FingerprintRejectsWrongLengthOrNonHexDigest()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new FileContentFingerprint(FileContentFingerprintAlgorithm.Sha256, "abc"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new FileContentFingerprint(
                FileContentFingerprintAlgorithm.Sha256,
                new string('g', FileContentFingerprint.Sha256HexLength)));
    }

    [TestMethod]
    public void UnsupportedFingerprintAlgorithmIsRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new FileContentFingerprint(
                (FileContentFingerprintAlgorithm)99,
                new string('0', FileContentFingerprint.Sha256HexLength)));
    }
}
