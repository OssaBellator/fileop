using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileCrossVolumeMoveSourcePreflightPolicyTests
{
    [TestMethod]
    public async Task SameVolumeMoveBypassesCrossVolumeSourcePreflight()
    {
        var plan = CreatePlan();
        var inner = new FakeValidator(CreateValidation(plan, destinationVolumeSerial: 1));
        var probe = new CountingProbe(BlockedClassification());
        var validator = new FileCrossVolumeMovePreflightExecutionValidator(inner, probe);

        var result = await validator.ValidateAsync(plan);

        Assert.AreEqual(FileOperationExecutionValidationStatus.Ready, result.Status);
        Assert.AreEqual(FileOperationExecutionValidationDecision.Ready, result.Items[0].Decision);
        Assert.AreEqual(0, probe.CallCount);
    }

    [TestMethod]
    public async Task ProtectedSourceFileBlocksBeforeInnerEvidenceProbe()
    {
        var request = CreatePreflightRequest();
        var inner = new CountingProbe(AllowedClassification());
        var policy = new FakeProtectedLocationPolicy(request.CanonicalSourcePath);
        var probe = new FileCrossVolumeMoveProtectedLocationPreflightProbe(inner, policy);

        var result = await probe.ProbeAsync(request);

        Assert.IsFalse(result.CanStartCopy);
        CollectionAssert.AreEqual(
            new[] { FileCrossVolumeMoveSourcePreflightBlocker.SourceProtectedLocation },
            (System.Collections.ICollection)result.Blockers);
        Assert.AreEqual(0, inner.CallCount);
        Assert.AreEqual(2, policy.CallCount);
        StringAssert.Contains(result.Summary, "protected from later source deletion");
        StringAssert.Contains(result.Summary, "re-evaluated at the final source-delete capability boundary");
    }

    [TestMethod]
    public async Task AllowedProtectedLocationPolicyContinuesToInnerEvidenceProbe()
    {
        var request = CreatePreflightRequest();
        var expected = AllowedClassification();
        var inner = new CountingProbe(expected);
        var policy = new FakeProtectedLocationPolicy(blockedPath: null);
        var probe = new FileCrossVolumeMoveProtectedLocationPreflightProbe(inner, policy);

        var result = await probe.ProbeAsync(request);

        Assert.AreSame(expected, result);
        Assert.AreEqual(1, inner.CallCount);
        Assert.AreEqual(2, policy.CallCount);
    }

    private static FileCrossVolumeMoveSourcePreflightRequest CreatePreflightRequest() =>
        new(
            Guid.NewGuid(),
            0,
            new FileOperationEntry(@"C:\Real\Source\a.txt", "a.txt", IsDirectory: false),
            @"C:\Real\Source",
            new FileIdentity(1, 10),
            @"C:\Real\Source\a.txt",
            new FileIdentity(1, 100));

    private static FileCrossVolumeMoveSourcePreflightClassification AllowedClassification() =>
        new(
            CanStartCopy: true,
            Array.Empty<FileCrossVolumeMoveSourcePreflightBlocker>(),
            "allowed");

    private static FileCrossVolumeMoveSourcePreflightClassification BlockedClassification() =>
        new(
            CanStartCopy: false,
            new[] { FileCrossVolumeMoveSourcePreflightBlocker.SourceNamedDataStreams },
            "blocked");

    private static FileOperationPlan CreatePlan()
    {
        var sourceDirectory = Path.GetFullPath(@"C:\Source");
        var destinationDirectory = Path.GetFullPath(@"C:\Destination");
        var entry = new FileOperationEntry(
            Path.Combine(sourceDirectory, "a.txt"),
            "a.txt",
            IsDirectory: false);
        return new FileOperationPlan(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero),
            FileOperationKind.Move,
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

    private static FileOperationExecutionValidationResult CreateValidation(
        FileOperationPlan plan,
        uint destinationVolumeSerial)
    {
        var canonicalSourceDirectory = Path.GetFullPath(@"C:\Real\Source");
        var canonicalDestinationDirectory = Path.GetFullPath(@"C:\Real\Destination");
        var entry = plan.Intent.Entries[0];
        return new FileOperationExecutionValidationResult(
            plan,
            new FileOperationCanonicalPath(
                plan.Intent.SourceDirectoryPath,
                canonicalSourceDirectory,
                FileOperationCanonicalPathState.Directory,
                IsLeafReparsePoint: false,
                Identity: new FileIdentity(1, 10)),
            new FileOperationCanonicalPath(
                plan.Intent.DestinationDirectoryPath,
                canonicalDestinationDirectory,
                FileOperationCanonicalPathState.Directory,
                IsLeafReparsePoint: false,
                Identity: new FileIdentity(destinationVolumeSerial, 20)),
            new[]
            {
                new FileOperationExecutionValidationItem(
                    entry,
                    new FileOperationCanonicalPath(
                        entry.Path,
                        Path.Combine(canonicalSourceDirectory, entry.Name),
                        FileOperationCanonicalPathState.File,
                        IsLeafReparsePoint: false,
                        Identity: new FileIdentity(1, 100)),
                    new FileOperationCanonicalPath(
                        Path.Combine(plan.Intent.DestinationDirectoryPath, entry.Name),
                        Path.Combine(canonicalDestinationDirectory, entry.Name),
                        FileOperationCanonicalPathState.Missing,
                        IsLeafReparsePoint: false),
                    FileOperationExecutionValidationDecision.Ready,
                    "ready"),
            },
            FileOperationExecutionValidationStatus.Ready,
            new DateTimeOffset(2026, 8, 15, 0, 1, 0, TimeSpan.Zero),
            "ready");
    }

    private sealed class FakeValidator : IFileOperationExecutionValidator
    {
        private readonly FileOperationExecutionValidationResult _validation;

        public FakeValidator(FileOperationExecutionValidationResult validation) =>
            _validation = validation;

        public ValueTask<FileOperationExecutionValidationResult> ValidateAsync(
            FileOperationPlan plan,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreSame(_validation.Plan, plan);
            return ValueTask.FromResult(_validation);
        }
    }

    private sealed class CountingProbe : IFileCrossVolumeMoveSourcePreflightProbe
    {
        private readonly FileCrossVolumeMoveSourcePreflightClassification _classification;

        public CountingProbe(FileCrossVolumeMoveSourcePreflightClassification classification) =>
            _classification = classification;

        public int CallCount { get; private set; }

        public ValueTask<FileCrossVolumeMoveSourcePreflightClassification> ProbeAsync(
            FileCrossVolumeMoveSourcePreflightRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(_classification);
        }
    }

    private sealed class FakeProtectedLocationPolicy : IFileDeleteProtectedLocationPolicy
    {
        private readonly string? _blockedPath;

        public FakeProtectedLocationPolicy(string? blockedPath) => _blockedPath = blockedPath;

        public int CallCount { get; private set; }

        public FileDeleteProtectedLocationResult Evaluate(string canonicalPath)
        {
            CallCount++;
            if (_blockedPath is not null &&
                string.Equals(canonicalPath, _blockedPath, StringComparison.OrdinalIgnoreCase))
            {
                return new FileDeleteProtectedLocationResult(
                    FileDeleteProtectedLocationDecision.Blocked,
                    "protected test path");
            }

            return new FileDeleteProtectedLocationResult(
                FileDeleteProtectedLocationDecision.AllowedForReview,
                "allowed test path");
        }
    }
}
