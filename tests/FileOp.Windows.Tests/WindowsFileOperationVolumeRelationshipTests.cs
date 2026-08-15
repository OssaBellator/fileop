using System;
using System.IO;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsFileOperationVolumeRelationshipTests
{
    [TestMethod]
    public async Task TwoDirectoriesOnSameTempVolumeResolveToSameHandleBoundGuid()
    {
        var fixture = CreateFixture();
        try
        {
            var resolver = new WindowsFileOperationCanonicalPathResolver();
            var sourceResolved = await resolver.ResolveAsync(fixture.Source);
            var destinationResolved = await resolver.ResolveAsync(fixture.Destination);
            Assert.IsTrue(sourceResolved.Identity.HasValue);
            Assert.IsTrue(destinationResolved.Identity.HasValue);

            var result = new WindowsFileOperationVolumeRelationshipProbe().Query(
                sourceResolved.CanonicalPath,
                sourceResolved.Identity.Value,
                destinationResolved.CanonicalPath,
                destinationResolved.Identity.Value);

            Assert.AreEqual(
                FileOperationVolumeRelationshipState.SameVolume,
                result.State,
                result.Summary);
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.SourceVolumeGuidName));
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.DestinationVolumeGuidName));
            Assert.IsTrue(
                string.Equals(
                    result.SourceVolumeGuidName,
                    result.DestinationVolumeGuidName,
                    StringComparison.OrdinalIgnoreCase),
                "Two directories on the same temp filesystem must resolve to the same handle-bound Windows volume GUID.");
            Assert.IsTrue(
                result.SourceVolumeGuidName!.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(
                result.SourceVolumeGuidName.EndsWith(@"}\", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(fixture.Root);
        }
    }

    [TestMethod]
    public async Task StaleExpectedRootIdentityMakesVolumeRelationshipUnavailable()
    {
        var fixture = CreateFixture();
        try
        {
            var resolver = new WindowsFileOperationCanonicalPathResolver();
            var sourceResolved = await resolver.ResolveAsync(fixture.Source);
            var destinationResolved = await resolver.ResolveAsync(fixture.Destination);
            Assert.IsTrue(sourceResolved.Identity.HasValue);
            Assert.IsTrue(destinationResolved.Identity.HasValue);

            var sourceIdentity = sourceResolved.Identity.Value;
            var staleSourceIdentity = new FileIdentity(
                sourceIdentity.VolumeSerialNumber,
                sourceIdentity.FileReferenceNumber == ulong.MaxValue
                    ? sourceIdentity.FileReferenceNumber - 1
                    : sourceIdentity.FileReferenceNumber + 1);

            var result = new WindowsFileOperationVolumeRelationshipProbe().Query(
                sourceResolved.CanonicalPath,
                staleSourceIdentity,
                destinationResolved.CanonicalPath,
                destinationResolved.Identity.Value);

            Assert.AreEqual(
                FileOperationVolumeRelationshipState.Unavailable,
                result.State,
                result.Summary);
            Assert.IsNull(result.SourceVolumeGuidName);
            Assert.IsNull(result.DestinationVolumeGuidName);
            StringAssert.Contains(result.Summary, "filesystem identity changed before volume relationship proof");
        }
        finally
        {
            TryDelete(fixture.Root);
        }
    }

    private static (string Root, string Source, string Destination) CreateFixture()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "FileOp.MoveVolumeRelationship.Tests",
            Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        return (root, source, destination);
    }

    private static void TryDelete(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
