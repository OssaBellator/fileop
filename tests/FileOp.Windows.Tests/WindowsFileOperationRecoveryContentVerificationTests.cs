using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsFileOperationRecoveryContentVerificationTests
{
    [TestMethod]
    public async Task StableSameObjectAndDigestProducesMainStreamMatchEvidence()
    {
        using var fixture = new VerificationFixture();
        var payload = new byte[(1024 * 1024) + 73];
        new Random(20260810).NextBytes(payload);
        await File.WriteAllBytesAsync(fixture.Path, payload);
        var history = await fixture.CreateHistoryAsync(payload);
        var inspection = await new FileOperationRecoveryInspector(
                new WindowsFileOperationCanonicalPathResolver())
            .InspectAsync(history);
        Assert.AreEqual(FileOperationRecoveryDestinationStatus.SameObject, inspection.Items[0].Status);

        var result = await new FileOperationRecoveryContentVerifier(
                new WindowsFileContentFingerprintReader())
            .VerifyAsync(inspection);

        Assert.AreEqual(
            FileOperationRecoveryContentStatus.MatchesRecordedMainStream,
            result.Items[0].Status);
        Assert.IsTrue(result.Items[0].MatchesRecordedMainStream);
        Assert.AreEqual(FileOperationUndoKind.None, result.Items[0].Inspection.Entry.UndoKind);
        Assert.IsFalse(result.Items[0].Inspection.Entry.IsUndoCandidate);
    }

    [TestMethod]
    public async Task ContentEditAfterSameObjectInspectionIsDetectedBySecondStage()
    {
        using var fixture = new VerificationFixture();
        var original = new byte[4096];
        new Random(41).NextBytes(original);
        await File.WriteAllBytesAsync(fixture.Path, original);
        var history = await fixture.CreateHistoryAsync(original);
        var inspection = await new FileOperationRecoveryInspector(
                new WindowsFileOperationCanonicalPathResolver())
            .InspectAsync(history);
        Assert.AreEqual(FileOperationRecoveryDestinationStatus.SameObject, inspection.Items[0].Status);

        var changed = (byte[])original.Clone();
        changed[changed.Length / 2] ^= 0x5a;
        await File.WriteAllBytesAsync(fixture.Path, changed);

        var result = await new FileOperationRecoveryContentVerifier(
                new WindowsFileContentFingerprintReader())
            .VerifyAsync(inspection);

        Assert.AreEqual(FileOperationRecoveryContentStatus.DifferentMainStream, result.Items[0].Status);
        Assert.IsFalse(result.Items[0].MatchesRecordedMainStream);
    }

    [TestMethod]
    public async Task ReplacementAfterSameObjectInspectionIsDetectedBeforeHashEvidence()
    {
        using var fixture = new VerificationFixture();
        var original = new byte[2048];
        new Random(42).NextBytes(original);
        await File.WriteAllBytesAsync(fixture.Path, original);
        var history = await fixture.CreateHistoryAsync(original);
        var inspection = await new FileOperationRecoveryInspector(
                new WindowsFileOperationCanonicalPathResolver())
            .InspectAsync(history);
        Assert.AreEqual(FileOperationRecoveryDestinationStatus.SameObject, inspection.Items[0].Status);

        var originalPath = fixture.Path + ".original";
        File.Move(fixture.Path, originalPath);
        await File.WriteAllTextAsync(fixture.Path, "replacement");

        var result = await new FileOperationRecoveryContentVerifier(
                new WindowsFileContentFingerprintReader())
            .VerifyAsync(inspection);

        Assert.AreEqual(FileOperationRecoveryContentStatus.DifferentObject, result.Items[0].Status);
        Assert.IsNull(result.Items[0].CurrentContentFingerprint);
    }

    private sealed class VerificationFixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "FileOp.RecoveryContent.Integration",
            Guid.NewGuid().ToString("N"));

        public VerificationFixture()
        {
            Directory.CreateDirectory(_root);
            Path = System.IO.Path.Combine(_root, "payload.bin");
        }

        public string Path { get; }

        public async Task<FileOperationActionHistory> CreateHistoryAsync(byte[] copiedPayload)
        {
            var current = await new WindowsFileOperationCanonicalPathResolver().ResolveAsync(Path);
            Assert.AreEqual(FileOperationCanonicalPathState.File, current.State);
            Assert.IsNotNull(current.Identity);
            var fingerprint = new FileContentFingerprint(
                FileContentFingerprintAlgorithm.Sha256,
                Convert.ToHexString(SHA256.HashData(copiedPayload)));
            var now = DateTimeOffset.UtcNow;
            var entry = new FileOperationActionEntry(
                0,
                new FileOperationEntry(@"C:\Source\payload.bin", "payload.bin", IsDirectory: false),
                @"C:\Real\Source\payload.bin",
                current.CanonicalPath,
                FileOperationActionEntryState.RecoveryRequired,
                now,
                now,
                new FileIdentity(1, 10),
                current.Identity,
                FileOperationUndoKind.None,
                new FileOperationFailure(
                    "CopyCommitBarrierFailed",
                    "fixture",
                    current.CanonicalPath,
                    Retryable: false),
                fingerprint);
            return new FileOperationActionHistory(
                Guid.NewGuid(),
                now,
                now,
                now,
                now,
                FileOperationKind.Copy,
                FileOperationCollisionPolicy.Stop,
                @"C:\Source",
                _root,
                @"C:\Real\Source",
                _root,
                FileOperationActionTerminalState.RecoveryRequired,
                new[] { entry });
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
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
