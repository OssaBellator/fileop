using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileCrossVolumeMoveSchemaV1VolumeGuardTests
{
    [TestMethod]
    public async Task EqualVolumeSerialEvidenceIsRejectedBeforeCompositeHistoryOrCopy()
    {
        using var fixture = new HistoryFixture();
        var plan = CreatePlan();
        var validator = new EqualSerialValidator();
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
        StringAssert.Contains(result.Failure?.Message ?? string.Empty, "distinct source/destination volumes");
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

    private sealed class EqualSerialValidator : IFileOperationExecutionValidator
    {
        public ValueTask<FileOperationExecutionValidationResult> ValidateAsync(
            FileOperationPlan plan,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceDirectory = Path.GetFullPath(@"C:\Real\Source");
            var destinationDirectory = Path.GetFullPath(@"D:\Real\Destination");
            var entry = plan.Intent.Entries[0];
            return ValueTask.FromResult(
                new FileOperationExecutionValidationResult(
                    plan,
                    new FileOperationCanonicalPath(
                        plan.Intent.SourceDirectoryPath,
                        sourceDirectory,
                        FileOperationCanonicalPathState.Directory,
                        IsLeafReparsePoint: false,
                        Identity: new FileIdentity(1, 10)),
                    new FileOperationCanonicalPath(
                        plan.Intent.DestinationDirectoryPath,
                        destinationDirectory,
                        FileOperationCanonicalPathState.Directory,
                        IsLeafReparsePoint: false,
                        Identity: new FileIdentity(1, 20)),
                    new[]
                    {
                        new FileOperationExecutionValidationItem(
                            entry,
                            new FileOperationCanonicalPath(
                                entry.Path,
                                Path.Combine(sourceDirectory, entry.Name),
                                FileOperationCanonicalPathState.File,
                                IsLeafReparsePoint: false,
                                Identity: new FileIdentity(1, 100)),
                            new FileOperationCanonicalPath(
                                Path.Combine(plan.Intent.DestinationDirectoryPath, entry.Name),
                                Path.Combine(destinationDirectory, entry.Name),
                                FileOperationCanonicalPathState.Missing,
                                IsLeafReparsePoint: false),
                            FileOperationExecutionValidationDecision.Ready,
                            "ready"),
                    },
                    FileOperationExecutionValidationStatus.Ready,
                    new DateTimeOffset(2026, 8, 15, 0, 1, 0, TimeSpan.Zero),
                    "ready"));
        }
    }

    private sealed class NeverCopyMutation : IFileCopyMutationPrimitive
    {
        public int CallCount { get; private set; }

        public ValueTask<IFileCopyMutationLease> CopyNewFileAsync(FileCopyMutationRequest request)
        {
            CallCount++;
            throw new AssertFailedException(
                "Schema-v1 equal-serial evidence must be rejected before Copy.");
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
            throw new AssertFailedException(
                "Schema-v1 equal-serial evidence must be rejected before source-delete acquisition.");
        }
    }

    private sealed class HistoryFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "FileOp.CrossVolumeMoveSchemaV1VolumeGuard.Tests",
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
