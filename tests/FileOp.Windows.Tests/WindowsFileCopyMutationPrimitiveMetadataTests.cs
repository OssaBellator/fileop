using System;
using System.IO;
using System.Threading.Tasks;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsFileCopyMutationPrimitiveMetadataTests
{
    private const FileAttributes PreservedAttributes =
        FileAttributes.ReadOnly |
        FileAttributes.Hidden |
        FileAttributes.System |
        FileAttributes.Archive |
        FileAttributes.NotContentIndexed;

    [TestMethod]
    public void BasicMetadataHelperPreservesTimestampsAndSafeAttributes()
    {
        var root = CreateRoot();
        var sourcePath = Path.Combine(root, "source.bin");
        var destinationPath = Path.Combine(root, "destination.bin");
        try
        {
            File.WriteAllText(sourcePath, "source-content");
            File.WriteAllText(destinationPath, "destination-content");

            var expectedCreation = new DateTime(2021, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            var expectedAccess = new DateTime(2022, 3, 4, 5, 6, 7, DateTimeKind.Utc);
            var expectedWrite = new DateTime(2023, 4, 5, 6, 7, 8, DateTimeKind.Utc);
            SetExpectedMetadata(sourcePath, expectedCreation, expectedAccess, expectedWrite);
            File.SetAttributes(destinationPath, FileAttributes.Temporary);

            using (var sourceHandle = File.OpenHandle(
                       sourcePath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var destinationHandle = File.OpenHandle(
                       destinationPath,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                WindowsFileCopyBasicMetadata.SuppressAutomaticTimestampUpdates(destinationHandle);
                var snapshot = WindowsFileCopyBasicMetadata.Capture(sourceHandle);
                WindowsFileCopyBasicMetadata.Apply(destinationHandle, snapshot);
            }

            AssertMetadata(
                sourcePath,
                destinationPath,
                expectedCreation,
                expectedAccess,
                expectedWrite);
            Assert.IsTrue(
                (File.GetAttributes(destinationPath) & FileAttributes.Temporary) != 0,
                "Applying source basic metadata must not clear destination-owned Temporary state.");
        }
        finally
        {
            ResetAttributes(sourcePath);
            ResetAttributes(destinationPath);
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void BasicMetadataCaptureDropsUnsupportedStorageStateAttributes()
    {
        var root = CreateRoot();
        var sourcePath = Path.Combine(root, "source.bin");
        try
        {
            File.WriteAllText(sourcePath, "source-content");
            File.SetAttributes(
                sourcePath,
                FileAttributes.Hidden |
                FileAttributes.Archive |
                FileAttributes.Temporary);

            using var sourceHandle = File.OpenHandle(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var snapshot = WindowsFileCopyBasicMetadata.Capture(sourceHandle);

            Assert.AreNotEqual(0u, snapshot.FileAttributes & (uint)FileAttributes.Hidden);
            Assert.AreNotEqual(0u, snapshot.FileAttributes & (uint)FileAttributes.Archive);
            Assert.AreEqual(0u, snapshot.FileAttributes & (uint)FileAttributes.Temporary);
        }
        finally
        {
            ResetAttributes(sourcePath);
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void BasicMetadataApplyLeavesZeroTimestampsDestinationOwned()
    {
        var root = CreateRoot();
        var destinationPath = Path.Combine(root, "destination.bin");
        try
        {
            File.WriteAllText(destinationPath, "destination-content");
            var expectedCreation = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var expectedAccess = new DateTime(2021, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            var expectedWrite = new DateTime(2022, 3, 4, 5, 6, 7, DateTimeKind.Utc);
            File.SetCreationTimeUtc(destinationPath, expectedCreation);
            File.SetLastAccessTimeUtc(destinationPath, expectedAccess);
            File.SetLastWriteTimeUtc(destinationPath, expectedWrite);
            File.SetAttributes(destinationPath, FileAttributes.Temporary);

            using (var destinationHandle = File.OpenHandle(
                       destinationPath,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                WindowsFileCopyBasicMetadata.SuppressAutomaticTimestampUpdates(destinationHandle);
                WindowsFileCopyBasicMetadata.Apply(
                    destinationHandle,
                    new WindowsFileCopyBasicMetadata.Snapshot(
                        CreationTime: 0,
                        LastAccessTime: 0,
                        LastWriteTime: 0,
                        FileAttributes: (uint)FileAttributes.Hidden));
            }

            AssertTimeClose(expectedCreation, File.GetCreationTimeUtc(destinationPath));
            AssertTimeClose(expectedAccess, File.GetLastAccessTimeUtc(destinationPath));
            AssertTimeClose(expectedWrite, File.GetLastWriteTimeUtc(destinationPath));
            var actualAttributes = File.GetAttributes(destinationPath);
            Assert.IsTrue((actualAttributes & FileAttributes.Hidden) != 0);
            Assert.IsTrue((actualAttributes & FileAttributes.Temporary) != 0);
        }
        finally
        {
            ResetAttributes(destinationPath);
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void BasicMetadataMergePreservesDestinationOwnedAttributes()
    {
        var destinationSettableOwned = (uint)(
            FileAttributes.Temporary |
            FileAttributes.Offline);
        var destinationNonSettableStorage = (uint)(
            FileAttributes.SparseFile |
            FileAttributes.Compressed |
            FileAttributes.Encrypted);
        var destinationSafe = (uint)(FileAttributes.ReadOnly | FileAttributes.System);
        var sourceSafe = (uint)(FileAttributes.Hidden | FileAttributes.Archive);
        var sourceSettableButNotCopied = (uint)(
            FileAttributes.Temporary |
            FileAttributes.Offline);
        var sourceNonSettableStorage = (uint)(
            FileAttributes.SparseFile |
            FileAttributes.ReparsePoint |
            FileAttributes.Compressed |
            FileAttributes.Encrypted);

        var merged = WindowsFileCopyBasicMetadata.MergeDestinationAttributes(
            destinationSettableOwned |
                destinationNonSettableStorage |
                destinationSafe,
            sourceSafe |
                sourceSettableButNotCopied |
                sourceNonSettableStorage |
                (uint)FileAttributes.Normal);

        Assert.AreEqual(sourceSafe, merged & (uint)PreservedAttributes);
        Assert.AreEqual(destinationSettableOwned, merged & destinationSettableOwned);
        Assert.AreEqual(0u, merged & destinationNonSettableStorage);
        Assert.AreEqual(0u, merged & (uint)FileAttributes.ReparsePoint);
        Assert.AreEqual(0u, merged & (uint)FileAttributes.Normal);

        var unsupportedSourceOnly = WindowsFileCopyBasicMetadata.MergeDestinationAttributes(
            (uint)FileAttributes.Normal,
            sourceSettableButNotCopied | sourceNonSettableStorage);
        Assert.AreEqual(
            (uint)FileAttributes.Normal,
            unsupportedSourceOnly,
            "Unsupported source state must not be introduced; Normal is the explicit clear-all encoding when no retained attributes remain.");
    }

    [TestMethod]
    public async Task CopyPrimitivePreservesSafeBasicMetadata()
    {
        var root = CreateRoot();
        var sourceDirectory = Path.Combine(root, "source");
        var destinationDirectory = Path.Combine(root, "destination");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var sourcePath = Path.Combine(sourceDirectory, "payload.bin");
        var destinationPath = Path.Combine(destinationDirectory, "payload.bin");
        try
        {
            File.WriteAllText(sourcePath, "source-content");
            var expectedCreation = new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc);
            var expectedAccess = new DateTime(2021, 6, 7, 8, 9, 10, DateTimeKind.Utc);
            var expectedWrite = new DateTime(2022, 7, 8, 9, 10, 11, DateTimeKind.Utc);

            var entry = new FileOperationEntry(sourcePath, "payload.bin", IsDirectory: false);
            var plan = new FileOperationPlan(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                FileOperationKind.Copy,
                FileOperationCollisionPolicy.Stop,
                new FileOperationIntent(
                    "Left",
                    Guid.NewGuid(),
                    sourceDirectory,
                    new[] { entry },
                    "Right",
                    Guid.NewGuid(),
                    destinationDirectory));
            var validation = await new WindowsFileOperationExecutionValidator().ValidateAsync(plan);
            Assert.AreEqual(FileOperationExecutionValidationDecision.Ready, validation.Items[0].Decision);

            // Establish the metadata contract after validation so validation-side
            // handle activity cannot make LastAccessTime expectations machine-policy dependent.
            // Basic metadata changes do not alter the stable file identity carried by validation.
            SetExpectedMetadata(sourcePath, expectedCreation, expectedAccess, expectedWrite);

            var lease = await new WindowsFileCopyMutationPrimitive().CopyNewFileAsync(
                new FileCopyMutationRequest(
                    validation.Items[0],
                    validation.SourceDirectory,
                    validation.DestinationDirectory));
            await lease.DisposeAsync();

            // Inspect metadata before any subsequent content read can legitimately
            // update LastAccessTime through a different handle.
            AssertMetadata(
                sourcePath,
                destinationPath,
                expectedCreation,
                expectedAccess,
                expectedWrite);
            Assert.AreEqual("source-content", await File.ReadAllTextAsync(destinationPath));
        }
        finally
        {
            ResetAttributes(sourcePath);
            ResetAttributes(destinationPath);
            DeleteRoot(root);
        }
    }

    private static void SetExpectedMetadata(
        string path,
        DateTime creation,
        DateTime access,
        DateTime write)
    {
        File.SetCreationTimeUtc(path, creation);
        File.SetLastAccessTimeUtc(path, access);
        File.SetLastWriteTimeUtc(path, write);
        File.SetAttributes(
            path,
            FileAttributes.ReadOnly |
            FileAttributes.Hidden |
            FileAttributes.System |
            FileAttributes.Archive |
            FileAttributes.NotContentIndexed);
    }

    private static void AssertMetadata(
        string sourcePath,
        string destinationPath,
        DateTime expectedCreation,
        DateTime expectedAccess,
        DateTime expectedWrite)
    {
        AssertTimeClose(expectedCreation, File.GetCreationTimeUtc(destinationPath));
        AssertTimeClose(expectedAccess, File.GetLastAccessTimeUtc(destinationPath));
        AssertTimeClose(expectedWrite, File.GetLastWriteTimeUtc(destinationPath));
        Assert.AreEqual(
            File.GetAttributes(sourcePath) & PreservedAttributes,
            File.GetAttributes(destinationPath) & PreservedAttributes);
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "FileOp.CopyMetadata.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void AssertTimeClose(DateTime expected, DateTime actual)
    {
        var difference = (expected - actual).Duration();
        Assert.IsTrue(
            difference <= TimeSpan.FromSeconds(2),
            $"Expected timestamp near {expected:O}, got {actual:O}.");
    }

    private static void ResetAttributes(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteRoot(string root)
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
