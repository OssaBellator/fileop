using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileOperationRecoveryBasicMetadataVerificationTests
{
    private const string RootPath = @"D:\Real\Destination";
    private static readonly FileIdentity RootIdentity = new(5, 50);
    private static readonly FileIdentity LeafIdentity = new(6, 60);
    private static readonly FileBasicMetadataEvidence Recorded = new(100, 200, 300, 0x00000022u);

    [TestMethod]
    public async Task LastAccessOnlyDifferenceRemainsSameStableMetadata()
    {
        var inspection = CreateInspection();
        var store = new FakeStore(Recorded, inspection);
        var reader = new FakeReader(request => Success(request, Recorded with { LastAccessTimeFileTime = 201 }));

        var result = await new FileOperationRecoveryBasicMetadataVerifier(store, reader).VerifyAsync(inspection);

        Assert.AreEqual(FileOperationRecoveryBasicMetadataStatus.SameStableMetadata, result.Items[0].Comparison.Status);
        Assert.AreEqual(false, result.Items[0].Comparison.LastAccessTimeMatchesDiagnostic);
        Assert.AreEqual(1, result.SameStableMetadataCount);
        Assert.AreEqual(1, reader.Calls.Count);
    }

    [TestMethod]
    public async Task LastWriteDifferenceIsReportedWithoutMutationAuthority()
    {
        var inspection = CreateInspection();
        var store = new FakeStore(Recorded, inspection);
        var reader = new FakeReader(request => Success(request, Recorded with { LastWriteTimeFileTime = 301 }));

        var result = await new FileOperationRecoveryBasicMetadataVerifier(store, reader).VerifyAsync(inspection);

        Assert.AreEqual(FileOperationRecoveryBasicMetadataStatus.DifferentStableMetadata, result.Items[0].Comparison.Status);
        Assert.AreEqual(false, result.Items[0].Comparison.LastWriteTimeMatches);
        Assert.IsFalse(result.Items[0].SameStableMetadata);
        Assert.AreEqual(FileOperationUndoKind.None, result.Items[0].Inspection.Entry.UndoKind);
        Assert.IsFalse(result.Items[0].Inspection.Entry.IsUndoCandidate);
    }

    [TestMethod]
    public async Task MissingRecordedMetadataSkipsReader()
    {
        var inspection = CreateInspection();
        var reader = new FakeReader(_ => throw new InvalidOperationException("reader should not run"));
        var store = new FakeStore(null, inspection);

        var result = await new FileOperationRecoveryBasicMetadataVerifier(store, reader)
            .VerifyAsync(inspection);

        Assert.AreEqual(FileOperationRecoveryBasicMetadataStatus.NoRecordedEvidence, result.Items[0].Comparison.Status);
        Assert.AreEqual(1, store.MetadataReadCount);
        Assert.AreEqual(0, reader.Calls.Count);
    }

    [TestMethod]
    public async Task UnverifiedRootSkipsReaderAndReturnsUnavailable()
    {
        var baseInspection = CreateInspection();
        var inspection = new FileOperationRecoveryInspection(baseInspection.OperationId, baseInspection.Items);
        var reader = new FakeReader(_ => throw new InvalidOperationException("reader should not run"));
        var store = new FakeStore(Recorded, baseInspection);

        var result = await new FileOperationRecoveryBasicMetadataVerifier(store, reader)
            .VerifyAsync(inspection);

        Assert.AreEqual(FileOperationRecoveryBasicMetadataStatus.Unavailable, result.Items[0].Comparison.Status);
        Assert.AreEqual(0, reader.Calls.Count);
    }

    [TestMethod]
    public async Task MismatchedDurableHistorySkipsMetadataAndReader()
    {
        var inspection = CreateInspection();
        var store = new FakeStore(Recorded, inspection, matchDurableHistory: false);
        var reader = new FakeReader(_ => throw new InvalidOperationException("reader should not run"));

        var result = await new FileOperationRecoveryBasicMetadataVerifier(store, reader)
            .VerifyAsync(inspection);

        Assert.AreEqual(FileOperationRecoveryBasicMetadataStatus.Unavailable, result.Items[0].Comparison.Status);
        Assert.AreEqual(0, store.MetadataReadCount);
        Assert.AreEqual(0, reader.Calls.Count);
        StringAssert.Contains(result.Items[0].Message, "durable action-history");
    }

    [TestMethod]
    public async Task UnsafeReaderStatusReturnsUnavailable()
    {
        var inspection = CreateInspection();
        var reader = new FakeReader(request => new FileBasicMetadataReadResult(
            FileContentFingerprintReadStatus.Busy,
            ExistingDirectory(request.CanonicalDestinationDirectoryPath, request.DestinationDirectoryIdentity),
            ExistingFile(request.CanonicalDestinationPath, request.DestinationIdentity),
            BasicMetadata: null,
            "busy"));

        var result = await new FileOperationRecoveryBasicMetadataVerifier(new FakeStore(Recorded, inspection), reader)
            .VerifyAsync(inspection);

        Assert.AreEqual(FileContentFingerprintReadStatus.Busy, result.Items[0].ReaderStatus);
        Assert.AreEqual(FileOperationRecoveryBasicMetadataStatus.Unavailable, result.Items[0].Comparison.Status);
    }

    [TestMethod]
    public async Task InconsistentSuccessEvidenceFailsClosedAsUnavailable()
    {
        var inspection = CreateInspection();
        var reader = new FakeReader(request => new FileBasicMetadataReadResult(
            FileContentFingerprintReadStatus.Success,
            ExistingDirectory(request.CanonicalDestinationDirectoryPath, request.DestinationDirectoryIdentity),
            ExistingFile(request.CanonicalDestinationPath, new FileIdentity(6, 61)),
            Recorded,
            "inconsistent"));

        var result = await new FileOperationRecoveryBasicMetadataVerifier(new FakeStore(Recorded, inspection), reader)
            .VerifyAsync(inspection);

        Assert.AreEqual(FileOperationRecoveryBasicMetadataStatus.Unavailable, result.Items[0].Comparison.Status);
    }

    private static FileOperationRecoveryInspection CreateInspection()
    {
        var path = RootPath + @"\payload.bin";
        var now = DateTimeOffset.UtcNow;
        var entry = new FileOperationActionEntry(
            0,
            new FileOperationEntry(@"C:\Source\payload.bin", "payload.bin", false),
            @"C:\Real\Source\payload.bin",
            path,
            FileOperationActionEntryState.RecoveryRequired,
            now,
            now,
            new FileIdentity(1, 10),
            LeafIdentity,
            FileOperationUndoKind.None,
            new FileOperationFailure("RecoveryRequired", "fixture", path, false));
        var item = new FileOperationRecoveryInspectionItem(
            0,
            entry,
            FileOperationRecoveryDestinationStatus.SameObject,
            ExistingFile(path, LeafIdentity),
            "same leaf");
        var root = new FileOperationRecoveryRootInspection(
            RootIdentity,
            FileOperationRecoveryRootStatus.SameObject,
            ExistingDirectory(RootPath, RootIdentity),
            "same root",
            RootPath);
        return new FileOperationRecoveryInspection(Guid.NewGuid(), new[] { item }, root);
    }

    private static FileOperationActionHistory CreateDurableHistory(
        FileOperationRecoveryInspection inspection,
        bool matchDurableHistory)
    {
        var now = DateTimeOffset.UtcNow;
        return new FileOperationActionHistory(
            inspection.OperationId,
            now,
            now,
            now,
            now,
            FileOperationKind.Copy,
            FileOperationCollisionPolicy.Stop,
            @"C:\Source",
            @"D:\Destination",
            @"C:\Real\Source",
            RootPath,
            FileOperationActionTerminalState.RecoveryRequired,
            new[] { inspection.Items[0].Entry },
            new FileIdentity(4, 40),
            matchDurableHistory ? RootIdentity : new FileIdentity(5, 51));
    }

    private static FileBasicMetadataReadResult Success(
        FileContentFingerprintReadRequest request,
        FileBasicMetadataEvidence metadata) =>
        new(
            FileContentFingerprintReadStatus.Success,
            ExistingDirectory(request.CanonicalDestinationDirectoryPath, request.DestinationDirectoryIdentity),
            ExistingFile(request.CanonicalDestinationPath, request.DestinationIdentity),
            metadata,
            "success");

    private static FileOperationCanonicalPath ExistingFile(string path, FileIdentity identity) =>
        new(path, path, FileOperationCanonicalPathState.File, false, identity);

    private static FileOperationCanonicalPath ExistingDirectory(string path, FileIdentity identity) =>
        new(path, path, FileOperationCanonicalPathState.Directory, false, identity);

    private sealed class FakeReader : IRootBoundFileBasicMetadataEvidenceReader
    {
        private readonly Func<FileContentFingerprintReadRequest, FileBasicMetadataReadResult> _callback;
        public FakeReader(Func<FileContentFingerprintReadRequest, FileBasicMetadataReadResult> callback) => _callback = callback;
        public List<FileContentFingerprintReadRequest> Calls { get; } = new();
        public ValueTask<FileBasicMetadataReadResult> ReadAsync(FileContentFingerprintReadRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request);
            return ValueTask.FromResult(_callback(request));
        }
    }

    private sealed class FakeStore : IFileOperationActionHistoryBasicMetadataEvidenceStore
    {
        private readonly FileBasicMetadataEvidence? _recorded;
        private readonly FileOperationActionHistory _history;

        public FakeStore(
            FileBasicMetadataEvidence? recorded,
            FileOperationRecoveryInspection inspection,
            bool matchDurableHistory = true)
        {
            _recorded = recorded;
            _history = CreateDurableHistory(inspection, matchDurableHistory);
        }

        public int MetadataReadCount { get; private set; }

        public ValueTask<FileBasicMetadataEvidence?> GetDestinationBasicMetadataEvidenceAsync(Guid operationId, int ordinal, CancellationToken cancellationToken = default)
        {
            MetadataReadCount++;
            return ValueTask.FromResult(_recorded);
        }

        public ValueTask<FileOperationActionHistory?> GetAsync(Guid operationId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<FileOperationActionHistory?>(operationId == _history.OperationId ? _history : null);

        public ValueTask<FileOperationActionHistory> BeginAsync(FileOperationExecutionValidationResult validation, DateTimeOffset startedAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FileOperationActionHistory> MarkMutationStartedAsync(Guid operationId, int ordinal, DateTimeOffset startedAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FileOperationActionHistory> MarkEntryFailedBeforeMutationAsync(Guid operationId, int ordinal, FileOperationFailure failure, DateTimeOffset failedAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FileOperationActionHistory> CommitCopyAsync(Guid operationId, int ordinal, FileIdentity destinationIdentity, FileContentFingerprint destinationContentFingerprint, DateTimeOffset committedAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FileOperationActionHistory> MarkMutationRecoveryRequiredAsync(Guid operationId, int ordinal, FileOperationFailure failure, DateTimeOffset failedAtUtc, CancellationToken cancellationToken = default, FileIdentity? destinationIdentity = null, FileContentFingerprint? destinationContentFingerprint = null) => throw new NotSupportedException();
        public ValueTask<FileOperationActionHistory> CommitCopyWithHardLinkEvidenceAsync(Guid operationId, int ordinal, FileIdentity destinationIdentity, FileContentFingerprint destinationContentFingerprint, uint destinationHardLinkCount, DateTimeOffset committedAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FileOperationActionHistory> MarkMutationRecoveryRequiredWithHardLinkEvidenceAsync(Guid operationId, int ordinal, FileOperationFailure failure, DateTimeOffset failedAtUtc, FileIdentity destinationIdentity, FileContentFingerprint destinationContentFingerprint, uint destinationHardLinkCount, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FileOperationActionHistory> CommitCopyWithBasicMetadataEvidenceAsync(Guid operationId, int ordinal, FileIdentity destinationIdentity, FileContentFingerprint destinationContentFingerprint, FileCopyDestinationCommitBasicMetadataEvidence destinationEvidence, DateTimeOffset committedAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FileOperationActionHistory> MarkMutationRecoveryRequiredWithBasicMetadataEvidenceAsync(Guid operationId, int ordinal, FileOperationFailure failure, DateTimeOffset failedAtUtc, FileIdentity destinationIdentity, FileContentFingerprint destinationContentFingerprint, FileCopyDestinationCommitBasicMetadataEvidence destinationEvidence, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<FileOperationActionHistory> CompleteAsync(Guid operationId, FileOperationActionTerminalState terminalState, DateTimeOffset completedAtUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<FileOperationActionHistory>> GetRecentAsync(int limit = 100, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
