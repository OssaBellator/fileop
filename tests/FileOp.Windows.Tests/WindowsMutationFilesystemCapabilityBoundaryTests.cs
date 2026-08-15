using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsMutationFilesystemCapabilityBoundaryTests
{
    [TestMethod]
    public async Task NtfsSourceAndDestinationPreserveReadyCopyValidation()
    {
        var plan = CreateFilePlan(FileOperationKind.Copy);
        var ready = CreateReadyValidation(plan);
        var probe = new FakeProbe(
            Capability(ready.SourceDirectory, WindowsMutationFilesystemCapabilityState.SupportedNtfs, "NTFS"),
            Capability(ready.DestinationDirectory, WindowsMutationFilesystemCapabilityState.SupportedNtfs, "NTFS"));
        var validator = new WindowsNtfsMutationExecutionValidator(
            new StaticValidator(ready),
            probe);

        var result = await validator.ValidateAsync(plan);

        Assert.AreSame(ready, result);
        Assert.AreEqual(2, probe.Calls.Count);
        Assert.AreEqual(
            (ready.SourceDirectory.CanonicalPath, ready.SourceDirectory.Identity!.Value),
            probe.Calls[0]);
        Assert.AreEqual(
            (ready.DestinationDirectory.CanonicalPath, ready.DestinationDirectory.Identity!.Value),
            probe.Calls[1]);
    }

    [TestMethod]
    public async Task RefsSourceBlocksCopyBeforeDestinationCapabilityIsConsulted()
    {
        var plan = CreateFilePlan(FileOperationKind.Copy);
        var ready = CreateReadyValidation(plan);
        var probe = new FakeProbe(
            Capability(ready.SourceDirectory, WindowsMutationFilesystemCapabilityState.UnsupportedFilesystem, "ReFS"));
        var validator = new WindowsNtfsMutationExecutionValidator(
            new StaticValidator(ready),
            probe);

        var result = await validator.ValidateAsync(plan);

        Assert.AreEqual(FileOperationExecutionValidationStatus.Blocked, result.Status);
        Assert.IsFalse(result.CanBeginMutation);
        Assert.AreEqual(1, probe.Calls.Count);
        StringAssert.Contains(result.Summary, "ReFS");
        StringAssert.Contains(result.Summary, "before durable mutation history");
    }

    [TestMethod]
    public async Task RefsDestinationBlocksCopyAfterSourceNtfsProof()
    {
        var plan = CreateFilePlan(FileOperationKind.Copy);
        var ready = CreateReadyValidation(plan);
        var probe = new FakeProbe(
            Capability(ready.SourceDirectory, WindowsMutationFilesystemCapabilityState.SupportedNtfs, "NTFS"),
            Capability(ready.DestinationDirectory, WindowsMutationFilesystemCapabilityState.UnsupportedFilesystem, "ReFS"));
        var validator = new WindowsNtfsMutationExecutionValidator(
            new StaticValidator(ready),
            probe);

        var result = await validator.ValidateAsync(plan);

        Assert.AreEqual(FileOperationExecutionValidationStatus.Blocked, result.Status);
        Assert.AreEqual(2, probe.Calls.Count);
        StringAssert.Contains(result.Summary, "Destination mutation root");
        StringAssert.Contains(result.Summary, "ReFS");
    }

    [TestMethod]
    public async Task UnavailableFilesystemProofBlocksMutationReadyMove()
    {
        var plan = CreateFilePlan(FileOperationKind.Move);
        var ready = CreateReadyValidation(plan);
        var probe = new FakeProbe(
            Capability(ready.SourceDirectory, WindowsMutationFilesystemCapabilityState.Unavailable, null));
        var validator = new WindowsNtfsMoveOperationExecutionValidator(
            new StaticValidator(ready),
            probe);

        var result = await validator.ValidateAsync(plan);

        Assert.AreEqual(FileOperationExecutionValidationStatus.Blocked, result.Status);
        Assert.IsFalse(result.CanBeginMutation);
        Assert.AreEqual(1, probe.Calls.Count);
        StringAssert.Contains(result.Summary, "filesystem proof unavailable");
    }

    [TestMethod]
    public async Task RefsDeleteRootBlocksEveryReadyItemBeforeAuthorizationReview()
    {
        var plan = CreateDeletePlan();
        var ready = CreateReadyDeleteValidation(plan);
        var probe = new FakeProbe(
            Capability(ready.SourceDirectory, WindowsMutationFilesystemCapabilityState.UnsupportedFilesystem, "ReFS"));
        var validator = new WindowsNtfsFileDeleteOperationExecutionValidator(
            new StaticDeleteValidator(ready),
            probe);

        var result = await validator.ValidateAsync(plan);

        Assert.AreEqual(FileDeleteOperationExecutionValidationStatus.Blocked, result.Status);
        Assert.IsFalse(result.CanRequestAuthorizationReview);
        Assert.IsFalse(result.DeleteMutationAuthorized);
        Assert.AreEqual(0, result.ReadyForAuthorizationReviewCount);
        Assert.AreEqual(1, result.BlockedCount);
        Assert.AreEqual(FileDeleteOperationExecutionValidationDecision.Blocked, result.Items[0].Decision);
        StringAssert.Contains(result.Items[0].Message, "ReFS");
    }

    [TestMethod]
    public async Task NtfsDeleteRootPreservesAuthorizationReviewEvidenceWithoutAuthorizingDelete()
    {
        var plan = CreateDeletePlan();
        var ready = CreateReadyDeleteValidation(plan);
        var probe = new FakeProbe(
            Capability(ready.SourceDirectory, WindowsMutationFilesystemCapabilityState.SupportedNtfs, "NTFS"));
        var validator = new WindowsNtfsFileDeleteOperationExecutionValidator(
            new StaticDeleteValidator(ready),
            probe);

        var result = await validator.ValidateAsync(plan);

        Assert.AreSame(ready, result);
        Assert.IsTrue(result.CanRequestAuthorizationReview);
        Assert.IsFalse(result.DeleteMutationAuthorized);
        Assert.AreEqual(1, probe.Calls.Count);
    }

    [TestMethod]
    public async Task RealHandleBoundProbeClassifiesCurrentTempFilesystemAndRejectsStaleIdentity()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "FileOp.MutationFilesystemCapability.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var resolved = await new WindowsFileOperationCanonicalPathResolver()
                .ResolveAsync(directory);
            Assert.AreEqual(FileOperationCanonicalPathState.Directory, resolved.State);
            Assert.IsNotNull(resolved.Identity);

            var probe = new WindowsMutationFilesystemCapabilityProbe();
            var capability = probe.QueryDirectory(
                resolved.CanonicalPath,
                resolved.Identity.Value);

            Assert.AreNotEqual(
                WindowsMutationFilesystemCapabilityState.Unavailable,
                capability.State,
                capability.Summary);
            if (string.Equals(capability.FileSystemName, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                Assert.AreEqual(
                    WindowsMutationFilesystemCapabilityState.SupportedNtfs,
                    capability.State);
                Assert.IsTrue(capability.CanUseCurrentMutationIdentity);
            }
            else
            {
                Assert.AreEqual(
                    WindowsMutationFilesystemCapabilityState.UnsupportedFilesystem,
                    capability.State);
                Assert.IsFalse(capability.CanUseCurrentMutationIdentity);
            }

            var stale = new FileIdentity(
                resolved.Identity.Value.VolumeSerialNumber,
                resolved.Identity.Value.FileReferenceNumber ^ 1UL);
            var staleCapability = probe.QueryDirectory(resolved.CanonicalPath, stale);
            Assert.AreEqual(
                WindowsMutationFilesystemCapabilityState.Unavailable,
                staleCapability.State);
            Assert.IsFalse(staleCapability.CanUseCurrentMutationIdentity);
            StringAssert.Contains(staleCapability.Summary, "identity changed");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static WindowsMutationFilesystemCapability Capability(
        FileOperationCanonicalPath root,
        WindowsMutationFilesystemCapabilityState state,
        string? fileSystemName) =>
        new(
            root.CanonicalPath,
            root.Identity!.Value,
            state,
            fileSystemName,
            fileSystemName is null
                ? "filesystem proof unavailable"
                : $"filesystem is {fileSystemName}");

    private static FileOperationPlan CreateFilePlan(FileOperationKind kind)
    {
        var sourceDirectory = Path.GetFullPath(@"C:\Source");
        var destinationDirectory = Path.GetFullPath(@"D:\Destination");
        var entry = new FileOperationEntry(
            Path.Combine(sourceDirectory, "a.txt"),
            "a.txt",
            IsDirectory: false);
        return new FileOperationPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            kind,
            FileOperationCollisionPolicy.Stop,
            new FileOperationIntent(
                "Left",
                Guid.NewGuid(),
                sourceDirectory,
                new[] { entry },
                "Right",
                Guid.NewGuid(),
                destinationDirectory));
    }

    private static FileOperationExecutionValidationResult CreateReadyValidation(
        FileOperationPlan plan)
    {
        var sourceRoot = new FileOperationCanonicalPath(
            plan.Intent.SourceDirectoryPath,
            Path.GetFullPath(@"C:\Real\Source"),
            FileOperationCanonicalPathState.Directory,
            IsLeafReparsePoint: false,
            Identity: new FileIdentity(1, 10));
        var destinationRoot = new FileOperationCanonicalPath(
            plan.Intent.DestinationDirectoryPath,
            Path.GetFullPath(@"D:\Real\Destination"),
            FileOperationCanonicalPathState.Directory,
            IsLeafReparsePoint: false,
            Identity: new FileIdentity(2, 20));
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
                        IsLeafReparsePoint: false,
                        Identity: new FileIdentity(1, 100)),
                    new FileOperationCanonicalPath(
                        Path.Combine(plan.Intent.DestinationDirectoryPath, entry.Name),
                        Path.Combine(destinationRoot.CanonicalPath, entry.Name),
                        FileOperationCanonicalPathState.Missing,
                        IsLeafReparsePoint: false),
                    FileOperationExecutionValidationDecision.Ready,
                    "ready"),
            },
            FileOperationExecutionValidationStatus.Ready,
            DateTimeOffset.UtcNow,
            "ready");
    }

    private static FileDeleteOperationPlan CreateDeletePlan()
    {
        var sourceDirectory = Path.GetFullPath(@"C:\Users\Alice\Temp");
        var entry = new FileOperationEntry(
            Path.Combine(sourceDirectory, "a.tmp"),
            "a.tmp",
            IsDirectory: false);
        return new FileDeleteOperationPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new FileDeleteOperationIntent(
                "Left",
                Guid.NewGuid(),
                sourceDirectory,
                new[] { entry }));
    }

    private static FileDeleteOperationExecutionValidationResult CreateReadyDeleteValidation(
        FileDeleteOperationPlan plan)
    {
        var sourceRoot = new FileOperationCanonicalPath(
            plan.Intent.SourceDirectoryPath,
            plan.Intent.SourceDirectoryPath,
            FileOperationCanonicalPathState.Directory,
            IsLeafReparsePoint: false,
            Identity: new FileIdentity(1, 10));
        var entry = plan.Intent.Entries[0];
        var source = new FileOperationCanonicalPath(
            entry.Path,
            entry.Path,
            FileOperationCanonicalPathState.File,
            IsLeafReparsePoint: false,
            Identity: new FileIdentity(1, 11));
        return new FileDeleteOperationExecutionValidationResult(
            plan,
            sourceRoot,
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
            Assert.AreSame(_result.Plan, plan);
            return ValueTask.FromResult(_result);
        }
    }

    private sealed class StaticDeleteValidator : IFileDeleteOperationExecutionValidator
    {
        private readonly FileDeleteOperationExecutionValidationResult _result;

        public StaticDeleteValidator(FileDeleteOperationExecutionValidationResult result) =>
            _result = result;

        public ValueTask<FileDeleteOperationExecutionValidationResult> ValidateAsync(
            FileDeleteOperationPlan plan,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreSame(_result.Plan, plan);
            return ValueTask.FromResult(_result);
        }
    }

    private sealed class FakeProbe : IWindowsMutationFilesystemCapabilityProbe
    {
        private readonly Queue<WindowsMutationFilesystemCapability> _results;

        public FakeProbe(params WindowsMutationFilesystemCapability[] results) =>
            _results = new Queue<WindowsMutationFilesystemCapability>(results);

        public List<(string Path, FileIdentity Identity)> Calls { get; } = new();

        public WindowsMutationFilesystemCapability QueryDirectory(
            string canonicalDirectoryPath,
            FileIdentity expectedIdentity)
        {
            Calls.Add((canonicalDirectoryPath, expectedIdentity));
            if (_results.Count == 0)
            {
                throw new AssertFailedException("Unexpected mutation filesystem capability query.");
            }

            var result = _results.Dequeue();
            Assert.AreEqual(result.CanonicalDirectoryPath, canonicalDirectoryPath);
            Assert.AreEqual(result.ExpectedIdentity, expectedIdentity);
            return result;
        }
    }
}
