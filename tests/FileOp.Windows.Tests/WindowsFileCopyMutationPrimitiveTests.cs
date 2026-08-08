using System;
using System.IO;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsFileCopyMutationPrimitiveTests
{
    [TestMethod]
    public async Task CopyCreatesExclusiveIdentityBoundDestinationAndLeaseBlocksDelete()
    {
        using var fixture = new CopyFixture();
        var payload = new byte[(1024 * 1024) + 137];
        new Random(20260808).NextBytes(payload);
        await File.WriteAllBytesAsync(fixture.SourcePath, payload);
        var validation = await fixture.ValidateAsync();
        var item = validation.Items[0];
        Assert.AreEqual(FileOperationExecutionValidationDecision.Ready, item.Decision);
        Assert.IsNotNull(item.MutationRootBinding);
        Assert.AreEqual(validation.SourceDirectory.Identity, item.MutationRootBinding.SourceDirectoryIdentity);
        Assert.AreEqual(validation.DestinationDirectory.Identity, item.MutationRootBinding.DestinationDirectoryIdentity);

        var primitive = new WindowsFileCopyMutationPrimitive();
        var lease = await primitive.CopyNewFileAsync(item);
        try
        {
            CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(fixture.DestinationPath));
            Assert.AreEqual(item.Source.Identity, lease.Receipt.SourceIdentity);
            Assert.IsFalse(lease.Receipt.DestinationIdentity == lease.Receipt.SourceIdentity);

            var resolver = new WindowsFileOperationCanonicalPathResolver();
            var created = await resolver.ResolveAsync(fixture.DestinationPath);
            Assert.AreEqual(FileOperationCanonicalPathState.File, created.State);
            Assert.AreEqual(lease.Receipt.DestinationIdentity, created.Identity);

            var deleteWasBlocked = false;
            try
            {
                File.Delete(fixture.DestinationPath);
            }
            catch (IOException)
            {
                deleteWasBlocked = true;
            }
            catch (UnauthorizedAccessException)
            {
                deleteWasBlocked = true;
            }

            Assert.IsTrue(deleteWasBlocked, "The mutation lease must deny delete/rename sharing until durable commit and progress complete.");
            Assert.IsTrue(File.Exists(fixture.DestinationPath));
        }
        finally
        {
            await lease.DisposeAsync();
        }

        File.Delete(fixture.DestinationPath);
        Assert.IsFalse(File.Exists(fixture.DestinationPath));
    }

    [TestMethod]
    public async Task CollisionAppearingAfterValidationNeverOverwritesExistingFile()
    {
        using var fixture = new CopyFixture();
        await File.WriteAllTextAsync(fixture.SourcePath, "source-content");
        var validation = await fixture.ValidateAsync();
        Assert.AreEqual(FileOperationExecutionValidationDecision.Ready, validation.Items[0].Decision);

        await File.WriteAllTextAsync(fixture.DestinationPath, "existing-content");
        var primitive = new WindowsFileCopyMutationPrimitive();
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await primitive.CopyNewFileAsync(validation.Items[0]));

        Assert.AreEqual("existing-content", await File.ReadAllTextAsync(fixture.DestinationPath));
    }

    [TestMethod]
    public async Task SourceIdentityReplacementAfterValidationFailsBeforeDestinationCreation()
    {
        using var fixture = new CopyFixture();
        await File.WriteAllTextAsync(fixture.SourcePath, "original-content");
        var validation = await fixture.ValidateAsync();
        Assert.AreEqual(FileOperationExecutionValidationDecision.Ready, validation.Items[0].Decision);

        var originalPath = fixture.SourcePath + ".original";
        File.Move(fixture.SourcePath, originalPath);
        await File.WriteAllTextAsync(fixture.SourcePath, "replacement-content");

        var primitive = new WindowsFileCopyMutationPrimitive();
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await primitive.CopyNewFileAsync(validation.Items[0]));

        Assert.IsFalse(File.Exists(fixture.DestinationPath));
        Assert.AreEqual("original-content", await File.ReadAllTextAsync(originalPath));
    }

    [TestMethod]
    public async Task DestinationRootReplacementAfterValidationFailsBeforeCreation()
    {
        using var fixture = new CopyFixture();
        await File.WriteAllTextAsync(fixture.SourcePath, "source-content");
        var validation = await fixture.ValidateAsync();
        Assert.AreEqual(FileOperationExecutionValidationDecision.Ready, validation.Items[0].Decision);

        var originalDestination = fixture.DestinationDirectory + ".original";
        Directory.Move(fixture.DestinationDirectory, originalDestination);
        Directory.CreateDirectory(fixture.DestinationDirectory);

        var primitive = new WindowsFileCopyMutationPrimitive();
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await primitive.CopyNewFileAsync(validation.Items[0]));

        Assert.IsFalse(File.Exists(fixture.DestinationPath));
        Assert.IsFalse(File.Exists(Path.Combine(originalDestination, "payload.bin")));
    }

    [TestMethod]
    public async Task ValidationItemCopyPreservesMutationRootBinding()
    {
        using var fixture = new CopyFixture();
        await File.WriteAllTextAsync(fixture.SourcePath, "source-content");
        var validation = await fixture.ValidateAsync();
        var original = validation.Items[0];
        var copied = original with { Message = original.Message + " copied" };

        Assert.IsNotNull(original.MutationRootBinding);
        Assert.AreEqual(original.MutationRootBinding, copied.MutationRootBinding);
    }

    [TestMethod]
    public async Task PrimitiveRejectsNonReadyOrDirectoryValidationItems()
    {
        using var fixture = new CopyFixture();
        await File.WriteAllTextAsync(fixture.SourcePath, "source-content");
        var validation = await fixture.ValidateAsync();
        var item = validation.Items[0];
        var primitive = new WindowsFileCopyMutationPrimitive();

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await primitive.CopyNewFileAsync(item with
            {
                Decision = FileOperationExecutionValidationDecision.Skip,
            }));

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await primitive.CopyNewFileAsync(item with
            {
                Entry = item.Entry with { IsDirectory = true },
            }));
    }

    private sealed class CopyFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "FileOp.WindowsCopy.Tests",
            Guid.NewGuid().ToString("N"));

        public CopyFixture()
        {
            SourceDirectory = Path.Combine(_root, "source");
            DestinationDirectory = Path.Combine(_root, "destination");
            Directory.CreateDirectory(SourceDirectory);
            Directory.CreateDirectory(DestinationDirectory);
            SourcePath = Path.Combine(SourceDirectory, "payload.bin");
            DestinationPath = Path.Combine(DestinationDirectory, "payload.bin");
        }

        public string SourceDirectory { get; }

        public string DestinationDirectory { get; }

        public string SourcePath { get; }

        public string DestinationPath { get; }

        public async Task<FileOperationExecutionValidationResult> ValidateAsync()
        {
            var entry = new FileOperationEntry(SourcePath, "payload.bin", IsDirectory: false);
            var plan = new FileOperationPlan(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                FileOperationKind.Copy,
                FileOperationCollisionPolicy.Stop,
                new FileOperationIntent(
                    "Left",
                    Guid.NewGuid(),
                    SourceDirectory,
                    new[] { entry },
                    "Right",
                    Guid.NewGuid(),
                    DestinationDirectory));
            return await new WindowsFileOperationExecutionValidator().ValidateAsync(plan);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
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
