using System;
using System.IO;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

/// <summary>
/// Opt-in real two-volume regression for the current #185 read-only policy.
/// ReadOnly is copied as metadata but is deliberately refused before the source-delete
/// barrier until #190 reviews exact-handle ignore-readonly disposition semantics.
/// </summary>
[TestClass]
public sealed class FileCrossVolumeMoveNativeReadOnlyTests
{
    private const string SourceRootVariable = "FILEOP_CROSS_VOLUME_MOVE_SOURCE_ROOT";
    private const string DestinationRootVariable = "FILEOP_CROSS_VOLUME_MOVE_DESTINATION_ROOT";

    [TestMethod]
    [TestCategory("CrossVolumeMoveNative")]
    public async Task ReadOnlySourceIsSafelyRetainedBeforeSourceDeleteBarrier()
    {
        using var fixture = CreateFixture();
        var sourcePath = Path.Combine(fixture.SourceDirectory, "read-only.txt");
        File.WriteAllText(sourcePath, "read-only cross-volume payload");
        File.SetAttributes(sourcePath, File.GetAttributes(sourcePath) | FileAttributes.ReadOnly);

        try
        {
            var plan = CreatePlan(fixture.SourceDirectory, fixture.DestinationDirectory, sourcePath);
            var validation = await new WindowsFileOperationExecutionValidator().ValidateAsync(plan);
            Assert.IsTrue(validation.CanBeginMutation, validation.Summary);
            Assert.IsTrue(validation.SourceDirectory.Identity.HasValue);
            Assert.IsTrue(validation.DestinationDirectory.Identity.HasValue);
            Assert.AreNotEqual(
                validation.SourceDirectory.Identity.Value.VolumeSerialNumber,
                validation.DestinationDirectory.Identity.Value.VolumeSerialNumber,
                "The opt-in read-only roots resolved to the same filesystem volume.");

            using var history = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.HistoryDatabasePath);
            var executor = new FileCrossVolumeMoveOperationExecutor(
                new WindowsFileOperationExecutionValidator(),
                history,
                new WindowsFileCopyMutationPrimitive(),
                new WindowsFidelityVerifiedFileCrossVolumeMoveSourceDeletePrimitive());

            var result = await executor.ExecuteAsync(plan);

            Assert.AreEqual(FileOperationExecutionState.Failed, result.State);
            Assert.AreEqual("CrossVolumeMoveSourceDeletePreparationFailed", result.Failure?.Code);
            StringAssert.Contains(result.Failure?.Message ?? string.Empty, "SourceUnsupportedAttributes");
            StringAssert.Contains(result.Failure?.Message ?? string.Empty, "DestinationUnsupportedAttributes");

            var destinationPath = Path.Combine(
                fixture.DestinationDirectory,
                Path.GetFileName(sourcePath));
            Assert.IsTrue(File.Exists(sourcePath));
            Assert.IsTrue(File.Exists(destinationPath));
            Assert.AreEqual(File.ReadAllText(sourcePath), File.ReadAllText(destinationPath));
            Assert.IsTrue((File.GetAttributes(sourcePath) & FileAttributes.ReadOnly) != 0);
            Assert.IsTrue(
                (File.GetAttributes(destinationPath) & FileAttributes.ReadOnly) != 0,
                "The real Copy phase should preserve ReadOnly even though destructive completion is currently refused.");

            var persisted = await history.GetAsync(plan.Id);
            Assert.IsNotNull(persisted);
            Assert.AreEqual(FileCrossVolumeMoveTerminalState.Failed, persisted.TerminalState);
            Assert.AreEqual(FileCrossVolumeMoveEntryState.Failed, persisted.Entries[0].State);
            Assert.IsNotNull(persisted.Entries[0].DestinationCommittedAtUtc);
            Assert.IsNull(persisted.Entries[0].SourceDeleteStartedAtUtc);
            Assert.IsTrue(persisted.Entries[0].DestinationIsDurablyCommitted);
            Assert.IsFalse(persisted.Entries[0].SourceDeleteBarrierMayBeUnresolved);
            Assert.IsTrue(persisted.HasRetainedSourceDuplicates);
            Assert.IsFalse(persisted.RequiresRecovery);
        }
        finally
        {
            ClearReadOnly(sourcePath);
            ClearReadOnly(Path.Combine(fixture.DestinationDirectory, Path.GetFileName(sourcePath)));
        }
    }

    private static FileOperationPlan CreatePlan(
        string sourceDirectory,
        string destinationDirectory,
        string sourcePath)
    {
        var entry = new FileOperationEntry(
            sourcePath,
            Path.GetFileName(sourcePath),
            IsDirectory: false);
        return new FileOperationPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            FileOperationKind.Move,
            FileOperationCollisionPolicy.Stop,
            new FileOperationIntent(
                "CrossVolumeReadOnlySource",
                Guid.NewGuid(),
                sourceDirectory,
                new[] { entry },
                "CrossVolumeReadOnlyDestination",
                Guid.NewGuid(),
                destinationDirectory));
    }

    private static NativeFixture CreateFixture()
    {
        var sourceRoot = Environment.GetEnvironmentVariable(SourceRootVariable);
        var destinationRoot = Environment.GetEnvironmentVariable(DestinationRootVariable);
        if (string.IsNullOrWhiteSpace(sourceRoot) || string.IsNullOrWhiteSpace(destinationRoot))
        {
            Assert.Inconclusive(
                $"Set {SourceRootVariable} and {DestinationRootVariable} to writable directories on different volumes to run opt-in cross-volume Move native tests.");
        }

        return new NativeFixture(
            Path.GetFullPath(sourceRoot!),
            Path.GetFullPath(destinationRoot!));
    }

    private static void ClearReadOnly(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class NativeFixture : IDisposable
    {
        private readonly string _historyDirectory;

        public NativeFixture(string sourceRoot, string destinationRoot)
        {
            var suffix = Guid.NewGuid().ToString("N");
            SourceDirectory = Path.Combine(
                sourceRoot,
                "FileOp.CrossVolumeMoveNativeReadOnly",
                suffix);
            DestinationDirectory = Path.Combine(
                destinationRoot,
                "FileOp.CrossVolumeMoveNativeReadOnly",
                suffix);
            Directory.CreateDirectory(SourceDirectory);
            Directory.CreateDirectory(DestinationDirectory);

            _historyDirectory = Path.Combine(
                Path.GetTempPath(),
                "FileOp.CrossVolumeMoveNativeReadOnly.History",
                suffix);
            Directory.CreateDirectory(_historyDirectory);
            HistoryDatabasePath = Path.Combine(_historyDirectory, "history.db");
        }

        public string SourceDirectory { get; }

        public string DestinationDirectory { get; }

        public string HistoryDatabasePath { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            ClearReadOnly(Path.Combine(SourceDirectory, "read-only.txt"));
            ClearReadOnly(Path.Combine(DestinationDirectory, "read-only.txt"));
            TryDeleteDirectory(SourceDirectory);
            TryDeleteDirectory(DestinationDirectory);
            TryDeleteDirectory(_historyDirectory);
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
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
