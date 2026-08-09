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
    private static readonly FileContentFingerprint RecordedFingerprint = new(
        FileContentFingerprintAlgorithm.Sha256,
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

    [TestMethod]
    public async Task MatchingMainStreamRequiresSameObjectAndRemainsEvidenceOnly()
    {
        var identity = new FileIdentity(7, 70);
        var inspectionItem = CreateInspectionItem(
            FileOperationRecoveryDestinationStatus.SameObject,
            identity,
            RecordedFingerprint);
        var reader = new FakeReader(_ => Success(inspectionItem, identity, RecordedFingerprint));
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
        Assert.AreEqual(identity, reader.Calls[0].ExpectedIdentity);
        Assert.AreEqual(inspectionItem.Entry.CanonicalDestinationPath, reader.Calls[0].Path);
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
        var reader = new FakeReader(_ => Success(inspectionItem, identity, different));

        var result = await new FileOperationRecoveryContentVerifier(reader)
            .VerifyAsync(CreateInspection(inspectionItem));

        Assert.AreEqual(FileOperationRecoveryContentStatus.DifferentMainStream, result.Items[0].Status);
        Assert.AreEqual(different, result.Items[0].CurrentContentFingerprint);
        Assert.IsFalse(result.Items[0].MatchesRecordedMainStream);
        Assert.AreEqual(0, result.MatchingMainStreamCount);
    }

    [TestMethod]
    public async Task MissingFingerprintSkipsStableReader()
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
    public async Task NonSameObjectInspectionSkipsStableReader()
    {
        var identity = new FileIdentity(10, 100);
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
    public async Task StableReaderUnsafeStatusesPropagateConservatively()
    {
        var statuses = new[]
        {
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
            var identity = new FileIdentity(11, (ulong)(110 + (int)pair.Item1));
            var inspectionItem = CreateInspectionItem(
                FileOperationRecoveryDestinationStatus.SameObject,
                identity,
                RecordedFingerprint);
            var reader = new FakeReader(_ => new FileContentFingerprintReadResult(
                pair.Item1,
                ExistingFile(inspectionItem.Entry.CanonicalDestinationPath, identity),
                ContentFingerprint: null,
                "fixture"));

            var result = await new FileOperationRecoveryContentVerifier(reader)
                .VerifyAsync(CreateInspection(inspectionItem));

            Assert.AreEqual(pair.Item2, result.Items[0].Status, pair.Item1.ToString());
            Assert.IsNull(result.Items[0].CurrentContentFingerprint);
            Assert.IsFalse(result.Items[0].MatchesRecordedMainStream);
        }
    }

    [TestMethod]
    public async Task InconsistentSuccessEvidenceFailsClosedAsError()
    {
        var identity = new FileIdentity(12, 120);
        var inspectionItem = CreateInspectionItem(
            FileOperationRecoveryDestinationStatus.SameObject,
            identity,
            RecordedFingerprint);
        var wrongIdentity = new FileIdentity(12, 121);
        var reader = new FakeReader(_ => new FileContentFingerprintReadResult(
            FileContentFingerprintReadStatus.Success,
            ExistingFile(inspectionItem.Entry.CanonicalDestinationPath, wrongIdentity),
            RecordedFingerprint,
            "inconsistent success"));

        var result = await new FileOperationRecoveryContentVerifier(reader)
            .VerifyAsync(CreateInspection(inspectionItem));

        Assert.AreEqual(FileOperationRecoveryContentStatus.Error, result.Items[0].Status);
        Assert.IsNull(result.Items[0].CurrentContentFingerprint);
        Assert.IsFalse(result.Items[0].MatchesRecordedMainStream);
    }

    [TestMethod]
    public async Task ReaderExceptionFailsClosedAsError()
    {
        var identity = new FileIdentity(13, 130);
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
    public void VerificationDefensivelySnapshotsItems()
    {
        var identity = new FileIdentity(14, 140);
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
        FileOperationRecoveryInspectionItem item) =>
        new(Guid.NewGuid(), new[] { item });

    private static FileOperationRecoveryInspectionItem CreateInspectionItem(
        FileOperationRecoveryDestinationStatus status,
        FileIdentity identity,
        FileContentFingerprint? fingerprint)
    {
        var path = @"D:\Real\Destination\payload.bin";
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
        FileOperationRecoveryInspectionItem inspection,
        FileIdentity identity,
        FileContentFingerprint fingerprint) =>
        new(
            FileContentFingerprintReadStatus.Success,
            ExistingFile(inspection.Entry.CanonicalDestinationPath, identity),
            fingerprint,
            "success");

    private static FileOperationCanonicalPath ExistingFile(string path, FileIdentity identity) =>
        new(
            path,
            path,
            FileOperationCanonicalPathState.File,
            IsLeafReparsePoint: false,
            Identity: identity);

    private sealed class FakeReader : IFileContentFingerprintReader
    {
        private readonly Func<Call, FileContentFingerprintReadResult> _callback;

        public FakeReader(Func<Call, FileContentFingerprintReadResult> callback) =>
            _callback = callback;

        public List<Call> Calls { get; } = new();

        public ValueTask<FileContentFingerprintReadResult> ReadAsync(
            string canonicalPath,
            FileIdentity expectedIdentity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = new Call(canonicalPath, expectedIdentity);
            Calls.Add(call);
            return ValueTask.FromResult(_callback(call));
        }

        public sealed record Call(string Path, FileIdentity ExpectedIdentity);
    }
}
