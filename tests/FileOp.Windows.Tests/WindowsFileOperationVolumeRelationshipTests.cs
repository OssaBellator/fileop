using System;
using System.IO;
using System.Threading.Tasks;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsFileOperationVolumeRelationshipTests
{
    [TestMethod]
    public async Task TwoDirectoriesOnSameTempVolumeResolveToSameHandleBoundGuid()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "FileOp.MoveVolumeRelationship.Tests",
            Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);

        try
        {
            var resolver = new WindowsFileOperationCanonicalPathResolver();
            var sourceResolved = await resolver.ResolveAsync(source);
            var destinationResolved = await resolver.ResolveAsync(destination);
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
            StringAssert.StartsWith(result.SourceVolumeGuidName!, @"\\?\Volume{");
            StringAssert.EndsWith(result.SourceVolumeGuidName!, @"}\");
        }
        finally
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
}
