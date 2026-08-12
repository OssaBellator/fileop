using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
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
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FileNamedDataStreamTopologyEvidence(2, 0, upper));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FileNamedDataStreamTopologyEvidence(1, -1, upper));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FileNamedDataStreamTopologyEvidence(
                1,
                FileNamedDataStreamTopologyEvidence.MaximumNamedStreamCount + 1,
                upper));
        Assert.Throws<ArgumentException>(() =>
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

    [TestMethod]
    public void ParserAcceptsSpecDefinedEmptyDefaultStreamName()
    {
        const int headerBytes = 24;
        var buffer = Marshal.AllocHGlobal(headerBytes);
        try
        {
            Marshal.Copy(new byte[headerBytes], 0, buffer, headerBytes);
            var digestType = typeof(WindowsRootBoundFileNamedDataStreamTopologyEvidenceReader)
                .Assembly
                .GetType(
                    "FileOp.Windows.Operations.WindowsFileNamedDataStreamTopologyDigest",
                    throwOnError: true)!;
            var parse = digestType.GetMethod(
                "Parse",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("Named-stream topology parser was not found.");

            var parsed = parse.Invoke(null, new object[] { buffer, headerBytes }) as IEnumerable
                ?? throw new InvalidOperationException("Named-stream topology parser returned no inventory.");

            Assert.AreEqual(0, parsed.Cast<object>().Count());
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static FileNamedDataStreamTopologyEvidence Evidence(int count, char digestCharacter) =>
        new(1, count, new string(digestCharacter, 64));
}
