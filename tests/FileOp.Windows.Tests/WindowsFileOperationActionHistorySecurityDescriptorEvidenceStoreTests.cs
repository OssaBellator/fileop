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
public sealed class WindowsFileOperationActionHistorySecurityDescriptorEvidenceStoreTests
{
    private static readonly FileContentFingerprint Fingerprint = new(
        FileContentFingerprintAlgorithm.Sha256,
        "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd");
    private static readonly FileCopyDestinationCommitSecurityDescriptorEvidence Evidence = new(
        new FileCopyDestinationCommitBasicMetadataEvidence(
            3,
            new FileBasicMetadataEvidence(100, 200, 300, 0x2021)),
        new FileSecurityDescriptorEvidence(
            FileSecurityDescriptorEvidence.QueriedSecurityInformationMask,
            "abababababababababababababababababababababababababababababababab"));

    [TestMethod]
    public async Task LegacyCommitCallCollectsAndPersistsSecurityEvidence()
    {
        var history = CreateMutationStartedHistory();
        var inner = new FakeStore(history);
        var source = new FakeSource(Evidence);
        var store = new WindowsFileOperationActionHistorySecurityDescriptorEvidenceStore(inner, source);
        var identity = new FileIdentity(7, 70);

        await store.CommitCopyAsync(history.OperationId, 0, identity, Fingerprint, DateTimeOffset.UtcNow);

        Assert.AreEqual(1, source.Calls.Count);
        Assert.AreEqual(1, inner.SecurityCommitCalls.Count);
        Assert.AreEqual(identity, inner.SecurityCommitCalls[0].Identity);
        Assert.AreEqual(Fingerprint, inner.SecurityCommitCalls[0].Fingerprint);
        Assert.AreEqual(Evidence, inner.SecurityCommitCalls[0].Evidence);
    }

    [TestMethod]
    public async Task FailedStrongCommitReusesExactSecurityObservationForRecovery()
    {
        var history = CreateMutationStartedHistory();
        var inner = new FakeStore(history) { ThrowSecurityCommit = true };
        var store = new WindowsFileOperationActionHistorySecurityDescriptorEvidenceStore(inner, new FakeSource(Evidence));
        var identity = new FileIdentity(8, 80);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CommitCopyAsync(history.OperationId, 0, identity, Fingerprint, DateTimeOffset.UtcNow));

        await store.MarkMutationRecoveryRequiredAsync(
            history.OperationId,
            0,
            new FileOperationFailure("CommitFailed", "fixture", null, false),
            DateTimeOffset.UtcNow,
            destinationIdentity: identity,
            destinationContentFingerprint: Fingerprint);

