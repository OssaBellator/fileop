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
public sealed class WindowsMutationFilesystemCapabilityPolicyTests
{
    [TestMethod]
    public async Task SkipOnlyCopyDoesNotRequireFilesystemCapabilityProof()
    {
        var plan = CreatePlan(FileOperationKind.Copy);
        var skipOnly = CreateSkipOnlyValidation(plan);
        var probe = new NeverProbe();
        var validator = new WindowsNtfsMutationExecutionValidator(
            new StaticValidator(skipOnly),
            probe);

        var result = await validator.ValidateAsync(plan);

        Assert.AreSame(skipOnly, result);
        Assert.IsTrue(result.CanBeginMutation);
        Assert.AreEqual(FileOperationExecutionValidationDecision.Skip, result.Items[0].Decision);
        Assert.AreEqual(0, probe.CallCount);
    }

    [TestMethod]
    public async Task SkipOnlyMoveDoesNotRequireFilesystemCapabilityProof()
    {
        var plan = CreatePlan(FileOperationKind.Move);
        var skipOnly = CreateSkipOnlyValidation(plan);
        var probe = new NeverProbe();
        var validator = new WindowsNtfsMoveOperationExecutionValidator(
            new StaticValidator(skipOnly),
            probe);

        var result = await validator.ValidateAsync(plan);

        Assert.AreSame(skipOnly, result);
        Assert.AreEqual(0, probe.CallCount);
    }

    [TestMethod]
    public async Task MissingCapabilityEvidenceBlocksReadyCopyWithoutThrowing()
    {
        var plan = CreatePlan(FileOperationKind.Copy);
        var ready = CreateReadyValidation(plan);
        var validator = new WindowsNtfsMutationExecutionValidator(
            new StaticValidator(ready),
            new NullProbe());

        var result = await validator.ValidateAsync(plan);

        Assert.AreEqual(FileOperationExecutionValidationStatus.Blocked, result.Status);
        Assert.IsFalse(result.CanBeginMutation);
        StringAssert.Contains(result.Summary, "provider returned no evidence");
    }

    [TestMethod]
    public async Task MissingCapabilityEvidenceBlocksDeleteBeforeAuthorizationReviewWithoutThrowing()
    {
        var plan = CreateDeletePlan();
        var ready = CreateReadyDeleteValidation(plan);
        var validator = new WindowsNtfsFileDeleteOperationExecutionValidator(
            new StaticDeleteValidator(ready),
            new NullProbe());

        var result = await validator.ValidateAsync(plan);

        Assert.AreEqual(FileDeleteOperationExecutionValidationStatus.Blocked, result.Status);
        Assert.IsFalse(result.CanRequestAuthorizationReview);
        Assert.IsFalse(result.DeleteMutationAuthorized);
        StringAssert.Contains(result.Summary, "provider returned no evidence");
    }

    private static FileOperationPlan CreatePlan(FileOperationKind kind)
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
            kind,
            FileOperationCollisionPolicy.Skip,
            new FileOperationIntent(
                "Left",
                Guid.NewGuid(),
                sourceRoot,
                new[] { entry },
                "Right",
                Guid.NewGuid(),
                destinationRoot));
    }

    private static FileOperationExecutionValidationResult CreateSkipOnlyValidation(
        FileOperationPlan plan) =>
        CreateValidation(plan, FileOperationExecutionValidationDecision.Skip);

    private static FileOperationExecutionValidationResult CreateReadyValidation(
        FileOperationPlan plan) =>
        CreateValidation(plan, FileOperationExecutionValidationDecision.Ready);

    private static FileOperationExecutionValidationResult CreateValidation(
        FileOperationPlan plan,
        FileOperationExecutionValidationDecision decision)
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
        var destinationState = decision == FileOperationExecutionValidationDecision.Skip
            ? FileOperationCanonicalPathState.File
            : FileOperationCanonicalPathState.Missing;
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
                        destinationState,
                        false,
                        destinationState == FileOperationCanonicalPathState.File
                            ? new FileIdentity(2, 200)
                            : null),
                    decision,
                    decision == FileOperationExecutionValidationDecision.Skip ? "skip" : "ready"),
            },
            FileOperationExecutionValidationStatus.Ready,
            DateTimeOffset.UtcNow,
            decision == FileOperationExecutionValidationDecision.Skip ? "skip-only" : "ready");
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
            Assert.AreSame(_result.Plan, plan);
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
            Assert.AreSame(_result.Plan, plan);
            return ValueTask.FromResult(_result);
        }
    }

    private sealed class NeverProbe : IWindowsMutationFilesystemCapabilityProbe
    {
        public int CallCount { get; private set; }

        public WindowsMutationFilesystemCapability QueryDirectory(
            string canonicalDirectoryPath,
            FileIdentity expectedIdentity)
        {
            CallCount++;
            throw new AssertFailedException(
                "Skip-only Copy/Move must not require mutation filesystem capability proof.");
        }
    }

    private sealed class NullProbe : IWindowsMutationFilesystemCapabilityProbe
    {
        public WindowsMutationFilesystemCapability QueryDirectory(
            string canonicalDirectoryPath,
            FileIdentity expectedIdentity) => null!;
    }
}
