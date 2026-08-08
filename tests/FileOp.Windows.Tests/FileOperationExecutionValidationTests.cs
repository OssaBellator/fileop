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
public sealed class FileOperationExecutionValidationTests
{
    [TestMethod]
    public async Task MissingCanonicalDestinationIsReady()
    {
        var (validator, plan) = CreateSingleFileCase(
            FileOperationCollisionPolicy.Ask,
            destinationState: FileOperationCanonicalPathState.Missing);

        var result = await validator.ValidateAsync(plan);

        Assert.AreEqual(FileOperationExecutionValidationStatus.Ready, result.Status);
        Assert.AreEqual(1, result.ReadyCount);
        Assert.AreEqual(0, result.BlockedCount);
        Assert.IsTrue(result.CanBeginMutation);
    }

    [TestMethod]
    public async Task AskCollisionRequiresDecision()
    {
        var (validator, plan) = CreateSingleFileCase(
            FileOperationCollisionPolicy.Ask,
            destinationState: FileOperationCanonicalPathState.File);

        var result = await validator.ValidateAsync(plan);

        Assert.AreEqual(FileOperationExecutionValidationStatus.NeedsDecision, result.Status);
        Assert.AreEqual(1, result.NeedsDecisionCount);
        Assert.IsFalse(result.CanBeginMutation);
    }

    [TestMethod]
    public async Task SkipCollisionIsReadyButSkipped()
    {
        var (validator, plan) = CreateSingleFileCase(
            FileOperationCollisionPolicy.Skip,
            destinationState: FileOperationCanonicalPathState.File);

        var result = await validator.ValidateAsync(plan);

        Assert.AreEqual(FileOperationExecutionValidationStatus.Ready, result.Status);
        Assert.AreEqual(1, result.SkipCount);
        Assert.AreEqual(
            FileOperationExecutionValidationDecision.Skip,
            result.Items[0].Decision);
    }

    [TestMethod]
    public async Task CanonicalRootAliasIsBlocked()
    {
        var sourceDirectory = Path.GetFullPath(@"C:\Source");
        var destinationDirectory = Path.GetFullPath(@"D:\Destination");
        var canonicalDirectory = Path.GetFullPath(@"C:\Real\Shared");
        var sourcePath = Path.Combine(sourceDirectory, "a.txt");
        var destinationPath = Path.Combine(destinationDirectory, "a.txt");
        var resolver = new FakeResolver(new Dictionary<string, FileOperationCanonicalPath>(StringComparer.OrdinalIgnoreCase)
        {
            [sourceDirectory] = Directory(sourceDirectory, canonicalDirectory, new FileIdentity(1, 10)),
            [destinationDirectory] = Directory(destinationDirectory, canonicalDirectory, new FileIdentity(1, 10)),
            [sourcePath] = File(sourcePath, Path.Combine(canonicalDirectory, "a.txt"), new FileIdentity(1, 11)),
            [destinationPath] = Missing(destinationPath, Path.Combine(canonicalDirectory, "a.txt")),
        });
        var plan = CreatePlan(
            sourceDirectory,
            destinationDirectory,
            new FileOperationEntry(sourcePath, "a.txt", IsDirectory: false),
            FileOperationCollisionPolicy.Ask);

        var result = await new WindowsFileOperationExecutionValidator(resolver).ValidateAsync(plan);

        Assert.AreEqual(FileOperationExecutionValidationStatus.Blocked, result.Status);
        StringAssert.Contains(result.Summary, "same canonical directory");
    }

    [TestMethod]
    public async Task SourceResolvingOutsideCanonicalRootIsBlocked()
    {
        var sourceDirectory = Path.GetFullPath(@"C:\Source");
        var destinationDirectory = Path.GetFullPath(@"D:\Destination");
        var sourcePath = Path.Combine(sourceDirectory, "a.txt");
        var destinationPath = Path.Combine(destinationDirectory, "a.txt");
        var canonicalSourceRoot = Path.GetFullPath(@"C:\Real\Source");
        var canonicalDestinationRoot = Path.GetFullPath(@"D:\Real\Destination");
        var resolver = new FakeResolver(new Dictionary<string, FileOperationCanonicalPath>(StringComparer.OrdinalIgnoreCase)
        {
            [sourceDirectory] = Directory(sourceDirectory, canonicalSourceRoot, new FileIdentity(1, 10)),
            [destinationDirectory] = Directory(destinationDirectory, canonicalDestinationRoot, new FileIdentity(2, 20)),
            [sourcePath] = File(sourcePath, Path.GetFullPath(@"E:\Escaped\a.txt"), new FileIdentity(3, 30)),
            [destinationPath] = Missing(destinationPath, Path.Combine(canonicalDestinationRoot, "a.txt")),
        });
        var plan = CreatePlan(
            sourceDirectory,
            destinationDirectory,
            new FileOperationEntry(sourcePath, "a.txt", IsDirectory: false),
            FileOperationCollisionPolicy.Ask);

        var result = await new WindowsFileOperationExecutionValidator(resolver).ValidateAsync(plan);

        Assert.AreEqual(FileOperationExecutionValidationStatus.Blocked, result.Status);
        StringAssert.Contains(result.Items[0].Message, "outside the canonical source directory");
    }