        Assert.AreEqual(1, inner.SecurityRecoveryCalls.Count);
        Assert.AreEqual(Evidence, inner.SecurityRecoveryCalls[0].Evidence);
        Assert.AreEqual(0, inner.LegacyRecoveryCalls.Count);
    }

    [TestMethod]
    public async Task MismatchedRecoveryEvidenceDowngradesToNoPartialProof()
    {
        var history = CreateMutationStartedHistory();
        var inner = new FakeStore(history) { ThrowSecurityCommit = true };
        var store = new WindowsFileOperationActionHistorySecurityDescriptorEvidenceStore(inner, new FakeSource(Evidence));
        var identity = new FileIdentity(9, 90);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.CommitCopyAsync(history.OperationId, 0, identity, Fingerprint, DateTimeOffset.UtcNow));

        var differentFingerprint = new FileContentFingerprint(
            FileContentFingerprintAlgorithm.Sha256,
            "efefefefefefefefefefefefefefefefefefefefefefefefefefefefefefefef");
        await store.MarkMutationRecoveryRequiredAsync(
            history.OperationId,
            0,
            new FileOperationFailure("CommitFailed", "fixture", null, false),
            DateTimeOffset.UtcNow,
            destinationIdentity: identity,
            destinationContentFingerprint: differentFingerprint);

        Assert.AreEqual(0, inner.SecurityRecoveryCalls.Count);
        Assert.AreEqual(1, inner.LegacyRecoveryCalls.Count);
        Assert.IsNull(inner.LegacyRecoveryCalls[0].Identity);
        Assert.IsNull(inner.LegacyRecoveryCalls[0].Fingerprint);
    }

    [TestMethod]
    public async Task EvidenceCollectionFailureLeavesNoTrustedObservationForRecovery()
    {
        var history = CreateMutationStartedHistory();
        var inner = new FakeStore(history);
        var store = new WindowsFileOperationActionHistorySecurityDescriptorEvidenceStore(
            inner,
            new FakeSource(new IOException("security unavailable")));
        var identity = new FileIdentity(10, 100);

        await Assert.ThrowsAsync<IOException>(async () =>
            await store.CommitCopyAsync(history.OperationId, 0, identity, Fingerprint, DateTimeOffset.UtcNow));

        await store.MarkMutationRecoveryRequiredAsync(
            history.OperationId,
            0,
            new FileOperationFailure("CommitFailed", "fixture", null, false),
            DateTimeOffset.UtcNow,
            destinationIdentity: identity,
            destinationContentFingerprint: Fingerprint);

        Assert.AreEqual(0, inner.SecurityRecoveryCalls.Count);
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

    private sealed class FakeSource : IFileCopyDestinationCommitSecurityDescriptorEvidenceSource
    {
        private readonly FileCopyDestinationCommitSecurityDescriptorEvidence? _evidence;
        private readonly Exception? _exception;
        public FakeSource(FileCopyDestinationCommitSecurityDescriptorEvidence evidence) => _evidence = evidence;
        public FakeSource(Exception exception) => _exception = exception;
        public List<FileIdentity> Calls { get; } = new();

        public ValueTask<FileCopyDestinationCommitSecurityDescriptorEvidence> ReadVerifiedEvidenceAsync(
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

    private sealed class FakeStore : IFileOperationActionHistorySecurityDescriptorEvidenceStore
    {
        private readonly FileOperationActionHistory _history;
        public FakeStore(FileOperationActionHistory history) => _history = history;
        public bool ThrowSecurityCommit { get; init; }
        public List<SecurityCall> SecurityCommitCalls { get; } = new();
        public List<SecurityCall> SecurityRecoveryCalls { get; } = new();
        public List<LegacyCall> LegacyRecoveryCalls { get; } = new();

        public ValueTask<FileOperationActionHistory> BeginAsync(FileOperationExecutionValidationResult validation, DateTimeOffset startedAtUtc, CancellationToken cancellationToken = default) => ValueTask.FromResult(_history);
        public ValueTask<FileOperationActionHistory> MarkMutationStartedAsync(Guid operationId, int ordinal, DateTimeOffset startedAtUtc, CancellationToken cancellationToken = default) => ValueTask.FromResult(_history);
        public ValueTask<FileOperationActionHistory> MarkEntryFailedBeforeMutationAsync(Guid operationId, int ordinal, FileOperationFailure failure, DateTimeOffset failedAtUtc, CancellationToken cancellationToken = default) => ValueTask.FromResult(_history);
        public ValueTask<FileOperationActionHistory> CommitCopyAsync(Guid operationId, int ordinal, FileIdentity destinationIdentity, FileContentFingerprint destinationContentFingerprint, DateTimeOffset committedAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<FileOperationActionHistory> MarkMutationRecoveryRequiredAsync(Guid operationId, int ordinal, FileOperationFailure failure, DateTimeOffset failedAtUtc, CancellationToken cancellationToken = default, FileIdentity? destinationIdentity = null, FileContentFingerprint? destinationContentFingerprint = null)
        {
            LegacyRecoveryCalls.Add(new LegacyCall(destinationIdentity, destinationContentFingerprint));
            return ValueTask.FromResult(_history);
        }

        public ValueTask<FileOperationActionHistory> CommitCopyWithHardLinkEvidenceAsync(Guid operationId, int ordinal, FileIdentity destinationIdentity, FileContentFingerprint destinationContentFingerprint, uint destinationHardLinkCount, DateTimeOffset committedAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FileOperationActionHistory> MarkMutationRecoveryRequiredWithHardLinkEvidenceAsync(Guid operationId, int ordinal, FileOperationFailure failure, DateTimeOffset failedAtUtc, FileIdentity destinationIdentity, FileContentFingerprint destinationContentFingerprint, uint destinationHardLinkCount, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FileOperationActionHistory> CommitCopyWithBasicMetadataEvidenceAsync(Guid operationId, int ordinal, FileIdentity destinationIdentity, FileContentFingerprint destinationContentFingerprint, FileCopyDestinationCommitBasicMetadataEvidence destinationEvidence, DateTimeOffset committedAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FileOperationActionHistory> MarkMutationRecoveryRequiredWithBasicMetadataEvidenceAsync(Guid operationId, int ordinal, FileOperationFailure failure, DateTimeOffset failedAtUtc, FileIdentity destinationIdentity, FileContentFingerprint destinationContentFingerprint, FileCopyDestinationCommitBasicMetadataEvidence destinationEvidence, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<FileOperationActionHistory> CommitCopyWithSecurityDescriptorEvidenceAsync(
            Guid operationId, int ordinal, FileIdentity destinationIdentity,
            FileContentFingerprint destinationContentFingerprint,
            FileCopyDestinationCommitSecurityDescriptorEvidence destinationEvidence,
            DateTimeOffset committedAtUtc, CancellationToken cancellationToken = default)
        {
            SecurityCommitCalls.Add(new SecurityCall(destinationIdentity, destinationContentFingerprint, destinationEvidence));
            if (ThrowSecurityCommit) throw new InvalidOperationException("fixture commit failure");
            return ValueTask.FromResult(_history);
        }

        public ValueTask<FileOperationActionHistory> MarkMutationRecoveryRequiredWithSecurityDescriptorEvidenceAsync(
            Guid operationId, int ordinal, FileOperationFailure failure, DateTimeOffset failedAtUtc,
            FileIdentity destinationIdentity, FileContentFingerprint destinationContentFingerprint,
            FileCopyDestinationCommitSecurityDescriptorEvidence destinationEvidence,
            CancellationToken cancellationToken = default)
        {
            SecurityRecoveryCalls.Add(new SecurityCall(destinationIdentity, destinationContentFingerprint, destinationEvidence));
            return ValueTask.FromResult(_history);
        }

        public ValueTask<FileOperationActionHistory> CompleteAsync(Guid operationId, FileOperationActionTerminalState terminalState, DateTimeOffset completedAtUtc, CancellationToken cancellationToken = default) => ValueTask.FromResult(_history);
        public ValueTask<FileOperationActionHistory?> GetAsync(Guid operationId, CancellationToken cancellationToken = default) => ValueTask.FromResult<FileOperationActionHistory?>(_history);
        public ValueTask<IReadOnlyList<FileOperationActionHistory>> GetRecentAsync(int limit = 100, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<FileOperationActionHistory>>(new[] { _history });
        public ValueTask<FileBasicMetadataEvidence?> GetDestinationBasicMetadataEvidenceAsync(Guid operationId, int ordinal, CancellationToken cancellationToken = default) => ValueTask.FromResult<FileBasicMetadataEvidence?>(null);
        public ValueTask<FileSecurityDescriptorEvidence?> GetDestinationSecurityDescriptorEvidenceAsync(Guid operationId, int ordinal, CancellationToken cancellationToken = default) => ValueTask.FromResult<FileSecurityDescriptorEvidence?>(null);

        public sealed record SecurityCall(FileIdentity Identity, FileContentFingerprint Fingerprint, FileCopyDestinationCommitSecurityDescriptorEvidence Evidence);
        public sealed record LegacyCall(FileIdentity? Identity, FileContentFingerprint? Fingerprint);
    }
}
