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
public sealed class WindowsFileOperationActionHistoryBasicMetadataEvidenceStoreTests
{
    private static readonly FileContentFingerprint Fingerprint = new(
        FileContentFingerprintAlgorithm.Sha256,
        "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd");
    private static readonly FileCopyDestinationCommitBasicMetadataEvidence Evidence = new(
        3,
        new FileBasicMetadataEvidence(100, 200, 300, 0x2021));

    [TestMethod]
    public async Task LegacyCommitCallPersistsCombinedEvidence()
    {
        var history = CreateMutationStartedHistory();
        var inner = new FakeStore(history);
        var source = new FakeSource(Evidence);
        var store = new WindowsFileOperationActionHistoryBasicMetadataEvidenceStore(inner, source);
        var identity = new FileIdentity(7, 70);

        await store.CommitCopyAsync(history.OperationId, 0, identity, Fingerprint, DateTimeOffset.UtcNow);

        Assert.AreEqual(1, source.Calls.Count);
        Assert.AreEqual(1, inner.CombinedCommitCalls.Count);
        Assert.AreEqual(identity, inner.CombinedCommitCalls[0].Identity);
        Assert.AreEqual(Fingerprint, inner.CombinedCommitCalls[0].Fingerprint);
        Assert.AreEqual(Evidence, inner.CombinedCommitCalls[0].Evidence);
    }

    [TestMethod]
    public async Task FailedCombinedCommitReusesExactMetadataEvidenceForRecovery()
    {
        var history = CreateMutationStartedHistory();
        var inner = new FakeStore(history) { ThrowCombinedCommit = true };
        var store = new WindowsFileOperationActionHistoryBasicMetadataEvidenceStore(inner, new FakeSource(Evidence));
        var identity = new FileIdentity(8, 80);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await store.CommitCopyAsync(history.OperationId, 0, identity, Fingerprint, DateTimeOffset.UtcNow));

        await store.MarkMutationRecoveryRequiredAsync(
            history.OperationId,
            0,
            new FileOperationFailure("CommitFailed", "fixture", null, false),
            DateTimeOffset.UtcNow,
            destinationIdentity: identity,
            destinationContentFingerprint: Fingerprint);