    [TestMethod]
    public async Task HardLinkAliasDestinationIsBlocked()
    {
        var sourceIdentity = new FileIdentity(1, 100);
        var (validator, plan) = CreateSingleFileCase(
            FileOperationCollisionPolicy.Skip,
            destinationState: FileOperationCanonicalPathState.File,
            sourceIdentity: sourceIdentity,
            destinationIdentity: sourceIdentity);

        var result = await validator.ValidateAsync(plan);

        Assert.AreEqual(FileOperationExecutionValidationStatus.Blocked, result.Status);
        StringAssert.Contains(result.Items[0].Message, "same filesystem object");
    }

    [TestMethod]
    public async Task DirectoryCannotTargetCanonicalDescendant()
    {
        var sourceDirectory = Path.GetFullPath(@"C:\Source");
        var destinationDirectory = Path.GetFullPath(@"D:\Destination");
        var sourcePath = Path.Combine(sourceDirectory, "Folder");
        var destinationPath = Path.Combine(destinationDirectory, "Folder");
        var canonicalSourceRoot = Path.GetFullPath(@"C:\Real\Source");
        var canonicalSourcePath = Path.Combine(canonicalSourceRoot, "Folder");
        var canonicalDestinationRoot = Path.Combine(canonicalSourcePath, "Nested");
        var resolver = new FakeResolver(new Dictionary<string, FileOperationCanonicalPath>(StringComparer.OrdinalIgnoreCase)
        {
            [sourceDirectory] = Directory(sourceDirectory, canonicalSourceRoot, new FileIdentity(1, 10)),
            [destinationDirectory] = Directory(destinationDirectory, canonicalDestinationRoot, new FileIdentity(1, 20)),
            [sourcePath] = Directory(sourcePath, canonicalSourcePath, new FileIdentity(1, 30)),
            [destinationPath] = Missing(destinationPath, Path.Combine(canonicalDestinationRoot, "Folder")),
        });
        var plan = CreatePlan(
            sourceDirectory,
            destinationDirectory,
            new FileOperationEntry(sourcePath, "Folder", IsDirectory: true),
            FileOperationCollisionPolicy.Ask);

        var result = await new WindowsFileOperationExecutionValidator(resolver).ValidateAsync(plan);

        Assert.AreEqual(FileOperationExecutionValidationStatus.Blocked, result.Status);
        StringAssert.Contains(result.Items[0].Message, "descendants");
    }

    [TestMethod]
    public async Task LeafReparseSourceIsBlocked()
    {
        var sourceDirectory = Path.GetFullPath(@"C:\Source");
        var destinationDirectory = Path.GetFullPath(@"D:\Destination");
        var sourcePath = Path.Combine(sourceDirectory, "a.txt");
        var destinationPath = Path.Combine(destinationDirectory, "a.txt");
        var canonicalSourceRoot = Path.GetFullPath(@"C:\Real\Source");
        var canonicalDestinationRoot = Path.GetFullPath(@"D:\Real\Destination");
        var resolver = new FakeResolver(new Dictionary<string, FileOperationCanonicalPath>(StringComparer.OrdinalIgnoreCase)
        {
            [sourceDirectory] = Directory(sourceDirectory, canonicalSourceRoot, new FileIdentity(1, 10)),
            [destinationDirectory] = Directory(destinationDirectory, canonicalDestinationRoot, new FileIdentity(2, 20)),
            [sourcePath] = new FileOperationCanonicalPath(
                sourcePath,
                Path.Combine(canonicalSourceRoot, "a.txt"),
                FileOperationCanonicalPathState.File,
                IsLeafReparsePoint: true,
                Identity: new FileIdentity(1, 100)),
            [destinationPath] = Missing(destinationPath, Path.Combine(canonicalDestinationRoot, "a.txt")),
        });
        var plan = CreatePlan(
            sourceDirectory,
            destinationDirectory,
            new FileOperationEntry(sourcePath, "a.txt", IsDirectory: false),
            FileOperationCollisionPolicy.Ask);

        var result = await new WindowsFileOperationExecutionValidator(resolver).ValidateAsync(plan);

        Assert.AreEqual(FileOperationExecutionValidationStatus.Blocked, result.Status);
        StringAssert.Contains(result.Items[0].Message, "itself a reparse point");
    }

