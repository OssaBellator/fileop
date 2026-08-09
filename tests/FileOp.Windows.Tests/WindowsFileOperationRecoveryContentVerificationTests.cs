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
        Assert.AreEqual(FileOperationRecoveryRootStatus.SameObject, inspection.DestinationDirectory.Status);
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
        Assert.AreEqual(FileOperationRecoveryRootStatus.SameObject, inspection.DestinationDirectory.Status);
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
        Assert.AreEqual(FileOperationRecoveryRootStatus.SameObject, inspection.DestinationDirectory.Status);
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

    [TestMethod]
    public async Task ReplacedRootWithSameFileMovedBackIsEvidenceInsufficient()
    {
        using var fixture = new VerificationFixture();
        var original = new byte[3072];
        new Random(43).NextBytes(original);
        await File.WriteAllBytesAsync(fixture.Path, original);
        var history = await fixture.CreateHistoryAsync(original);
        var recordedFileIdentity = history.Entries[0].DestinationIdentity;

        var originalRoot = fixture.Root + ".original";
        Directory.Move(fixture.Root, originalRoot);
        Directory.CreateDirectory(fixture.Root);
        File.Move(
            System.IO.Path.Combine(originalRoot, "payload.bin"),
            fixture.Path);
        fixture.AddCleanupRoot(originalRoot);

        var inspection = await new FileOperationRecoveryInspector(
                new WindowsFileOperationCanonicalPathResolver())
            .InspectAsync(history);

        Assert.AreEqual(
            FileOperationRecoveryRootStatus.DifferentObject,
            inspection.DestinationDirectory.Status);
        Assert.AreEqual(FileOperationRecoveryDestinationStatus.SameObject, inspection.Items[0].Status);
        Assert.AreEqual(recordedFileIdentity, inspection.Items[0].CurrentDestination.Identity);

        var result = await new FileOperationRecoveryContentVerifier(
                new WindowsFileContentFingerprintReader())
            .VerifyAsync(inspection);

        Assert.AreEqual(
            FileOperationRecoveryContentStatus.DestinationRootNotVerified,
            result.Items[0].Status);
        Assert.IsNull(result.Items[0].CurrentContentFingerprint);
    }

    private sealed class VerificationFixture : IDisposable
    {
        private readonly string _initialRoot = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "FileOp.RecoveryContent.Integration",
            Guid.NewGuid().ToString("N"));
        private string? _additionalCleanupRoot;

        public VerificationFixture()
        {
            Directory.CreateDirectory(_initialRoot);
            Root = _initialRoot;
            Path = System.IO.Path.Combine(Root, "payload.bin");
        }

        public string Root { get; }

        public string Path { get; }

        public void AddCleanupRoot(string path) => _additionalCleanupRoot = path;

        public async Task<FileOperationActionHistory> CreateHistoryAsync(byte[] copiedPayload)
        {
            var resolver = new WindowsFileOperationCanonicalPathResolver();
            var current = await resolver.ResolveAsync(Path);
            var destinationRoot = await resolver.ResolveAsync(Root);
            Assert.AreEqual(FileOperationCanonicalPathState.File, current.State);
            Assert.AreEqual(FileOperationCanonicalPathState.Directory, destinationRoot.State);
            Assert.IsNotNull(current.Identity);
            Assert.IsNotNull(destinationRoot.Identity);
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
                Root,
                @"C:\Real\Source",
                destinationRoot.CanonicalPath,
                FileOperationActionTerminalState.RecoveryRequired,
                new[] { entry },
                SourceDirectoryIdentity: new FileIdentity(1, 1),
                DestinationDirectoryIdentity: destinationRoot.Identity);
        }

        public void Dispose()
        {
            foreach (var root in new[] { Root, _additionalCleanupRoot })
            {
                if (string.IsNullOrEmpty(root))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(root, recursive: true);
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
}
