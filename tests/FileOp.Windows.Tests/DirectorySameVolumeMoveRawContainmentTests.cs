using System;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class DirectorySameVolumeMoveRawContainmentTests
{
    [TestMethod]
    public async Task DestinationParentInsideSourceIsRejectedBeforeCapabilityProbes()
    {
        var sourceRoot = new FileOperationCanonicalPath(
            @"C:\Source",
            @"C:\Source",
            FileOperationCanonicalPathState.Directory,
            false,
            new FileIdentity(7, 70));
        var source = new FileOperationCanonicalPath(
            @"C:\Source\MoveMe",
            @"C:\Source\MoveMe",
            FileOperationCanonicalPathState.Directory,
            false,
            new FileIdentity(7, 71));
        var destinationRoot = new FileOperationCanonicalPath(
            @"C:\Source\MoveMe\nested",
            @"C:\Source\MoveMe\nested",
            FileOperationCanonicalPathState.Directory,
            false,
            new FileIdentity(7, 72));
        var destination = new FileOperationCanonicalPath(
            @"C:\Source\MoveMe\nested\MoveMe",
            @"C:\Source\MoveMe\nested\MoveMe",
            FileOperationCanonicalPathState.Missing,
            false);
        var entry = new FileOperationEntry(source.CanonicalPath, "MoveMe", IsDirectory: true);
        var request = new DirectorySameVolumeMoveMutationRequest(
            new FileOperationExecutionValidationItem(
                entry,
                source,
                destination,
                FileOperationExecutionValidationDecision.Ready,
                "forged ready evidence"),
            sourceRoot,
            destinationRoot);
        var filesystemProbe = new CountingFilesystemProbe();
        var relationshipProbe = new CountingRelationshipProbe();
        var inner = new CountingMutationPrimitive();
        var guarded = new WindowsNtfsDirectorySameVolumeMoveMutationPrimitive(
            inner,
            filesystemProbe,
            relationshipProbe);

        await AssertThrowsAsync<NotSupportedException>(async () =>
            await guarded.RenameDirectoryAsync(request));

        Assert.AreEqual(0, filesystemProbe.CallCount);
        Assert.AreEqual(0, relationshipProbe.CallCount);
        Assert.AreEqual(0, inner.CallCount);
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        Assert.Fail($"Expected {typeof(TException).Name} to be thrown.");
    }

    private sealed class CountingFilesystemProbe : IWindowsMutationFilesystemCapabilityProbe
    {
        public int CallCount { get; private set; }

        public WindowsMutationFilesystemCapability QueryDirectory(
            string canonicalDirectoryPath,
            FileIdentity expectedIdentity)
        {
            CallCount++;
            return new WindowsMutationFilesystemCapability(
                canonicalDirectoryPath,
                expectedIdentity,
                WindowsMutationFilesystemCapabilityState.SupportedNtfs,
                "NTFS",
                "supported");
        }
    }

    private sealed class CountingRelationshipProbe : IFileOperationVolumeRelationshipProbe
    {
        public int CallCount { get; private set; }

        public FileOperationVolumeRelationship Query(
            string canonicalSourceDirectoryPath,
            FileIdentity expectedSourceIdentity,
            string canonicalDestinationDirectoryPath,
            FileIdentity expectedDestinationIdentity)
        {
            CallCount++;
            return new FileOperationVolumeRelationship(
                FileOperationVolumeRelationshipState.SameVolume,
                @"\\?\Volume{same}\",
                @"\\?\Volume{same}\",
                "same");
        }
    }

    private sealed class CountingMutationPrimitive : IDirectorySameVolumeMoveMutationPrimitive
    {
        public int CallCount { get; private set; }

        public ValueTask<IDirectorySameVolumeMoveMutationLease> RenameDirectoryAsync(
            DirectorySameVolumeMoveMutationRequest request)
        {
            CallCount++;
            throw new AssertFailedException("The raw mutation primitive must not be reached.");
        }
    }
}
