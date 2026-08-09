using System;
using System.IO;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileOperationActionHistoryHardLinkEvidenceMultiEntryTests
{
    [TestMethod]
    public async Task LaterCommitAndCompletionPreserveEarlierHardLinkEvidence()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation();
        using var store = new SqliteFileOperationActionHistoryHardLinkEvidenceStore(fixture.DatabasePath);
        await store.BeginAsync(validation, DateTimeOffset.UtcNow);

        await store.MarkMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);
        var first = await store.CommitCopyWithHardLinkEvidenceAsync(
            validation.Plan.Id,
            0,
            new FileIdentity(7, 70),
            Fingerprint('a'),
            2,
            DateTimeOffset.UtcNow);
        Assert.AreEqual(2u, first.Entries[0].DestinationHardLinkCount);

        await store.MarkMutationStartedAsync(validation.Plan.Id, 1, DateTimeOffset.UtcNow);
        var second = await store.CommitCopyWithHardLinkEvidenceAsync(
            validation.Plan.Id,
            1,
            new FileIdentity(8, 80),
            Fingerprint('b'),
            3,
            DateTimeOffset.UtcNow);

        Assert.AreEqual(2u, second.Entries[0].DestinationHardLinkCount);
        Assert.AreEqual(3u, second.Entries[1].DestinationHardLinkCount);

        var completed = await store.CompleteAsync(
            validation.Plan.Id,
            FileOperationActionTerminalState.Succeeded,
            DateTimeOffset.UtcNow);
        Assert.AreEqual(2u, completed.Entries[0].DestinationHardLinkCount);
        Assert.AreEqual(3u, completed.Entries[1].DestinationHardLinkCount);
    }

    private static FileContentFingerprint Fingerprint(char value) =>
        new(FileContentFingerprintAlgorithm.Sha256, new string(value, 64));

    private static FileOperationExecutionValidationResult CreateValidation()
    {
        var sourceDirectory = Path.GetFullPath(@"C:\Source");
        var destinationDirectory = Path.GetFullPath(@"D:\Destination");
        var canonicalSourceDirectory = Path.GetFullPath(@"C:\Real\Source");
        var canonicalDestinationDirectory = Path.GetFullPath(@"D:\Real\Destination");
        var entries = new[]
        {
            new FileOperationEntry(Path.Combine(sourceDirectory, "a.txt"), "a.txt", IsDirectory: false),
            new FileOperationEntry(Path.Combine(sourceDirectory, "b.txt"), "b.txt", IsDirectory: false),
        };
        var plan = new FileOperationPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            FileOperationKind.Copy,
            FileOperationCollisionPolicy.Stop,
            new FileOperationIntent(
                "Left",
                Guid.NewGuid(),
                sourceDirectory,
                entries,
                "Right",
                Guid.NewGuid(),
                destinationDirectory));
        var items = new FileOperationExecutionValidationItem[entries.Length];
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            items[index] = new FileOperationExecutionValidationItem(
                entry,
                new FileOperationCanonicalPath(
                    entry.Path,
                    Path.Combine(canonicalSourceDirectory, entry.Name),
                    FileOperationCanonicalPathState.File,
                    IsLeafReparsePoint: false,
                    Identity: new FileIdentity(3, (ulong)(30 + index))),
                new FileOperationCanonicalPath(
                    Path.Combine(destinationDirectory, entry.Name),
                    Path.Combine(canonicalDestinationDirectory, entry.Name),
                    FileOperationCanonicalPathState.Missing,
                    IsLeafReparsePoint: false),
                FileOperationExecutionValidationDecision.Ready,
                "ready");
        }

        return new FileOperationExecutionValidationResult(
            plan,
            new FileOperationCanonicalPath(
                sourceDirectory,
                canonicalSourceDirectory,
                FileOperationCanonicalPathState.Directory,
                IsLeafReparsePoint: false,
                Identity: new FileIdentity(4, 40)),
            new FileOperationCanonicalPath(
                destinationDirectory,
                canonicalDestinationDirectory,
                FileOperationCanonicalPathState.Directory,
                IsLeafReparsePoint: false,
                Identity: new FileIdentity(5, 50)),
            items,
            FileOperationExecutionValidationStatus.Ready,
            DateTimeOffset.UtcNow,
            "ready");
    }

    private sealed class HistoryFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "FileOp.ActionHistory.HardLink.Multi.Tests",
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
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
