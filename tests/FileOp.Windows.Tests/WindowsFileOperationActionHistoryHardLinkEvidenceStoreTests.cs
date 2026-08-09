using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsFileOperationActionHistoryHardLinkEvidenceStoreTests
{
    private static readonly FileContentFingerprint Fingerprint = new(
        FileContentFingerprintAlgorithm.Sha256,
        "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");

    [TestMethod]
    public async Task LegacyCommitCallCollectsCountAndPersistsStrongEvidence()
    {
        using var fixture = new HistoryFixture();
        var validation = CreateValidation();
        using var sqlite = new SqliteFileOperationActionHistoryHardLinkEvidenceStore(fixture.DatabasePath);
        var source = new FakeEvidenceSource(4);
        var store = new WindowsFileOperationActionHistoryHardLinkEvidenceStore(sqlite, source);
        await store.BeginAsync(validation, DateTimeOffset.UtcNow);
        await store.MarkMutationStartedAsync(validation.Plan.Id, 0, DateTimeOffset.UtcNow);
        var destinationIdentity = new FileIdentity(7, 70);

        var committed = await store.CommitCopyAsync(
            validation.Plan.Id,
            0,
            destinationIdentity,
            Fingerprint,
            DateTimeOffset.UtcNow);

        Assert.AreEqual(1, source.Calls.Count);
        Assert.AreEqual(destinationIdentity, source.Calls[0].ExpectedDestinationIdentity);
        Assert.AreEqual(FileOperationActionEntryState.MutationStarted, source.Calls[0].History.Entries[0].State);
        Assert.AreEqual(4u, committed.Entries[0].DestinationHardLinkCount);

        using var reopened = new SqliteFileOperationActionHistoryHardLinkEvidenceStore(fixture.DatabasePath);
        var persisted = await reopened.GetAsync(validation.Plan.Id);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(4u, persisted.Entries[0].DestinationHardLinkCount);
    }

    [TestMethod]
    public async Task FailedStrongCommitCarriesExactCountIntoRecovery()
    {
        var history = CreateMutationStartedHistory();
        var inner = new FakeStrongStore(history) { ThrowStrongCommit = true };
        var source = new FakeEvidenceSource(7);
        var store = new WindowsFileOperationActionHistoryHardLinkEvidenceStore(inner, source);
        var destinationIdentity = new FileIdentity(8, 80);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await store.CommitCopyAsync(
                history.OperationId,
                0,
                destinationIdentity,
                Fingerprint,
                DateTimeOffset.UtcNow));

        var failure = new FileOperationFailure(
            "CopyCommitBarrierFailed",
            "fixture",
            history.Entries[0].CanonicalDestinationPath,
            Retryable: false);
        await store.MarkMutationRecoveryRequiredAsync(
            history.OperationId,
            0,
            failure,
            DateTimeOffset.UtcNow,
            destinationIdentity: destinationIdentity,
            destinationContentFingerprint: Fingerprint);

        Assert.AreEqual(1, inner.StrongRecoveryCalls.Count);
        Assert.AreEqual(7u, inner.StrongRecoveryCalls[0].HardLinkCount);
        Assert.AreEqual(destinationIdentity, inner.StrongRecoveryCalls[0].DestinationIdentity);
        Assert.AreEqual(Fingerprint, inner.StrongRecoveryCalls[0].Fingerprint);
        Assert.AreEqual(0, inner.LegacyRecoveryCalls.Count);
    }

    [TestMethod]
    public async Task EvidenceSourceFailureDropsIdentityFingerprintFromRecovery()
    {
        var history = CreateMutationStartedHistory();
        var inner = new FakeStrongStore(history);
        var source = new FakeEvidenceSource(new IOException("topology unavailable"));
        var store = new WindowsFileOperationActionHistoryHardLinkEvidenceStore(inner, source);
        var destinationIdentity = new FileIdentity(9, 90);

        await Assert.ThrowsExceptionAsync<IOException>(async () =>
            await store.CommitCopyAsync(
                history.OperationId,
                0,
                destinationIdentity,
                Fingerprint,
                DateTimeOffset.UtcNow));
        Assert.AreEqual(0, inner.StrongCommitCalls.Count);

        var failure = new FileOperationFailure(
            "CopyCommitBarrierFailed",
            "fixture",
            history.Entries[0].CanonicalDestinationPath,
            Retryable: false);
        await store.MarkMutationRecoveryRequiredAsync(
            history.OperationId,
            0,
            failure,
            DateTimeOffset.UtcNow,
            destinationIdentity: destinationIdentity,
            destinationContentFingerprint: Fingerprint);

        Assert.AreEqual(0, inner.StrongRecoveryCalls.Count);
        Assert.AreEqual(1, inner.LegacyRecoveryCalls.Count);
        Assert.IsNull(inner.LegacyRecoveryCalls[0].DestinationIdentity);
        Assert.IsNull(inner.LegacyRecoveryCalls[0].Fingerprint);
    }

    [TestMethod]
    public async Task CompletionClearsUnusedPendingObservation()
    {
        var history = CreateMutationStartedHistory();
        var inner = new FakeStrongStore(history) { ThrowStrongCommit = true };
        var store = new WindowsFileOperationActionHistoryHardLinkEvidenceStore(inner, new FakeEvidenceSource(6));

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await store.CommitCopyAsync(
                history.OperationId,
                0,
                new FileIdentity(10, 100),
                Fingerprint,
                DateTimeOffset.UtcNow));

        await store.CompleteAsync(
            history.OperationId,
            FileOperationActionTerminalState.RecoveryRequired,
            DateTimeOffset.UtcNow);

        await store.MarkMutationRecoveryRequiredAsync(
            history.OperationId,
            0,
            new FileOperationFailure("Recovery", "fixture", null, false),
            DateTimeOffset.UtcNow,
            destinationIdentity: new FileIdentity(10, 100),
            destinationContentFingerprint: Fingerprint);

        Assert.AreEqual(0, inner.StrongRecoveryCalls.Count);
        Assert.AreEqual(1, inner.LegacyRecoveryCalls.Count);
    }

    private static FileOperationExecutionValidationResult CreateValidation()
    {
        var sourceDirectory = Path.GetFullPath(@"C:\Source");
        var destinationDirectory = Path.GetFullPath(@"D:\Destination");
        var canonicalSourceDirectory = Path.GetFullPath(@"C:\Real\Source");
        var canonicalDestinationDirectory = Path.GetFullPath(@"D:\Real\Destination");
        var entry = new FileOperationEntry(Path.Combine(sourceDirectory, "a.txt"), "a.txt", IsDirectory: false);
        var plan = new FileOperationPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            FileOperationKind.Copy,
            FileOperationCollisionPolicy.Stop,
            new FileOperationIntent("Left", Guid.NewGuid(), sourceDirectory, new[] { entry }, "Right", Guid.NewGuid(), destinationDirectory));
        return new FileOperationExecutionValidationResult(
            plan,
            new FileOperationCanonicalPath(sourceDirectory, canonicalSourceDirectory, FileOperationCanonicalPathState.Directory, false, new FileIdentity(4, 40)),
            new FileOperationCanonicalPath(destinationDirectory, canonicalDestinationDirectory, FileOperationCanonicalPathState.Directory, false, new FileIdentity(5, 50)),
            new[]
            {
                new FileOperationExecutionValidationItem(
                    entry,
                    new FileOperationCanonicalPath(entry.Path, Path.Combine(canonicalSourceDirectory, entry.Name), FileOperationCanonicalPathState.File, false, new FileIdentity(3, 30)),
                    new FileOperationCanonicalPath(Path.Combine(destinationDirectory, entry.Name), Path.Combine(canonicalDestinationDirectory, entry.Name), FileOperationCanonicalPathState.Missing, false),
                    FileOperationExecutionValidationDecision.Ready,
                    "ready"),
            },
            FileOperationExecutionValidationStatus.Ready,
            DateTimeOffset.UtcNow,
            "ready");
    }

    private static FileOperationActionHistory CreateMutationStartedHistory()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = new FileOperationActionEntry(
            0,
            new FileOperationEntry(@"C:\Source\a.txt", "a.txt", false),
            @"C:\Real\Source\a.txt",
            @"D:\Real\Destination\a.txt",
            FileOperationActionEntryState.MutationStarted,
            now,
            null,
            new FileIdentity(3, 30),
            null,
            FileOperationUndoKind.None,
            null);
        return new FileOperationActionHistory(
            Guid.NewGuid(), now, now, now, null,
            FileOperationKind.Copy,
            FileOperationCollisionPolicy.Stop,
            @"C:\Source", @"D:\Destination", @"C:\Real\Source", @"D:\Real\Destination",
            null,
            new[] { entry },
            new FileIdentity(4, 40),
            new FileIdentity(5, 50));
    }

    private sealed class FakeEvidenceSource : IFileCopyDestinationHardLinkEvidenceSource
    {
        private readonly uint _count;
        private readonly Exception? _exception;
        public FakeEvidenceSource(uint count) => _count = count;
        public FakeEvidenceSource(Exception exception) => _exception = exception;
        public List<Call> Calls { get; } = new();

        public ValueTask<uint> ReadVerifiedHardLinkCountAsync(
            FileOperationActionHistory history,
            int ordinal,
            FileIdentity expectedDestinationIdentity,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new Call(history, ordinal, expectedDestinationIdentity));
            if (_exception is not null) throw _exception;
            return ValueTask.FromResult(_count);
        }

        public sealed record Call(FileOperationActionHistory History, int Ordinal, FileIdentity ExpectedDestinationIdentity);
    }

    private sealed class FakeStrongStore : IFileOperationActionHistoryHardLinkEvidenceStore
    {
        private readonly FileOperationActionHistory _history;
        public FakeStrongStore(FileOperationActionHistory history) => _history = history;
        public bool ThrowStrongCommit { get; init; }
        public List<StrongCall> StrongCommitCalls { get; } = new();
        public List<StrongCall> StrongRecoveryCalls { get; } = new();
        public List<LegacyRecoveryCall> LegacyRecoveryCalls { get; } = new();

        public ValueTask<FileOperationActionHistory> BeginAsync(FileOperationExecutionValidationResult validation, DateTimeOffset startedAtUtc, CancellationToken cancellationToken = default) => ValueTask.FromResult(_history);
        public ValueTask<FileOperationActionHistory> MarkMutationStartedAsync(Guid operationId, int ordinal, DateTimeOffset startedAtUtc, CancellationToken cancellationToken = default) => ValueTask.FromResult(_history);
        public ValueTask<FileOperationActionHistory> MarkEntryFailedBeforeMutationAsync(Guid operationId, int ordinal, FileOperationFailure failure, DateTimeOffset failedAtUtc, CancellationToken cancellationToken = default) => ValueTask.FromResult(_history);
        public ValueTask<FileOperationActionHistory> CommitCopyAsync(Guid operationId, int ordinal, FileIdentity destinationIdentity, FileContentFingerprint destinationContentFingerprint, DateTimeOffset committedAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<FileOperationActionHistory> MarkMutationRecoveryRequiredAsync(
            Guid operationId, int ordinal, FileOperationFailure failure, DateTimeOffset failedAtUtc,
            CancellationToken cancellationToken = default, FileIdentity? destinationIdentity = null,
            FileContentFingerprint? destinationContentFingerprint = null)
        {
            LegacyRecoveryCalls.Add(new LegacyRecoveryCall(destinationIdentity, destinationContentFingerprint));
            return ValueTask.FromResult(_history);
        }

        public ValueTask<FileOperationActionHistory> CommitCopyWithHardLinkEvidenceAsync(
            Guid operationId, int ordinal, FileIdentity destinationIdentity,
            FileContentFingerprint destinationContentFingerprint, uint destinationHardLinkCount,
            DateTimeOffset committedAtUtc, CancellationToken cancellationToken = default)
        {
            StrongCommitCalls.Add(new StrongCall(destinationIdentity, destinationContentFingerprint, destinationHardLinkCount));
            if (ThrowStrongCommit) throw new InvalidOperationException("fixture commit failure");
            return ValueTask.FromResult(_history);
        }

        public ValueTask<FileOperationActionHistory> MarkMutationRecoveryRequiredWithHardLinkEvidenceAsync(
            Guid operationId, int ordinal, FileOperationFailure failure, DateTimeOffset failedAtUtc,
            FileIdentity destinationIdentity, FileContentFingerprint destinationContentFingerprint,
            uint destinationHardLinkCount, CancellationToken cancellationToken = default)
        {
            StrongRecoveryCalls.Add(new StrongCall(destinationIdentity, destinationContentFingerprint, destinationHardLinkCount));
            return ValueTask.FromResult(_history);
        }

        public ValueTask<FileOperationActionHistory> CompleteAsync(Guid operationId, FileOperationActionTerminalState terminalState, DateTimeOffset completedAtUtc, CancellationToken cancellationToken = default) => ValueTask.FromResult(_history);
        public ValueTask<FileOperationActionHistory?> GetAsync(Guid operationId, CancellationToken cancellationToken = default) => ValueTask.FromResult<FileOperationActionHistory?>(_history);
        public ValueTask<IReadOnlyList<FileOperationActionHistory>> GetRecentAsync(int limit = 100, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<FileOperationActionHistory>>(new[] { _history });

        public sealed record StrongCall(FileIdentity DestinationIdentity, FileContentFingerprint Fingerprint, uint HardLinkCount);
        public sealed record LegacyRecoveryCall(FileIdentity? DestinationIdentity, FileContentFingerprint? Fingerprint);
    }

    private sealed class HistoryFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "FileOp.ActionHistory.HardLink.WindowsStore.Tests", Guid.NewGuid().ToString("N"));
        public HistoryFixture() { Directory.CreateDirectory(_directory); DatabasePath = Path.Combine(_directory, "actions.sqlite"); }
        public string DatabasePath { get; }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
