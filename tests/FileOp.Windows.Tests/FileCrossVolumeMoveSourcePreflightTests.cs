using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileCrossVolumeMoveSourcePreflightTests
{
    [TestMethod]
    public void ReadOnlyNamedStreamAndEaAreDeterministicPreCopyBlockers()
    {
        var classification = FileCrossVolumeMoveSourcePreflightClassifier.Classify(
            new FileCrossVolumeMoveSourcePreflightEvidence(
                new FileBasicMetadataEvidence(1, 2, 3, FileAttributes: 0x00000001u),
                SourceNamedDataStreamCount: 1,
                SourceExtendedAttributeSize: 16));

        Assert.IsFalse(classification.CanStartCopy);
        Assert.IsTrue(classification.Blockers.Contains(
            FileCrossVolumeMoveSourcePreflightBlocker.SourceUnsupportedAttributes));
        Assert.IsTrue(classification.Blockers.Contains(
            FileCrossVolumeMoveSourcePreflightBlocker.SourceNamedDataStreams));
        Assert.IsTrue(classification.Blockers.Contains(
            FileCrossVolumeMoveSourcePreflightBlocker.SourceExtendedAttributes));
    }

    [TestMethod]
    public void OrdinarySupportedSourceMayContinueButPreflightNeverAuthorizesMutation()
    {
        var classification = FileCrossVolumeMoveSourcePreflightClassifier.Classify(
            new FileCrossVolumeMoveSourcePreflightEvidence(
                new FileBasicMetadataEvidence(1, 2, 3, FileAttributes: 0x00000020u),
                SourceNamedDataStreamCount: 0,
                SourceExtendedAttributeSize: 0));
        var request = new FileCrossVolumeMoveSourcePreflightRequest(
            Guid.NewGuid(),
            0,
            new FileOperationEntry(@"C:\Source\a.txt", "a.txt", IsDirectory: false),
            @"C:\Source",
            new FileIdentity(1, 10),
            @"C:\Source\a.txt",
            new FileIdentity(1, 100));

        Assert.IsTrue(classification.CanStartCopy);
        Assert.AreEqual(0, classification.Blockers.Count);
        Assert.IsFalse(request.SourcePreflightAuthorizesMutation);
    }

    [TestMethod]
    public async Task UnsupportedSourcePreflightBlocksBeforeCompositeHistoryOrCopy()
    {
        using var fixture = new HistoryFixture();
        var plan = CreatePlan();
        var probe = new FakePreflightProbe(
            new FileCrossVolumeMoveSourcePreflightClassification(
                CanStartCopy: false,
                new[] { FileCrossVolumeMoveSourcePreflightBlocker.SourceNamedDataStreams },
                "source has a named stream"));
        var validator = new FileCrossVolumeMovePreflightExecutionValidator(
            new FakeValidator(),
            probe);
        using var history = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.DatabasePath);
        var copy = new NeverCopyMutation();
        var sourceDelete = new NeverSourceDeletePrimitive();
        var executor = new FileCrossVolumeMoveOperationExecutor(
            validator,
            history,
            copy,
            sourceDelete);

        var result = await executor.ExecuteAsync(plan);

        Assert.AreEqual(FileOperationExecutionState.Failed, result.State);
        Assert.AreEqual("CrossVolumeMoveValidationBlocked", result.Failure?.Code);
        Assert.AreEqual(1, probe.CallCount);
        Assert.AreEqual(0, copy.CallCount);
        Assert.AreEqual(0, sourceDelete.CallCount);
        Assert.IsNull(await history.GetAsync(plan.Id));
    }

    private static FileOperationPlan CreatePlan()
    {
        var sourceDirectory = Path.GetFullPath(@"C:\Source");
        var destinationDirectory = Path.GetFullPath(@"D:\Destination");
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

    private static FileOperationExecutionValidationResult CreateValidation(FileOperationPlan plan)
    {
        var canonicalSourceDirectory = Path.GetFullPath(@"C:\Real\Source");
        var canonicalDestinationDirectory = Path.GetFullPath(@"D:\Real\Destination");
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
                Identity: new FileIdentity(2, 20)),
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
        public ValueTask<FileOperationExecutionValidationResult> ValidateAsync(
            FileOperationPlan plan,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(CreateValidation(plan));
        }
    }

    private sealed class FakePreflightProbe : IFileCrossVolumeMoveSourcePreflightProbe
    {
        private readonly FileCrossVolumeMoveSourcePreflightClassification _classification;

        public FakePreflightProbe(FileCrossVolumeMoveSourcePreflightClassification classification) =>
            _classification = classification;

        public int CallCount { get; private set; }

        public ValueTask<FileCrossVolumeMoveSourcePreflightClassification> ProbeAsync(
            FileCrossVolumeMoveSourcePreflightRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.IsFalse(request.SourcePreflightAuthorizesMutation);
            Assert.AreEqual(new FileIdentity(1, 10), request.SourceDirectoryIdentity);
            Assert.AreEqual(new FileIdentity(1, 100), request.SourceIdentity);
            CallCount++;
            return ValueTask.FromResult(_classification);
        }
    }

    private sealed class NeverCopyMutation : IFileCopyMutationPrimitive
    {
        public int CallCount { get; private set; }

        public ValueTask<IFileCopyMutationLease> CopyNewFileAsync(FileCopyMutationRequest request)
        {
            CallCount++;
            throw new AssertFailedException("Copy must not run after deterministic source preflight refusal.");
        }
    }

    private sealed class NeverSourceDeletePrimitive : IFileCrossVolumeMoveSourceDeletePrimitive
    {
        public int CallCount { get; private set; }

        public ValueTask<IFileCrossVolumeMoveSourceDeleteLease> AcquireAsync(
            FileCrossVolumeMoveSourceDeleteRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new AssertFailedException("Source-delete capability must not be acquired after source preflight refusal.");
        }
    }

    private sealed class HistoryFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "FileOp.CrossVolumeMoveSourcePreflight.Tests",
            Guid.NewGuid().ToString("N"));

        public HistoryFixture()
        {
            Directory.CreateDirectory(_directory);
            DatabasePath = Path.Combine(_directory, "actions.sqlite");
        }

        public string DatabasePath { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                try
                {
                    File.Delete(DatabasePath + suffix);
                }
                catch (IOException)
                {
                }
            }
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
