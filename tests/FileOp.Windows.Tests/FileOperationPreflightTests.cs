using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileOperationPreflightTests
{
    [TestMethod]
    public void IntentDefensivelySnapshotsEntries()
    {
        var sourceDirectory = Path.GetFullPath(@"C:\Source");
        var destinationDirectory = Path.GetFullPath(@"C:\Destination");
        var entries = new List<FileOperationEntry>
        {
            new(Path.Combine(sourceDirectory, "a.txt"), "a.txt", IsDirectory: false),
        };
        var intent = new FileOperationIntent(
            "Left",
            Guid.NewGuid(),
            sourceDirectory,
            entries,
            "Right",
            Guid.NewGuid(),
            destinationDirectory);

        entries.Clear();

        Assert.AreEqual(1, intent.Entries.Count);
        Assert.AreEqual("a.txt", intent.Entries[0].Name);
    }

    [TestMethod]
    public async Task MissingDestinationIsReady()
    {
        var (validator, plan) = CreateSingleFileCase(
            FileOperationCollisionPolicy.Ask,
            destinationState: FileOperationPathState.Missing);

        var result = await validator.ValidateAsync(plan);

        Assert.AreEqual(FileOperationPreflightStatus.Ready, result.Status);
        Assert.AreEqual(1, result.ReadyCount);
        Assert.AreEqual(0, result.BlockedCount);
        Assert.IsTrue(result.CanProceedToExecutionValidation);
    }

    [TestMethod]
    public async Task AskCollisionRequiresDecision()
    {
        var (validator, plan) = CreateSingleFileCase(
            FileOperationCollisionPolicy.Ask,
            destinationState: FileOperationPathState.File);

        var result = await validator.ValidateAsync(plan);

        Assert.AreEqual(FileOperationPreflightStatus.NeedsDecision, result.Status);
        Assert.AreEqual(1, result.NeedsDecisionCount);
        Assert.IsFalse(result.CanProceedToExecutionValidation);
    }

    [TestMethod]
    public async Task SkipCollisionRemainsReadOnlyReady()
    {
        var (validator, plan) = CreateSingleFileCase(
            FileOperationCollisionPolicy.Skip,
            destinationState: FileOperationPathState.File);

        var result = await validator.ValidateAsync(plan);

        Assert.AreEqual(FileOperationPreflightStatus.Ready, result.Status);
        Assert.AreEqual(1, result.SkipCount);
        Assert.AreEqual(FileOperationPreflightDecision.Skip, result.Items[0].Decision);
    }

    [TestMethod]
    public async Task StopCollisionBlocksPlan()
    {
        var (validator, plan) = CreateSingleFileCase(
            FileOperationCollisionPolicy.Stop,
            destinationState: FileOperationPathState.Directory);

        var result = await validator.ValidateAsync(plan);

        Assert.AreEqual(FileOperationPreflightStatus.Blocked, result.Status);
        Assert.AreEqual(1, result.BlockedCount);
    }

    [TestMethod]
    public async Task ReparsePointSourceIsBlocked()
    {
        var (validator, plan) = CreateSingleFileCase(
            FileOperationCollisionPolicy.Ask,
            destinationState: FileOperationPathState.Missing,
            sourceReparsePoint: true);

        var result = await validator.ValidateAsync(plan);

        Assert.AreEqual(FileOperationPreflightStatus.Blocked, result.Status);
        Assert.AreEqual(1, result.BlockedCount);
    }

    [TestMethod]
    public async Task DirectoryCannotTargetOwnDescendant()
    {
        var sourceDirectory = Path.GetFullPath(@"C:\Source");
        var sourcePath = Path.Combine(sourceDirectory, "Folder");
        var destinationDirectory = Path.Combine(sourcePath, "Nested");
        var probe = new FakeProbe(new Dictionary<string, FileOperationPathInspection>(StringComparer.OrdinalIgnoreCase)
        {
            [sourceDirectory] = Inspection(sourceDirectory, FileOperationPathState.Directory),
            [destinationDirectory] = Inspection(destinationDirectory, FileOperationPathState.Directory),
        });
        var plan = CreatePlan(
            sourceDirectory,
            destinationDirectory,
            new FileOperationEntry(sourcePath, "Folder", IsDirectory: true),
            FileOperationCollisionPolicy.Ask);

        var result = await new WindowsFileOperationPreflightValidator(probe).ValidateAsync(plan);

        Assert.AreEqual(FileOperationPreflightStatus.Blocked, result.Status);
        Assert.AreEqual(1, result.BlockedCount);
    }

    private static (WindowsFileOperationPreflightValidator Validator, FileOperationPlan Plan) CreateSingleFileCase(
        FileOperationCollisionPolicy collisionPolicy,
        FileOperationPathState destinationState,
        bool sourceReparsePoint = false)
    {
        var sourceDirectory = Path.GetFullPath(@"C:\Source");
        var destinationDirectory = Path.GetFullPath(@"C:\Destination");
        var sourcePath = Path.Combine(sourceDirectory, "a.txt");
        var destinationPath = Path.Combine(destinationDirectory, "a.txt");
        var probe = new FakeProbe(new Dictionary<string, FileOperationPathInspection>(StringComparer.OrdinalIgnoreCase)
        {
            [sourceDirectory] = Inspection(sourceDirectory, FileOperationPathState.Directory),
            [destinationDirectory] = Inspection(destinationDirectory, FileOperationPathState.Directory),
            [sourcePath] = Inspection(sourcePath, FileOperationPathState.File, sourceReparsePoint),
            [destinationPath] = Inspection(destinationPath, destinationState),
        });
        var plan = CreatePlan(
            sourceDirectory,
            destinationDirectory,
            new FileOperationEntry(sourcePath, "a.txt", IsDirectory: false),
            collisionPolicy);
        return (new WindowsFileOperationPreflightValidator(probe), plan);
    }

    private static FileOperationPlan CreatePlan(
        string sourceDirectory,
        string destinationDirectory,
        FileOperationEntry entry,
        FileOperationCollisionPolicy collisionPolicy) =>
        new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            FileOperationKind.Copy,
            collisionPolicy,
            new FileOperationIntent(
                "Left",
                Guid.NewGuid(),
                sourceDirectory,
                new[] { entry },
                "Right",
                Guid.NewGuid(),
                destinationDirectory));

    private static FileOperationPathInspection Inspection(
        string path,
        FileOperationPathState state,
        bool reparsePoint = false) =>
        new(path, state, reparsePoint);

    private sealed class FakeProbe : IFileOperationPathProbe
    {
        private readonly IReadOnlyDictionary<string, FileOperationPathInspection> _inspections;

        public FakeProbe(IReadOnlyDictionary<string, FileOperationPathInspection> inspections)
        {
            _inspections = inspections;
        }

        public ValueTask<FileOperationPathInspection> InspectAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = Path.GetFullPath(path);
            return ValueTask.FromResult(
                _inspections.TryGetValue(normalized, out var inspection)
                    ? inspection
                    : Inspection(normalized, FileOperationPathState.Missing));
        }
    }
}
