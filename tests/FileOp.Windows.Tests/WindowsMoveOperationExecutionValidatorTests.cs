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
public sealed class WindowsMoveOperationExecutionValidatorTests
{
    [TestMethod]
    public async Task UnsupportedNamespaceBlocksReadyMoveBeforeMutationHistory()
    {
        var ready = CreateReadyValidation(FileOperationKind.Move);
        var probe = new StubNamespaceProbe(reject: true, "case-sensitive namespace");
        var validator = new WindowsMoveOperationExecutionValidator(
            new StaticExecutionValidator(ready),
            probe);

        var result = await validator.ValidateAsync(ready.Plan);

        Assert.AreEqual(1, probe.RequireCalls);
        Assert.AreSame(ready.Plan, result.Plan);
        Assert.AreSame(ready.Items, result.Items);
        Assert.AreEqual(FileOperationExecutionValidationStatus.Blocked, result.Status);
        Assert.IsFalse(result.CanBeginMutation);
        StringAssert.Contains(result.Summary, "before durable mutation history");
        StringAssert.Contains(result.Summary, "case-sensitive namespace");
        StringAssert.Contains(result.Summary, "No MutationStarted record");
    }

    [TestMethod]
    public async Task UnavailableNamespaceCapabilityAlsoBlocksReadyMove()
    {
        var ready = CreateReadyValidation(FileOperationKind.Move);
        var probe = new StubNamespaceProbe(reject: true, "namespace capability unavailable");
        var validator = new WindowsMoveOperationExecutionValidator(
            new StaticExecutionValidator(ready),
            probe);

        var result = await validator.ValidateAsync(ready.Plan);

        Assert.AreEqual(FileOperationExecutionValidationStatus.Blocked, result.Status);
        Assert.AreEqual(1, probe.RequireCalls);
        StringAssert.Contains(result.Summary, "namespace capability unavailable");
    }

    [TestMethod]
    public async Task SupportedNamespaceReturnsOriginalReadyMoveValidation()
    {
        var ready = CreateReadyValidation(FileOperationKind.Move);
        var probe = new StubNamespaceProbe(reject: false, string.Empty);
        var validator = new WindowsMoveOperationExecutionValidator(
            new StaticExecutionValidator(ready),
            probe);

        var result = await validator.ValidateAsync(ready.Plan);

        Assert.AreSame(ready, result);
        Assert.AreEqual(1, probe.RequireCalls);
        Assert.IsTrue(result.CanBeginMutation);
    }

    [TestMethod]
    public async Task NonMoveValidationDoesNotInvokeMoveNamespaceCapability()
    {
        var ready = CreateReadyValidation(FileOperationKind.Copy);
        var probe = new StubNamespaceProbe(reject: true, "must not be called");
        var validator = new WindowsMoveOperationExecutionValidator(
            new StaticExecutionValidator(ready),
            probe);

        var result = await validator.ValidateAsync(ready.Plan);

        Assert.AreSame(ready, result);
        Assert.AreEqual(0, probe.RequireCalls);
    }

    private static FileOperationExecutionValidationResult CreateReadyValidation(FileOperationKind kind)
    {
        var sourceDirectory = Path.GetFullPath(@"C:\Source");
        var destinationDirectory = Path.GetFullPath(@"C:\Destination");
        var canonicalSourceDirectory = Path.GetFullPath(@"C:\Real\Source");
        var canonicalDestinationDirectory = Path.GetFullPath(@"C:\Real\Destination");
        var entry = new FileOperationEntry(
            Path.Combine(sourceDirectory, "a.txt"),
            "a.txt",
            IsDirectory: false);
        var plan = new FileOperationPlan(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero),
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
        var item = new FileOperationExecutionValidationItem(
            entry,
            new FileOperationCanonicalPath(
                entry.Path,
                Path.Combine(canonicalSourceDirectory, entry.Name),
                FileOperationCanonicalPathState.File,
                IsLeafReparsePoint: false,
                Identity: new FileIdentity(11, 101)),
            new FileOperationCanonicalPath(
                Path.Combine(destinationDirectory, entry.Name),
                Path.Combine(canonicalDestinationDirectory, entry.Name),
                FileOperationCanonicalPathState.Missing,
                IsLeafReparsePoint: false),
            FileOperationExecutionValidationDecision.Ready,
            "ready");

        return new FileOperationExecutionValidationResult(
            plan,
            new FileOperationCanonicalPath(
                sourceDirectory,
                canonicalSourceDirectory,
                FileOperationCanonicalPathState.Directory,
                IsLeafReparsePoint: false,
                Identity: new FileIdentity(11, 10)),
            new FileOperationCanonicalPath(
                destinationDirectory,
                canonicalDestinationDirectory,
                FileOperationCanonicalPathState.Directory,
                IsLeafReparsePoint: false,
                Identity: new FileIdentity(11, 20)),
            new[] { item },
            FileOperationExecutionValidationStatus.Ready,
            new DateTimeOffset(2026, 8, 14, 0, 1, 0, TimeSpan.Zero),
            "ready");
    }

    private sealed class StaticExecutionValidator : IFileOperationExecutionValidator
    {
        private readonly FileOperationExecutionValidationResult _validation;

        public StaticExecutionValidator(FileOperationExecutionValidationResult validation) =>
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

    private sealed class StubNamespaceProbe : IFileOperationNamespaceCapabilityProbe
    {
        private readonly bool _reject;
        private readonly string _summary;

        public StubNamespaceProbe(bool reject, string summary)
        {
            _reject = reject;
            _summary = summary;
        }

        public int RequireCalls { get; private set; }

        public FileOperationNamespaceCapability QueryDirectory(string canonicalDirectoryPath) =>
            new(
                canonicalDirectoryPath,
                _reject
                    ? FileOperationNamespaceCapabilityState.UnsupportedCaseSensitiveDirectory
                    : FileOperationNamespaceCapabilityState.SupportedCaseInsensitive,
                _summary);

        public void RequireSupportedMutationRoots(FileOperationExecutionValidationResult validation)
        {
            RequireCalls++;
            if (_reject)
            {
                throw new NotSupportedException(_summary);
            }
        }
    }
}
