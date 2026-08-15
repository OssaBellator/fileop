using System;
using System.IO;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsFileOperationVolumeRelationshipTests
{
    [TestMethod]
    public void TwoDirectoriesOnSameTempVolumeResolveToSameHandleBoundGuid()
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
            var result = new WindowsFileOperationVolumeRelationshipProbe()
                .Query(Path.GetFullPath(source), Path.GetFullPath(destination));

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
