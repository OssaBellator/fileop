using System;
using System.IO;
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
            File.SetCreationTimeUtc(sourcePath, expectedCreation);
            File.SetLastAccessTimeUtc(sourcePath, expectedAccess);
            File.SetLastWriteTimeUtc(sourcePath, expectedWrite);
            File.SetAttributes(
                sourcePath,
                FileAttributes.ReadOnly |
                FileAttributes.Hidden |
                FileAttributes.Archive |
                FileAttributes.NotContentIndexed);

            using (var sourceHandle = File.OpenHandle(
                       sourcePath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var destinationHandle = File.OpenHandle(
                       destinationPath,
                       FileMode.Open,
                       FileAccess.Write,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                var snapshot = WindowsFileCopyBasicMetadata.Capture(sourceHandle);
                WindowsFileCopyBasicMetadata.Apply(destinationHandle, snapshot);
            }

            AssertTimeClose(expectedCreation, File.GetCreationTimeUtc(destinationPath));
            AssertTimeClose(expectedAccess, File.GetLastAccessTimeUtc(destinationPath));
            AssertTimeClose(expectedWrite, File.GetLastWriteTimeUtc(destinationPath));
            Assert.AreEqual(
                File.GetAttributes(sourcePath) & PreservedAttributes,
                File.GetAttributes(destinationPath) & PreservedAttributes);
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
