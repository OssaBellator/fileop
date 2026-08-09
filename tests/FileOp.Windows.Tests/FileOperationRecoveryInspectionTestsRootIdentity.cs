using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileOperationRecoveryInspectionTestsRootIdentity
{
    [TestMethod]
    public async Task LegacyHistoryDoesNotResolveRootWithoutDurableIdentity()
    {
        var history = CreateHistory(destinationRootIdentity: null);
        var resolver = new RecordingResolver(path =>
        {
            if (string.Equals(
                Trim(path),
                Trim(history.CanonicalDestinationDirectoryPath),
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Root resolver should not run for legacy root evidence.");
            }

            return File(
                history.Entries[0].CanonicalDestinationPath,
                history.Entries[0].DestinationIdentity!.Value);
        });
        var inspector = new FileOperationRecoveryInspector(resolver);

        var result = await inspector.InspectAsync(history);

        Assert.AreEqual(
            FileOperationRecoveryRootStatus.NoVerifiedIdentity,
            result.DestinationDirectory.Status);
        Assert.IsNull(result.DestinationDirectory.RecordedIdentity);
        Assert.AreEqual(
            history.CanonicalDestinationDirectoryPath,
            result.DestinationDirectory.RecordedCanonicalPath);
        Assert.IsNull(result.DestinationDirectory.CurrentDirectory);
        Assert.AreEqual(1, resolver.Paths.Count);
        Assert.AreEqual(history.Entries[0].CanonicalDestinationPath, resolver.Paths[0]);
    }

    [TestMethod]
    public async Task SameAndDifferentRootIdentityAreDistinguished()
    {
        var recorded = new FileIdentity(8, 80);
        foreach (var same in new[] { true, false })
        {
            var history = CreateHistory(recorded);
            var currentIdentity = same ? recorded : new FileIdentity(8, 81);
            var resolver = CreateResolverForRoot(
                history,
                Directory(history.CanonicalDestinationDirectoryPath, currentIdentity));

            var result = await new FileOperationRecoveryInspector(resolver).InspectAsync(history);

            Assert.AreEqual(
                same ? FileOperationRecoveryRootStatus.SameObject : FileOperationRecoveryRootStatus.DifferentObject,
                result.DestinationDirectory.Status);
            Assert.AreEqual(recorded, result.DestinationDirectory.RecordedIdentity);
            Assert.AreEqual(
                history.CanonicalDestinationDirectoryPath,
                result.DestinationDirectory.RecordedCanonicalPath);
            Assert.AreEqual(currentIdentity, result.DestinationDirectory.CurrentDirectory?.Identity);
        }
    }

    [TestMethod]
    public async Task RootUnsafeStatesAreClassifiedConservatively()
    {
        var recorded = new FileIdentity(9, 90);
        var history = CreateHistory(recorded);
        var cases = new[]
        {
            (Missing(history.CanonicalDestinationDirectoryPath), FileOperationRecoveryRootStatus.Missing),
            (Directory(@"D:\Redirected", recorded), FileOperationRecoveryRootStatus.Redirected),
            (Directory(history.CanonicalDestinationDirectoryPath, recorded, isReparse: true), FileOperationRecoveryRootStatus.ReparsePoint),
            (File(history.CanonicalDestinationDirectoryPath, recorded), FileOperationRecoveryRootStatus.UnexpectedType),
            (Inaccessible(history.CanonicalDestinationDirectoryPath), FileOperationRecoveryRootStatus.Inaccessible),
            (Error(history.CanonicalDestinationDirectoryPath), FileOperationRecoveryRootStatus.Error),
        };

        foreach (var pair in cases)
        {
            var resolver = CreateResolverForRoot(history, pair.Item1);
            var result = await new FileOperationRecoveryInspector(resolver).InspectAsync(history);
            Assert.AreEqual(pair.Item2, result.DestinationDirectory.Status, pair.Item2.ToString());
            Assert.AreEqual(
                history.CanonicalDestinationDirectoryPath,
                result.DestinationDirectory.RecordedCanonicalPath);
            Assert.IsFalse(result.DestinationDirectory.IsSameRecordedRoot);
        }
    }

    private static RecordingResolver CreateResolverForRoot(
        FileOperationActionHistory history,
        FileOperationCanonicalPath rootResult) =>
        new(path => string.Equals(
                Trim(path),
                Trim(history.CanonicalDestinationDirectoryPath),
                StringComparison.OrdinalIgnoreCase)
            ? rootResult
            : File(
                history.Entries[0].CanonicalDestinationPath,
                history.Entries[0].DestinationIdentity!.Value));

    private static FileOperationActionHistory CreateHistory(FileIdentity? destinationRootIdentity)
    {
        var now = DateTimeOffset.UtcNow;
        var destinationPath = @"D:\Real\Destination\payload.bin";
        var entry = new FileOperationActionEntry(
            0,
            new FileOperationEntry(@"C:\Source\payload.bin", "payload.bin", IsDirectory: false),
            @"C:\Real\Source\payload.bin",
            destinationPath,
            FileOperationActionEntryState.RecoveryRequired,
            now,
            now,
            new FileIdentity(1, 10),
            new FileIdentity(3, 30),
            FileOperationUndoKind.None,
            new FileOperationFailure("RecoveryRequired", "fixture", destinationPath, Retryable: false));
        return new FileOperationActionHistory(
            Guid.NewGuid(),
            now,
            now,
            now,
            now,
            FileOperationKind.Copy,
            FileOperationCollisionPolicy.Stop,
            @"C:\Source",
            @"D:\Destination",
            @"C:\Real\Source",
            @"D:\Real\Destination",
            FileOperationActionTerminalState.RecoveryRequired,
            new[] { entry },
            SourceDirectoryIdentity: destinationRootIdentity.HasValue ? new FileIdentity(2, 20) : null,
            DestinationDirectoryIdentity: destinationRootIdentity);
    }

    private static FileOperationCanonicalPath Directory(
        string path,
        FileIdentity identity,
        bool isReparse = false) =>
        new(path, path, FileOperationCanonicalPathState.Directory, isReparse, Identity: identity);

    private static FileOperationCanonicalPath File(string path, FileIdentity identity) =>
        new(path, path, FileOperationCanonicalPathState.File, IsLeafReparsePoint: false, Identity: identity);

    private static FileOperationCanonicalPath Missing(string path) =>
        new(path, path, FileOperationCanonicalPathState.Missing, IsLeafReparsePoint: false);

    private static FileOperationCanonicalPath Inaccessible(string path) =>
        new(
            path,
            path,
            FileOperationCanonicalPathState.Inaccessible,
            IsLeafReparsePoint: false,
            ErrorCode: "AccessDenied",
            ErrorMessage: "fixture");

    private static FileOperationCanonicalPath Error(string path) =>
        new(
            path,
            path,
            FileOperationCanonicalPathState.Error,
            IsLeafReparsePoint: false,
            ErrorCode: "Error",
            ErrorMessage: "fixture");

    private static string Trim(string path) => path.TrimEnd('\\', '/');

    private sealed class RecordingResolver : IFileOperationCanonicalPathResolver
    {
        private readonly Func<string, FileOperationCanonicalPath> _callback;

        public RecordingResolver(Func<string, FileOperationCanonicalPath> callback) =>
            _callback = callback;

        public List<string> Paths { get; } = new();

        public ValueTask<FileOperationCanonicalPath> ResolveAsync(
            string path,
            bool allowMissingLeaf = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Paths.Add(path);
            return ValueTask.FromResult(_callback(path));
        }
    }
}
