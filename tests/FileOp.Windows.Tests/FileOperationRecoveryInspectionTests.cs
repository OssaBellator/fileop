using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileOperationRecoveryInspectionTests
{
    [TestMethod]
    public async Task SameIdentityAtCanonicalPathIsEvidenceButNotUndoAuthority()
    {
        var identity = new FileIdentity(7, 70);
        var entry = CreateEntry(
            0,
            FileOperationActionEntryState.RecoveryRequired,
            @"D:\Real\Destination\a.txt",
            identity);
        var resolver = new FakeResolver(new Dictionary<string, FileOperationCanonicalPath>
        {
            [entry.CanonicalDestinationPath] = ExistingFile(entry.CanonicalDestinationPath, identity),
        });
        var inspector = new FileOperationRecoveryInspector(resolver);

        var result = await inspector.InspectAsync(CreateHistory(entry));

        Assert.AreEqual(1, result.Items.Count);
        Assert.AreEqual(FileOperationRecoveryDestinationStatus.SameObject, result.Items[0].Status);
        Assert.IsTrue(result.Items[0].IsSameRecordedObject);
        Assert.AreEqual(identity, result.Items[0].RecordedDestinationIdentity);
        Assert.AreEqual(FileOperationUndoKind.None, result.Items[0].Entry.UndoKind);
        Assert.IsFalse(result.Items[0].Entry.IsUndoCandidate);
        Assert.AreEqual(1, result.SameObjectCount);
        CollectionAssert.AreEqual(
            new[] { entry.CanonicalDestinationPath },
            resolver.Calls.Select(call => call.Path).ToArray());
        Assert.IsTrue(resolver.Calls.All(call => call.AllowMissingLeaf));
    }

    [TestMethod]
    public async Task RecoveryInspectionClassifiesUnsafeAndChangedDestinationsConservatively()
    {
        var entries = new[]
        {
            CreateEntry(0, FileOperationActionEntryState.RecoveryRequired, @"D:\Dest\missing.txt", new FileIdentity(1, 10)),
            CreateEntry(1, FileOperationActionEntryState.RecoveryRequired, @"D:\Dest\different.txt", new FileIdentity(1, 11)),
            CreateEntry(2, FileOperationActionEntryState.RecoveryRequired, @"D:\Dest\redirected.txt", new FileIdentity(1, 12)),
            CreateEntry(3, FileOperationActionEntryState.RecoveryRequired, @"D:\Dest\reparse.txt", new FileIdentity(1, 13)),
            CreateEntry(4, FileOperationActionEntryState.RecoveryRequired, @"D:\Dest\directory.txt", new FileIdentity(1, 14)),
            CreateEntry(5, FileOperationActionEntryState.RecoveryRequired, @"D:\Dest\inaccessible.txt", new FileIdentity(1, 15)),
            CreateEntry(6, FileOperationActionEntryState.RecoveryRequired, @"D:\Dest\error.txt", new FileIdentity(1, 16)),
            CreateEntry(7, FileOperationActionEntryState.RecoveryRequired, @"D:\Dest\missing-redirected.txt", new FileIdentity(1, 17)),
        };
        var resolver = new FakeResolver(new Dictionary<string, FileOperationCanonicalPath>
        {
            [entries[0].CanonicalDestinationPath] = Canonical(entries[0].CanonicalDestinationPath, FileOperationCanonicalPathState.Missing),
            [entries[1].CanonicalDestinationPath] = ExistingFile(entries[1].CanonicalDestinationPath, new FileIdentity(9, 99)),
            [entries[2].CanonicalDestinationPath] = ExistingFile(@"E:\Redirected\redirected.txt", entries[2].DestinationIdentity!.Value, requestedPath: entries[2].CanonicalDestinationPath),
            [entries[3].CanonicalDestinationPath] = ExistingFile(entries[3].CanonicalDestinationPath, entries[3].DestinationIdentity!.Value, isReparsePoint: true),
            [entries[4].CanonicalDestinationPath] = Canonical(entries[4].CanonicalDestinationPath, FileOperationCanonicalPathState.Directory, entries[4].DestinationIdentity),
            [entries[5].CanonicalDestinationPath] = Canonical(entries[5].CanonicalDestinationPath, FileOperationCanonicalPathState.Inaccessible, errorCode: "AccessDenied"),
            [entries[6].CanonicalDestinationPath] = Canonical(entries[6].CanonicalDestinationPath, FileOperationCanonicalPathState.Error, errorCode: "IoError"),
            [entries[7].CanonicalDestinationPath] = Canonical(
                entries[7].CanonicalDestinationPath,
                FileOperationCanonicalPathState.Missing,
                canonicalPath: @"E:\Redirected\missing-redirected.txt"),
        });
        var inspector = new FileOperationRecoveryInspector(resolver);

        var result = await inspector.InspectAsync(CreateHistory(entries));

        CollectionAssert.AreEqual(
            new[]
            {
                FileOperationRecoveryDestinationStatus.Missing,
                FileOperationRecoveryDestinationStatus.DifferentObject,
                FileOperationRecoveryDestinationStatus.Redirected,
                FileOperationRecoveryDestinationStatus.ReparsePoint,
                FileOperationRecoveryDestinationStatus.UnexpectedType,
                FileOperationRecoveryDestinationStatus.Inaccessible,
                FileOperationRecoveryDestinationStatus.Error,
                FileOperationRecoveryDestinationStatus.Redirected,
            },
            result.Items.Select(item => item.Status).ToArray());
        Assert.AreEqual(0, result.SameObjectCount);
        Assert.IsTrue(result.Items.All(item => !item.IsSameRecordedObject));
    }

    [TestMethod]
    public async Task ExistingDestinationWithoutDurableIdentityCannotBeReportedAsSameObject()
    {
        var entry = CreateEntry(
            0,
            FileOperationActionEntryState.MutationStarted,
            @"D:\Dest\uncertain.txt",
            destinationIdentity: null);
        var resolver = new FakeResolver(new Dictionary<string, FileOperationCanonicalPath>
        {
            [entry.CanonicalDestinationPath] = ExistingFile(
                entry.CanonicalDestinationPath,
                new FileIdentity(4, 44)),
        });
        var inspector = new FileOperationRecoveryInspector(resolver);

        var result = await inspector.InspectAsync(CreateHistory(entry));

        Assert.AreEqual(FileOperationRecoveryDestinationStatus.NoVerifiedIdentity, result.Items[0].Status);
        Assert.IsFalse(result.Items[0].IsSameRecordedObject);
        Assert.IsNull(result.Items[0].RecordedDestinationIdentity);
    }

    [TestMethod]
    public async Task InspectorSkipsSettledEntriesAndRejectsMoveHistory()
    {
        var committed = CreateEntry(
            0,
            FileOperationActionEntryState.Committed,
            @"D:\Dest\committed.txt",
            new FileIdentity(2, 20),
            FileOperationUndoKind.DeleteCreatedDestination);
        var skipped = CreateEntry(
            1,
            FileOperationActionEntryState.Skipped,
            @"D:\Dest\skipped.txt",
            new FileIdentity(2, 21));
        var recovery = CreateEntry(
            2,
            FileOperationActionEntryState.RecoveryRequired,
            @"D:\Dest\recovery.txt",
            new FileIdentity(2, 22));
        var resolver = new FakeResolver(new Dictionary<string, FileOperationCanonicalPath>
        {
            [recovery.CanonicalDestinationPath] = ExistingFile(
                recovery.CanonicalDestinationPath,
                recovery.DestinationIdentity!.Value),
        });
        var inspector = new FileOperationRecoveryInspector(resolver);

        var result = await inspector.InspectAsync(CreateHistory(committed, skipped, recovery));

        Assert.AreEqual(1, result.Items.Count);
        Assert.AreEqual(2, result.Items[0].Ordinal);
        Assert.AreEqual(1, resolver.Calls.Count);
        Assert.AreEqual(recovery.CanonicalDestinationPath, resolver.Calls[0].Path);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(async () =>
            await new FileOperationRecoveryInspector(resolver)
                .InspectAsync(CreateMoveHistory()));
    }

    [TestMethod]
    public void InspectionDefensivelySnapshotsItems()
    {
        var entry = CreateEntry(
            0,
            FileOperationActionEntryState.RecoveryRequired,
            @"D:\Dest\snapshot.txt",
            new FileIdentity(8, 80));
        var current = ExistingFile(entry.CanonicalDestinationPath, entry.DestinationIdentity!.Value);
        var item = new FileOperationRecoveryInspectionItem(
            entry.Ordinal,
            entry,
            FileOperationRecoveryDestinationStatus.SameObject,
            current,
            "same");
        var mutable = new List<FileOperationRecoveryInspectionItem> { item };
        var inspection = new FileOperationRecoveryInspection(Guid.NewGuid(), mutable);

        mutable.Clear();

        Assert.AreEqual(1, inspection.Items.Count);
        Assert.AreSame(item, inspection.Items[0]);
    }

    private static FileOperationActionHistory CreateHistory(params FileOperationActionEntry[] entries)
    {
        var now = new DateTimeOffset(2026, 8, 9, 4, 0, 0, TimeSpan.Zero);
        return new FileOperationActionHistory(
            Guid.NewGuid(),
            now,
            now,
            now,
            now,
            FileOperationKind.Copy,
            FileOperationCollisionPolicy.Skip,
            @"C:\Source",
            @"D:\Destination",
            @"C:\Real\Source",
            @"D:\Real\Destination",
            FileOperationActionTerminalState.RecoveryRequired,
            entries);
    }

    private static FileOperationActionHistory CreateMoveHistory()
    {
        var now = new DateTimeOffset(2026, 8, 9, 4, 0, 0, TimeSpan.Zero);
        var skipped = CreateEntry(
            0,
            FileOperationActionEntryState.Skipped,
            @"D:\Dest\existing.txt",
            new FileIdentity(5, 50));
        return new FileOperationActionHistory(
            Guid.NewGuid(),
            now,
            now,
            now,
            now,
            FileOperationKind.Move,
            FileOperationCollisionPolicy.Skip,
            @"C:\Source",
            @"D:\Destination",
            @"C:\Real\Source",
            @"D:\Real\Destination",
            FileOperationActionTerminalState.Cancelled,
            new[] { skipped });
    }

    private static FileOperationActionEntry CreateEntry(
        int ordinal,
        FileOperationActionEntryState state,
        string canonicalDestinationPath,
        FileIdentity? destinationIdentity,
        FileOperationUndoKind undoKind = FileOperationUndoKind.None)
    {
        var now = new DateTimeOffset(2026, 8, 9, 4, 0, 0, TimeSpan.Zero);
        return new FileOperationActionEntry(
            ordinal,
            new FileOperationEntry(
                $@"C:\Source\file-{ordinal}.dat",
                $"file-{ordinal}.dat",
                IsDirectory: false),
            $@"C:\Real\Source\file-{ordinal}.dat",
            canonicalDestinationPath,
            state,
            state is FileOperationActionEntryState.Pending or
                FileOperationActionEntryState.Skipped or
                FileOperationActionEntryState.Failed
                ? null
                : now,
            state is FileOperationActionEntryState.Pending or
                FileOperationActionEntryState.MutationStarted
                ? null
                : now,
            new FileIdentity(1, (ulong)(100 + ordinal)),
            destinationIdentity,
            undoKind,
            state == FileOperationActionEntryState.RecoveryRequired
                ? new FileOperationFailure(
                    "RecoveryRequired",
                    "Recovery inspection fixture.",
                    canonicalDestinationPath,
                    Retryable: false)
                : null);
    }

    private static FileOperationCanonicalPath ExistingFile(
        string canonicalPath,
        FileIdentity identity,
        string? requestedPath = null,
        bool isReparsePoint = false) =>
        Canonical(
            requestedPath ?? canonicalPath,
            FileOperationCanonicalPathState.File,
            identity,
            canonicalPath,
            isReparsePoint);

    private static FileOperationCanonicalPath Canonical(
        string requestedPath,
        FileOperationCanonicalPathState state,
        FileIdentity? identity = null,
        string? canonicalPath = null,
        bool isReparsePoint = false,
        string? errorCode = null) =>
        new(
            requestedPath,
            canonicalPath ?? requestedPath,
            state,
            isReparsePoint,
            identity,
            errorCode,
            errorCode);

    private sealed class FakeResolver : IFileOperationCanonicalPathResolver
    {
        private readonly IReadOnlyDictionary<string, FileOperationCanonicalPath> _results;

        public FakeResolver(IReadOnlyDictionary<string, FileOperationCanonicalPath> results) =>
            _results = results;

        public List<(string Path, bool AllowMissingLeaf)> Calls { get; } = new();

        public ValueTask<FileOperationCanonicalPath> ResolveAsync(
            string path,
            bool allowMissingLeaf = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((path, allowMissingLeaf));
            return ValueTask.FromResult(
                _results.TryGetValue(path, out var result)
                    ? result
                    : throw new InvalidOperationException($"No fake resolution for {path}."));
        }
    }
}