        Assert.AreEqual(1, inner.CombinedRecoveryCalls.Count);
        Assert.AreEqual(Evidence, inner.CombinedRecoveryCalls[0].Evidence);
        Assert.AreEqual(0, inner.LegacyRecoveryCalls.Count);
    }

    [TestMethod]
    public async Task EvidenceCollectionFailureDowngradesRecoveryToNoPartialProof()
    {
        var history = CreateMutationStartedHistory();
        var inner = new FakeStore(history);
        var store = new WindowsFileOperationActionHistoryBasicMetadataEvidenceStore(
            inner,
            new FakeSource(new IOException("metadata unavailable")));
        var identity = new FileIdentity(9, 90);

        await Assert.ThrowsExceptionAsync<IOException>(async () =>
            await store.CommitCopyAsync(history.OperationId, 0, identity, Fingerprint, DateTimeOffset.UtcNow));

        await store.MarkMutationRecoveryRequiredAsync(
            history.OperationId,
            0,
            new FileOperationFailure("CommitFailed", "fixture", null, false),
            DateTimeOffset.UtcNow,
            destinationIdentity: identity,
            destinationContentFingerprint: Fingerprint);

        Assert.AreEqual(0, inner.CombinedRecoveryCalls.Count);
        Assert.AreEqual(1, inner.LegacyRecoveryCalls.Count);
        Assert.IsNull(inner.LegacyRecoveryCalls[0].Identity);
        Assert.IsNull(inner.LegacyRecoveryCalls[0].Fingerprint);
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
            FileOperationKind.Copy, FileOperationCollisionPolicy.Stop,
            @"C:\Source", @"D:\Destination", @"C:\Real\Source", @"D:\Real\Destination",
            null,
            new[] { entry },
            new FileIdentity(4, 40),
            new FileIdentity(5, 50));
    }

    private sealed class FakeSource : IFileCopyDestinationCommitBasicMetadataEvidenceSource
    {
        private readonly FileCopyDestinationCommitBasicMetadataEvidence? _evidence;
        private readonly Exception? _exception;
        public FakeSource(FileCopyDestinationCommitBasicMetadataEvidence evidence) => _evidence = evidence;
        public FakeSource(Exception exception) => _exception = exception;
        public List<FileIdentity> Calls { get; } = new();

        public ValueTask<FileCopyDestinationCommitBasicMetadataEvidence> ReadVerifiedEvidenceAsync(
            FileOperationActionHistory history,
            int ordinal,
            FileIdentity expectedDestinationIdentity,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(expectedDestinationIdentity);
            if (_exception is not null) throw _exception;
            return ValueTask.FromResult(_evidence!);
        }
    }

    private sealed class FakeStore : IFileOperationActionHistoryBasicMetadataEvidenceStore
    {
        private readonly FileOperationActionHistory _history;
        public FakeStore(FileOperationActionHistory history) => _history = history;
        public bool ThrowCombinedCommit { get; init; }
        public List<CombinedCall> CombinedCommitCalls { get; } = new();
        public List<CombinedCall> CombinedRecoveryCalls { get; } = new();
        public List<LegacyCall> LegacyRecoveryCalls { get; } = new();

        public ValueTask<FileOperationActionHistory> BeginAsync(FileOperationExecutionValidationResult validation, DateTimeOffset startedAtUtc, CancellationToken cancellationToken = default) => ValueTask.FromResult(_history);
        public ValueTask<FileOperationActionHistory> MarkMutationStartedAsync(Guid operationId, int ordinal, DateTimeOffset startedAtUtc, CancellationToken cancellationToken = default) => ValueTask.FromResult(_history);
        public ValueTask<FileOperationActionHistory> MarkEntryFailedBeforeMutationAsync(Guid operationId, int ordinal, FileOperationFailure failure, DateTimeOffset failedAtUtc, CancellationToken cancellationToken = default) => ValueTask.FromResult(_history);
        public ValueTask<FileOperationActionHistory> CommitCopyAsync(Guid operationId, int ordinal, FileIdentity destinationIdentity, FileContentFingerprint destinationContentFingerprint, DateTimeOffset committedAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<FileOperationActionHistory> MarkMutationRecoveryRequiredAsync(
            Guid operationId, int ordinal, FileOperationFailure failure, DateTimeOffset failedAtUtc,
            CancellationToken cancellationToken = default, FileIdentity? destinationIdentity = null,
            FileContentFingerprint? destinationContentFingerprint = null)
        {
            LegacyRecoveryCalls.Add(new LegacyCall(destinationIdentity, destinationContentFingerprint));
            return ValueTask.FromResult(_history);
        }

        public ValueTask<FileOperationActionHistory> CommitCopyWithHardLinkEvidenceAsync(Guid operationId, int ordinal, FileIdentity destinationIdentity, FileContentFingerprint destinationContentFingerprint, uint destinationHardLinkCount, DateTimeOffset committedAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FileOperationActionHistory> MarkMutationRecoveryRequiredWithHardLinkEvidenceAsync(Guid operationId, int ordinal, FileOperationFailure failure, DateTimeOffset failedAtUtc, FileIdentity destinationIdentity, FileContentFingerprint destinationContentFingerprint, uint destinationHardLinkCount, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<FileOperationActionHistory> CommitCopyWithBasicMetadataEvidenceAsync(
            Guid operationId, int ordinal, FileIdentity destinationIdentity,
            FileContentFingerprint destinationContentFingerprint,
            FileCopyDestinationCommitBasicMetadataEvidence destinationEvidence,
            DateTimeOffset committedAtUtc, CancellationToken cancellationToken = default)
        {
            CombinedCommitCalls.Add(new CombinedCall(destinationIdentity, destinationContentFingerprint, destinationEvidence));
            if (ThrowCombinedCommit) throw new InvalidOperationException("fixture commit failure");
            return ValueTask.FromResult(_history);
        }

        public ValueTask<FileOperationActionHistory> MarkMutationRecoveryRequiredWithBasicMetadataEvidenceAsync(
            Guid operationId, int ordinal, FileOperationFailure failure, DateTimeOffset failedAtUtc,
            FileIdentity destinationIdentity, FileContentFingerprint destinationContentFingerprint,
            FileCopyDestinationCommitBasicMetadataEvidence destinationEvidence,
            CancellationToken cancellationToken = default)
        {
            CombinedRecoveryCalls.Add(new CombinedCall(destinationIdentity, destinationContentFingerprint, destinationEvidence));
            return ValueTask.FromResult(_history);
        }

        public ValueTask<FileOperationActionHistory> CompleteAsync(Guid operationId, FileOperationActionTerminalState terminalState, DateTimeOffset completedAtUtc, CancellationToken cancellationToken = default) => ValueTask.FromResult(_history);
        public ValueTask<FileOperationActionHistory?> GetAsync(Guid operationId, CancellationToken cancellationToken = default) => ValueTask.FromResult<FileOperationActionHistory?>(_history);
        public ValueTask<IReadOnlyList<FileOperationActionHistory>> GetRecentAsync(int limit = 100, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<FileOperationActionHistory>>(new[] { _history });
        public ValueTask<FileBasicMetadataEvidence?> GetDestinationBasicMetadataEvidenceAsync(Guid operationId, int ordinal, CancellationToken cancellationToken = default) => ValueTask.FromResult<FileBasicMetadataEvidence?>(null);

        public sealed record CombinedCall(FileIdentity Identity, FileContentFingerprint Fingerprint, FileCopyDestinationCommitBasicMetadataEvidence Evidence);
        public sealed record LegacyCall(FileIdentity? Identity, FileContentFingerprint? Fingerprint);
    }
}
