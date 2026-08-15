using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsMutationFilesystemCapabilityBindingTests
{
    [TestMethod]
    public async Task SupportedNtfsEvidenceForDifferentIdentityDoesNotAuthorizeCopyValidation()
    {
        var plan = CreateFilePlan();
        var ready = CreateReadyFileValidation(plan);
        var source = ready.SourceDirectory;
        var mismatched = new WindowsMutationFilesystemCapability(
            source.CanonicalPath,
            new FileIdentity(
                source.Identity!.Value.VolumeSerialNumber,
                source.Identity.Value.FileReferenceNumber + 1),
            WindowsMutationFilesystemCapabilityState.SupportedNtfs,
            "NTFS",
            "misbound supported evidence");
        var validator = new WindowsNtfsMutationExecutionValidator(
            new StaticValidator(ready),
            new SingleResultProbe(mismatched));

        var result = await validator.ValidateAsync(plan);

        Assert.AreEqual(FileOperationExecutionValidationStatus.Blocked, result.Status);
        Assert.IsFalse(result.CanBeginMutation);
        StringAssert.Contains(result.Summary, "not exact NTFS evidence");
    }

    [TestMethod]
    public async Task SupportedStateWithNonNtfsFilesystemNameDoesNotAuthorizeCopyValidation()
    {
        var plan = CreateFilePlan();
        var ready = CreateReadyFileValidation(plan);
        var source = ready.SourceDirectory;
        var contradictory = new WindowsMutationFilesystemCapability(
            source.CanonicalPath,
            source.Identity!.Value,
            WindowsMutationFilesystemCapabilityState.SupportedNtfs,
            "ReFS",
            "contradictory supported evidence");
        Assert.IsFalse(contradictory.CanUseCurrentMutationIdentity);
        var validator = new WindowsNtfsMutationExecutionValidator(
            new StaticValidator(ready),
            new SingleResultProbe(contradictory));

        var result = await validator.ValidateAsync(plan);

        Assert.AreEqual(FileOperationExecutionValidationStatus.Blocked, result.Status);
        Assert.IsFalse(result.CanBeginMutation);
    }

    [TestMethod]
    public async Task SupportedNtfsEvidenceForDifferentPathDoesNotReachDeleteAuthorizationReview()
    {
        var plan = CreateDeletePlan();
        var ready = CreateReadyDeleteValidation(plan);
        var mismatched = new WindowsMutationFilesystemCapability(
            Path.GetFullPath(@"C:\DifferentRoot"),
            ready.SourceDirectory.Identity!.Value,
            WindowsMutationFilesystemCapabilityState.SupportedNtfs,
            "NTFS",
            "wrong-root supported evidence");
        var validator = new WindowsNtfsFileDeleteOperationExecutionValidator(
            new StaticDeleteValidator(ready),
            new SingleResultProbe(mismatched));

        var result = await validator.ValidateAsync(plan);

        Assert.AreEqual(FileDeleteOperationExecutionValidationStatus.Blocked, result.Status);
        Assert.IsFalse(result.CanRequestAuthorizationReview);
        Assert.AreEqual(FileDeleteOperationExecutionValidationDecision.Blocked, result.Items[0].Decision);
        StringAssert.Contains(result.Summary, "not exact NTFS evidence");
    }

    private static FileOperationPlan CreateFilePlan()
    {
        var sourceRoot = Path.GetFullPath(@"C:\Source");
        var destinationRoot = Path.GetFullPath(@"D:\Destination");
        var entry = new FileOperationEntry(
            Path.Combine(sourceRoot, "a.txt"),
            "a.txt",
            false);
        return new FileOperationPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            FileOperationKind.Copy,
            FileOperationCollisionPolicy.Stop,
            new FileOperationIntent(
                "Left",
                Guid.NewGuid(),
                sourceRoot,
                new[] { entry },
                "Right",
                Guid.NewGuid(),
                destinationRoot));
    }

    private static FileOperationExecutionValidationResult CreateReadyFileValidation(
        FileOperationPlan plan)
    {
        var sourceRoot = new FileOperationCanonicalPath(
            plan.Intent.SourceDirectoryPath,
            Path.GetFullPath(@"C:\Real\Source"),
            FileOperationCanonicalPathState.Directory,
            false,
            new FileIdentity(1, 10));
        var destinationRoot = new FileOperationCanonicalPath(
            plan.Intent.DestinationDirectoryPath,
            Path.GetFullPath(@"D:\Real\Destination"),
            FileOperationCanonicalPathState.Directory,
            false,
            new FileIdentity(2, 20));
        var entry = plan.Intent.Entries[0];
        return new FileOperationExecutionValidationResult(
            plan,
            sourceRoot,
            destinationRoot,
            new[]
            {
                new FileOperationExecutionValidationItem(
                    entry,
                    new FileOperationCanonicalPath(
                        entry.Path,
                        Path.Combine(sourceRoot.CanonicalPath, entry.Name),
                        FileOperationCanonicalPathState.File,
                        false,
                        new FileIdentity(1, 100)),
                    new FileOperationCanonicalPath(
                        Path.Combine(plan.Intent.DestinationDirectoryPath, entry.Name),
                        Path.Combine(destinationRoot.CanonicalPath, entry.Name),
                        FileOperationCanonicalPathState.Missing,
                        false),
                    FileOperationExecutionValidationDecision.Ready,
                    "ready"),
            },
            FileOperationExecutionValidationStatus.Ready,
            DateTimeOffset.UtcNow,
            "ready");
    }

    private static FileDeleteOperationPlan CreateDeletePlan()
    {
        var root = Path.GetFullPath(@"C:\Users\Alice\Temp");
        var entry = new FileOperationEntry(Path.Combine(root, "a.tmp"), "a.tmp", false);
        return new FileDeleteOperationPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new FileDeleteOperationIntent("Left", Guid.NewGuid(), root, new[] { entry }));
    }

    private static FileDeleteOperationExecutionValidationResult CreateReadyDeleteValidation(
        FileDeleteOperationPlan plan)
    {
        var root = new FileOperationCanonicalPath(
            plan.Intent.SourceDirectoryPath,
            plan.Intent.SourceDirectoryPath,
            FileOperationCanonicalPathState.Directory,
            false,
            new FileIdentity(1, 10));
        var entry = plan.Intent.Entries[0];
        var source = new FileOperationCanonicalPath(
            entry.Path,
            entry.Path,
            FileOperationCanonicalPathState.File,
            false,
            new FileIdentity(1, 11));
        return new FileDeleteOperationExecutionValidationResult(
            plan,
            root,
            new[]
            {
                new FileDeleteOperationExecutionValidationItem(
                    entry,
                    source,
                    FileDeleteOperationExecutionValidationDecision.ReadyForAuthorizationReview,
                    "ready"),
            },
            FileDeleteOperationExecutionValidationStatus.ReadyForAuthorizationReview,
            DateTimeOffset.UtcNow,
            "ready");
    }

    private sealed class StaticValidator : IFileOperationExecutionValidator
    {
        private readonly FileOperationExecutionValidationResult _result;

        public StaticValidator(FileOperationExecutionValidationResult result) => _result = result;

        public ValueTask<FileOperationExecutionValidationResult> ValidateAsync(
            FileOperationPlan plan,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_result);
        }
    }

    private sealed class StaticDeleteValidator : IFileDeleteOperationExecutionValidator
    {
        private readonly FileDeleteOperationExecutionValidationResult _result;

        public StaticDeleteValidator(FileDeleteOperationExecutionValidationResult result) => _result = result;

        public ValueTask<FileDeleteOperationExecutionValidationResult> ValidateAsync(
            FileDeleteOperationPlan plan,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_result);
        }
    }

    private sealed class SingleResultProbe : IWindowsMutationFilesystemCapabilityProbe
    {
        private readonly WindowsMutationFilesystemCapability _result;

        public SingleResultProbe(WindowsMutationFilesystemCapability result) => _result = result;

        public WindowsMutationFilesystemCapability QueryDirectory(
            string canonicalDirectoryPath,
            FileIdentity expectedIdentity) => _result;
    }
}