    [TestMethod]
    public void ResultDefensivelySnapshotsItems()
    {
        var sourceDirectory = Directory(
            Path.GetFullPath(@"C:\Source"),
            Path.GetFullPath(@"C:\Real\Source"),
            new FileIdentity(1, 1));
        var destinationDirectory = Directory(
            Path.GetFullPath(@"D:\Destination"),
            Path.GetFullPath(@"D:\Real\Destination"),
            new FileIdentity(2, 2));
        var entry = new FileOperationEntry(
            Path.Combine(sourceDirectory.RequestedPath, "a.txt"),
            "a.txt",
            IsDirectory: false);
        var plan = CreatePlan(
            sourceDirectory.RequestedPath,
            destinationDirectory.RequestedPath,
            entry,
            FileOperationCollisionPolicy.Ask);
        var items = new List<FileOperationExecutionValidationItem>
        {
            new(
                entry,
                File(entry.Path, Path.Combine(sourceDirectory.CanonicalPath, "a.txt"), new FileIdentity(1, 3)),
                Missing(
                    Path.Combine(destinationDirectory.RequestedPath, "a.txt"),
                    Path.Combine(destinationDirectory.CanonicalPath, "a.txt")),
                FileOperationExecutionValidationDecision.Ready,
                "ready"),
        };

        var result = new FileOperationExecutionValidationResult(
            plan,
            sourceDirectory,
            destinationDirectory,
            items,
            FileOperationExecutionValidationStatus.Ready,
            DateTimeOffset.UtcNow,
            "ready");

        items.Clear();

        Assert.AreEqual(1, result.Items.Count);
    }

    private static (
        WindowsFileOperationExecutionValidator Validator,
        FileOperationPlan Plan) CreateSingleFileCase(
        FileOperationCollisionPolicy collisionPolicy,
        FileOperationCanonicalPathState destinationState,
        FileIdentity? sourceIdentity = null,
        FileIdentity? destinationIdentity = null)
    {
        var sourceDirectory = Path.GetFullPath(@"C:\Source");
        var destinationDirectory = Path.GetFullPath(@"D:\Destination");
        var sourcePath = Path.Combine(sourceDirectory, "a.txt");
        var destinationPath = Path.Combine(destinationDirectory, "a.txt");
        var canonicalSourceRoot = Path.GetFullPath(@"C:\Real\Source");
        var canonicalDestinationRoot = Path.GetFullPath(@"D:\Real\Destination");
        sourceIdentity ??= new FileIdentity(1, 100);
        destinationIdentity ??= destinationState is FileOperationCanonicalPathState.File or
            FileOperationCanonicalPathState.Directory
                ? new FileIdentity(2, 200)
                : null;

        var destination = destinationState switch
        {
            FileOperationCanonicalPathState.Missing =>
                Missing(destinationPath, Path.Combine(canonicalDestinationRoot, "a.txt")),
            FileOperationCanonicalPathState.File =>
                File(destinationPath, Path.Combine(canonicalDestinationRoot, "a.txt"), destinationIdentity!.Value),
            FileOperationCanonicalPathState.Directory =>
                Directory(destinationPath, Path.Combine(canonicalDestinationRoot, "a.txt"), destinationIdentity!.Value),
            _ => new FileOperationCanonicalPath(
                destinationPath,
                destinationPath,
                destinationState,
                IsLeafReparsePoint: false),
        };

        var resolver = new FakeResolver(new Dictionary<string, FileOperationCanonicalPath>(StringComparer.OrdinalIgnoreCase)
        {
            [sourceDirectory] = Directory(sourceDirectory, canonicalSourceRoot, new FileIdentity(1, 10)),
            [destinationDirectory] = Directory(destinationDirectory, canonicalDestinationRoot, new FileIdentity(2, 20)),
            [sourcePath] = File(sourcePath, Path.Combine(canonicalSourceRoot, "a.txt"), sourceIdentity.Value),
            [destinationPath] = destination,
        });
        var plan = CreatePlan(
            sourceDirectory,
            destinationDirectory,
            new FileOperationEntry(sourcePath, "a.txt", IsDirectory: false),
            collisionPolicy);
        return (new WindowsFileOperationExecutionValidator(resolver), plan);
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

    private static FileOperationCanonicalPath Directory(
        string requested,
        string canonical,
        FileIdentity identity) =>
        new(
            requested,
            canonical,
            FileOperationCanonicalPathState.Directory,
            IsLeafReparsePoint: false,
            Identity: identity);

    private static FileOperationCanonicalPath File(
        string requested,
        string canonical,
        FileIdentity identity) =>
        new(
            requested,
            canonical,
            FileOperationCanonicalPathState.File,
            IsLeafReparsePoint: false,
            Identity: identity);

    private static FileOperationCanonicalPath Missing(
        string requested,
        string canonical) =>
        new(
            requested,
            canonical,
            FileOperationCanonicalPathState.Missing,
            IsLeafReparsePoint: false);

    private sealed class FakeResolver : IFileOperationCanonicalPathResolver
    {
        private readonly IReadOnlyDictionary<string, FileOperationCanonicalPath> _paths;

        public FakeResolver(IReadOnlyDictionary<string, FileOperationCanonicalPath> paths)
        {
            _paths = paths;
        }

        public ValueTask<FileOperationCanonicalPath> ResolveAsync(
            string path,
            bool allowMissingLeaf = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = Path.GetFullPath(path);
            if (_paths.TryGetValue(normalized, out var resolved))
            {
                return ValueTask.FromResult(resolved);
            }

            return ValueTask.FromResult(
                new FileOperationCanonicalPath(
                    normalized,
                    normalized,
                    FileOperationCanonicalPathState.Missing,
                    IsLeafReparsePoint: false));
        }
    }
}
