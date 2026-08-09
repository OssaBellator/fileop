using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileOperationRecoveryContentVerificationTests
{
    private const string RootPath = @"D:\Real\Destination";
    private static readonly FileIdentity RootIdentity = new(99, 999);
    private static readonly FileContentFingerprint RecordedFingerprint = new(
        FileContentFingerprintAlgorithm.Sha256,
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

    [TestMethod]
    public async Task MatchingMainStreamRequiresRootAndLeafProvenanceAndRemainsEvidenceOnly()
    {
        var identity = new FileIdentity(7, 70);
        var inspectionItem = CreateInspectionItem(
            FileOperationRecoveryDestinationStatus.SameObject,
            identity,
            RecordedFingerprint);
        var reader = new FakeReader(call => Success(call.Request, RecordedFingerprint));
        var verifier = new FileOperationRecoveryContentVerifier(reader);

        var result = await verifier.VerifyAsync(CreateInspection(inspectionItem));

        Assert.AreEqual(1, result.Items.Count);
        Assert.AreEqual(
            FileOperationRecoveryContentStatus.MatchesRecordedMainStream,
            result.Items[0].Status);
        Assert.IsTrue(result.Items[0].MatchesRecordedMainStream);
        Assert.AreEqual(RecordedFingerprint, result.Items[0].RecordedContentFingerprint);
        Assert.AreEqual(RecordedFingerprint, result.Items[0].CurrentContentFingerprint);
        Assert.AreEqual(1, result.MatchingMainStreamCount);
        Assert.AreEqual(1, reader.Calls.Count);
        var request = reader.Calls[0].Request;
        Assert.AreEqual(RootPath, request.CanonicalDestinationDirectoryPath);
        Assert.AreEqual(RootIdentity, request.DestinationDirectoryIdentity);
        Assert.AreEqual(inspectionItem.Entry.CanonicalDestinationPath, request.CanonicalDestinationPath);
        Assert.AreEqual(identity, request.DestinationIdentity);
        Assert.AreEqual(FileOperationUndoKind.None, result.Items[0].Inspection.Entry.UndoKind);
        Assert.IsFalse(result.Items[0].Inspection.Entry.IsUndoCandidate);
    }

    [TestMethod]
    public async Task DifferentDigestIsDifferentMainStream()
    {
        var identity = new FileIdentity(8, 80);
        var inspectionItem = CreateInspectionItem(
            FileOperationRecoveryDestinationStatus.SameObject,
            identity,
            RecordedFingerprint);
        var different = new FileContentFingerprint(
            FileContentFingerprintAlgorithm.Sha256,
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var reader = new FakeReader(call => Success(call.Request, different));

        var result = await new FileOperationRecoveryContentVerifier(reader)
            .VerifyAsync(CreateInspection(inspectionItem));

        Assert.AreEqual(FileOperationRecoveryContentStatus.DifferentMainStream, result.Items[0].Status);
        Assert.AreEqual(different, result.Items[0].CurrentContentFingerprint);
        Assert.IsFalse(result.Items[0].MatchesRecordedMainStream);
        Assert.AreEqual(0, result.MatchingMainStreamCount);
    }

    [TestMethod]
    public async Task MissingFingerprintSkipsRootBoundReader()
    {
        var identity = new FileIdentity(9, 90);
        var inspectionItem = CreateInspectionItem(
            FileOperationRecoveryDestinationStatus.SameObject,
            identity,
            fingerprint: null);
        var reader = new FakeReader(_ => throw new InvalidOperationException("reader should not run"));

        var result = await new FileOperationRecoveryContentVerifier(reader)
            .VerifyAsync(CreateInspection(inspectionItem));

        Assert.AreEqual(FileOperationRecoveryContentStatus.NoRecordedFingerprint, result.Items[0].Status);
        Assert.AreEqual(0, reader.Calls.Count);
    }

    [TestMethod]
    public async Task UnverifiedDestinationRootSkipsRootBoundReader()
    {
        var identity = new FileIdentity(10, 100);
        var inspectionItem = CreateInspectionItem(
            FileOperationRecoveryDestinationStatus.SameObject,
            identity,
            RecordedFingerprint);
        var reader = new FakeReader(_ => throw new InvalidOperationException("reader should not run"));

        var result = await new FileOperationRecoveryContentVerifier(reader)
            .VerifyAsync(CreateInspection(inspectionItem, rootVerified: false));

        Assert.AreEqual(
            FileOperationRecoveryContentStatus.DestinationRootNotVerified,
            result.Items[0].Status);
        Assert.AreEqual(0, reader.Calls.Count);
    }

    [TestMethod]
    public async Task InconsistentRecordedRootPathSkipsRootBoundReader()
    {
        var identity = new FileIdentity(10, 101);
        var inspectionItem = CreateInspectionItem(
            FileOperationRecoveryDestinationStatus.SameObject,
            identity,
            RecordedFingerprint);
        var reader = new FakeReader(_ => throw new InvalidOperationException("reader should not run"));

        var result = await new FileOperationRecoveryContentVerifier(reader)
            .VerifyAsync(CreateInspection(
                inspectionItem,
                recordedRootPath: @"D:\Different\Destination"));

        Assert.AreEqual(
            FileOperationRecoveryContentStatus.DestinationRootNotVerified,
            result.Items[0].Status);
        Assert.AreEqual(0, reader.Calls.Count);
    }

    [TestMethod]
    public async Task NonSameObjectInspectionSkipsRootBoundReader()
    {
        var identity = new FileIdentity(11, 110);
        var inspectionItem = CreateInspectionItem(
            FileOperationRecoveryDestinationStatus.DifferentObject,
            identity,
            RecordedFingerprint);
        var reader = new FakeReader(_ => throw new InvalidOperationException("reader should not run"));

        var result = await new FileOperationRecoveryContentVerifier(reader)
            .VerifyAsync(CreateInspection(inspectionItem));

        Assert.AreEqual(FileOperationRecoveryContentStatus.NotSameRecordedObject, result.Items[0].Status);
        Assert.AreEqual(0, reader.Calls.Count);
    }

    [TestMethod]
    public async Task RootBoundReaderUnsafeStatusesPropagateConservatively()
    {
        var statuses = new[]
        {
            (FileContentFingerprintReadStatus.DestinationRootChanged, FileOperationRecoveryContentStatus.DestinationRootChanged),
            (FileContentFingerprintReadStatus.Missing, FileOperationRecoveryContentStatus.Missing),
            (FileContentFingerprintReadStatus.DifferentObject, FileOperationRecoveryContentStatus.DifferentObject),
            (FileContentFingerprintReadStatus.Redirected, FileOperationRecoveryContentStatus.Redirected),
            (FileContentFingerprintReadStatus.ReparsePoint, FileOperationRecoveryContentStatus.ReparsePoint),
            (FileContentFingerprintReadStatus.UnexpectedType, FileOperationRecoveryContentStatus.UnexpectedType),
            (FileContentFingerprintReadStatus.Busy, FileOperationRecoveryContentStatus.Busy),
            (FileContentFingerprintReadStatus.Inaccessible, FileOperationRecoveryContentStatus.Inaccessible),
            (FileContentFingerprintReadStatus.Error, FileOperationRecoveryContentStatus.Error),
        };

        foreach (var pair in statuses)
        {
            var identity = new FileIdentity(12, (ulong)(120 + (int)pair.Item1));
            var inspectionItem = CreateInspectionItem(
                FileOperationRecoveryDestinationStatus.SameObject,
                identity,
                RecordedFingerprint);
            var reader = new FakeReader(call => new FileContentFingerprintReadResult(
                pair.Item1,
                ExistingFile(call.Request.CanonicalDestinationPath, call.Request.DestinationIdentity),
                ContentFingerprint: null,
                "fixture",
                ExistingDirectory(
                    call.Request.CanonicalDestinationDirectoryPath,
                    call.Request.DestinationDirectoryIdentity)));

            var result = await new FileOperationRecoveryContentVerifier(reader)
                .VerifyAsync(CreateInspection(inspectionItem));

            Assert.AreEqual(pair.Item2, result.Items[0].Status, pair.Item1.ToString());
            Assert.IsNull(result.Items[0].CurrentContentFingerprint);
            Assert.IsFalse(result.Items[0].MatchesRecordedMainStream);
        }
    }

    [TestMethod]
    public async Task InconsistentLeafSuccessEvidenceFailsClosedAsError()
    {
        var identity = new FileIdentity(13, 130);
        var inspectionItem = CreateInspectionItem(
            FileOperationRecoveryDestinationStatus.SameObject,
            identity,
            RecordedFingerprint);
        var wrongIdentity = new FileIdentity(13, 131);
        var reader = new FakeReader(call => new FileContentFingerprintReadResult(
            FileContentFingerprintReadStatus.Success,
            ExistingFile(call.Request.CanonicalDestinationPath, wrongIdentity),
            RecordedFingerprint,
            "inconsistent leaf success",
            ExistingDirectory(
                call.Request.CanonicalDestinationDirectoryPath,
                call.Request.DestinationDirectoryIdentity)));

        var result = await new FileOperationRecoveryContentVerifier(reader)
            .VerifyAsync(CreateInspection(inspectionItem));

        Assert.AreEqual(FileOperationRecoveryContentStatus.Error, result.Items[0].Status);
        Assert.IsNull(result.Items[0].CurrentContentFingerprint);
        Assert.IsFalse(result.Items[0].MatchesRecordedMainStream);
    }

    [TestMethod]
    public async Task InconsistentRootSuccessEvidenceFailsClosedAsError()
    {
        var identity = new FileIdentity(14, 140);
        var inspectionItem = CreateInspectionItem(
            FileOperationRecoveryDestinationStatus.SameObject,
            identity,
            RecordedFingerprint);
        var reader = new FakeReader(call => new FileContentFingerprintReadResult(
            FileContentFingerprintReadStatus.Success,
            ExistingFile(call.Request.CanonicalDestinationPath, call.Request.DestinationIdentity),
            RecordedFingerprint,
            "inconsistent root success",
            ExistingDirectory(
                call.Request.CanonicalDestinationDirectoryPath,
                new FileIdentity(99, 1000))));

        var result = await new FileOperationRecoveryContentVerifier(reader)
            .VerifyAsync(CreateInspection(inspectionItem));

        Assert.AreEqual(FileOperationRecoveryContentStatus.Error, result.Items[0].Status);
        Assert.IsNull(result.Items[0].CurrentContentFingerprint);
        Assert.IsFalse(result.Items[0].MatchesRecordedMainStream);
    }

    [TestMethod]
    public async Task ReaderExceptionFailsClosedAsError()
    {
        var identity = new FileIdentity(15, 150);
        var inspectionItem = CreateInspectionItem(
            FileOperationRecoveryDestinationStatus.SameObject,
            identity,
            RecordedFingerprint);
        var reader = new FakeReader(_ => throw new InvalidOperationException("reader failed"));

        var result = await new FileOperationRecoveryContentVerifier(reader)
            .VerifyAsync(CreateInspection(inspectionItem));

        Assert.AreEqual(FileOperationRecoveryContentStatus.Error, result.Items[0].Status);
        StringAssert.Contains(result.Items[0].Message, "reader failed");
    }

    [TestMethod]
    public void LegacyReadResultFourFieldConstructionAndDeconstructionRemainAvailable()
    {
        var destination = ExistingFile(@"D:\Legacy\payload.bin", new FileIdentity(17, 170));
        var fingerprint = new FileContentFingerprint(
            FileContentFingerprintAlgorithm.Sha256,
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc");
        var result = new FileContentFingerprintReadResult(
            FileContentFingerprintReadStatus.Success,
            destination,
            fingerprint,
            "legacy");

        var (status, current, currentFingerprint, message) = result;

        Assert.AreEqual(FileContentFingerprintReadStatus.Success, status);
        Assert.AreSame(destination, current);
        Assert.AreEqual(fingerprint, currentFingerprint);
        Assert.AreEqual("legacy", message);
        Assert.IsNull(result.CurrentDestinationDirectory);
        Assert.AreEqual(8, (int)FileContentFingerprintReadStatus.Error);
        Assert.AreEqual(9, (int)FileContentFingerprintReadStatus.DestinationRootChanged);
        Assert.AreEqual(12, (int)FileOperationRecoveryContentStatus.Error);
        Assert.AreEqual(13, (int)FileOperationRecoveryContentStatus.DestinationRootChanged);
    }

    [TestMethod]
    public void VerificationDefensivelySnapshotsItems()
    {
        var identity = new FileIdentity(16, 160);
        var inspectionItem = CreateInspectionItem(
            FileOperationRecoveryDestinationStatus.SameObject,
            identity,
            RecordedFingerprint);
        var verificationItem = new FileOperationRecoveryContentVerificationItem(
            inspectionItem.Ordinal,
            inspectionItem,
            FileOperationRecoveryContentStatus.MatchesRecordedMainStream,
            RecordedFingerprint,
            "match");
        var mutable = new List<FileOperationRecoveryContentVerificationItem> { verificationItem };
        var verification = new FileOperationRecoveryContentVerification(Guid.NewGuid(), mutable);

        mutable.Clear();

        Assert.AreEqual(1, verification.Items.Count);
        Assert.AreSame(verificationItem, verification.Items[0]);
    }

    private static FileOperationRecoveryInspection CreateInspection(
        FileOperationRecoveryInspectionItem item,
        bool rootVerified = true,
        string recordedRootPath = RootPath)
    {
        if (!rootVerified)
        {
            return new FileOperationRecoveryInspection(Guid.NewGuid(), new[] { item });
        }

        var root = new FileOperationRecoveryRootInspection(
            RootIdentity,
            FileOperationRecoveryRootStatus.SameObject,
            ExistingDirectory(RootPath, RootIdentity),
            "same root",
            recordedRootPath);
        return new FileOperationRecoveryInspection(Guid.NewGuid(), new[] { item }, root);
    }

    private static FileOperationRecoveryInspectionItem CreateInspectionItem(
        FileOperationRecoveryDestinationStatus status,
        FileIdentity identity,
        FileContentFingerprint? fingerprint)
    {
        var path = RootPath + @"\payload.bin";
        var entry = new FileOperationActionEntry(
            0,
            new FileOperationEntry(@"C:\Source\payload.bin", "payload.bin", IsDirectory: false),
            @"C:\Real\Source\payload.bin",
            path,
            FileOperationActionEntryState.RecoveryRequired,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            new FileIdentity(1, 10),
            identity,
            FileOperationUndoKind.None,
            new FileOperationFailure(
                "RecoveryRequired",
                "fixture",
                path,
                Retryable: false),
            fingerprint);
        var current = status == FileOperationRecoveryDestinationStatus.SameObject
            ? ExistingFile(path, identity)
            : ExistingFile(path, new FileIdentity(identity.VolumeSerialNumber, identity.FileReferenceNumber + 1));
        return new FileOperationRecoveryInspectionItem(0, entry, status, current, "fixture");
    }

    private static FileContentFingerprintReadResult Success(
        FileContentFingerprintReadRequest request,
        FileContentFingerprint fingerprint) =>
        new(
            FileContentFingerprintReadStatus.Success,
            ExistingFile(request.CanonicalDestinationPath, request.DestinationIdentity),
            fingerprint,
            "success",
            ExistingDirectory(
                request.CanonicalDestinationDirectoryPath,
                request.DestinationDirectoryIdentity));

    private static FileOperationCanonicalPath ExistingFile(string path, FileIdentity identity) =>
        new(
            path,
            path,
            FileOperationCanonicalPathState.File,
            IsLeafReparsePoint: false,
            Identity: identity);

    private static FileOperationCanonicalPath ExistingDirectory(string path, FileIdentity identity) =>
        new(
            path,
            path,
            FileOperationCanonicalPathState.Directory,
            IsLeafReparsePoint: false,
            Identity: identity);

    private sealed class FakeReader : IRootBoundFileContentFingerprintReader
    {
        private readonly Func<Call, FileContentFingerprintReadResult> _callback;

        public FakeReader(Func<Call, FileContentFingerprintReadResult> callback) =>
            _callback = callback;

        public List<Call> Calls { get; } = new();

        public ValueTask<FileContentFingerprintReadResult> ReadAsync(
            FileContentFingerprintReadRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = new Call(request);
            Calls.Add(call);
            return ValueTask.FromResult(_callback(call));
        }

        public sealed record Call(FileContentFingerprintReadRequest Request);
    }
}
