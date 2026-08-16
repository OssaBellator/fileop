using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileCrossVolumeMoveMutationProofTests
{
    [TestMethod]
    public async Task FidelityWrapperRejectsInnerSuccessWithoutDispositionProof()
    {
        var request = CreateRequest();
        var inner = new NonReportingDeletePrimitive();
        var wrapper = new WindowsFidelityVerifiedFileCrossVolumeMoveSourceDeletePrimitive(
            inner,
            new AlwaysAllowedFidelityVerifier());
        await using var lease = await wrapper.AcquireAsync(request);
        var history = CreateBarrierHistory(request);
        var authorization = CreateAuthorization(lease.Evidence, history);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await lease.MarkDeletePendingAsync(authorization).AsTask());

        StringAssert.Contains(exception.Message, "without reporting");
        Assert.IsTrue(inner.MarkDeletePendingCalled);
        Assert.IsFalse(lease.SourceDeleteMutationPerformed);
        Assert.IsFalse(lease.SourceDeleteHandleCloseCompleted);
    }

    private static FileCrossVolumeMoveSourceDeleteRequest CreateRequest()
    {
        var entry = new FileOperationEntry(
            @"C:\Source\a.txt",
            "a.txt",
            IsDirectory: false);
        return new FileCrossVolumeMoveSourceDeleteRequest(
            Guid.NewGuid(),
            0,
            entry,
            @"C:\Source",
            new FileIdentity(1, 10),
            @"D:\Destination",
            new FileIdentity(2, 20),
            @"C:\Source\a.txt",
            new FileIdentity(1, 100),
            @"D:\Destination\a.txt",
            new FileIdentity(2, 200),
            Fingerprint());
    }

    private static FileCrossVolumeMoveActionHistory CreateBarrierHistory(
        FileCrossVolumeMoveSourceDeleteRequest request)
    {
        var queued = new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);
        return new FileCrossVolumeMoveActionHistory(
            operationId: request.OperationId,
            queuedAtUtc: queued,
            validatedAtUtc: queued.AddSeconds(1),
            startedAtUtc: queued.AddSeconds(2),
            completedAtUtc: null,
            collisionPolicy: FileOperationCollisionPolicy.Stop,
            sourceDirectoryPath: request.CanonicalSourceDirectoryPath,
            destinationDirectoryPath: request.CanonicalDestinationDirectoryPath,
            canonicalSourceDirectoryPath: request.CanonicalSourceDirectoryPath,
            canonicalDestinationDirectoryPath: request.CanonicalDestinationDirectoryPath,
            sourceDirectoryIdentity: request.SourceDirectoryIdentity,
            destinationDirectoryIdentity: request.DestinationDirectoryIdentity,
            terminalState: null,
            entries: new[]
            {
                new FileCrossVolumeMoveActionEntry(
                    Ordinal: request.Ordinal,
                    Entry: request.Entry,
                    CanonicalSourcePath: request.CanonicalSourcePath,
                    CanonicalDestinationPath: request.CanonicalDestinationPath,
                    State: FileCrossVolumeMoveEntryState.SourceDeleteStarted,
                    SourceIdentity: request.SourceIdentity,
                    DestinationIdentity: request.DestinationIdentity,
                    DestinationContentFingerprint: request.DestinationContentFingerprint,
                    CopyMutationStartedAtUtc: queued.AddSeconds(3),
                    DestinationCommittedAtUtc: queued.AddSeconds(4),
                    SourceDeleteStartedAtUtc: queued.AddSeconds(5),
                    CompletedAtUtc: null,
                    Failure: null),
            });
    }

    private static FileCrossVolumeMoveSourceDeleteAuthorization CreateAuthorization(
        FileCrossVolumeMoveSourceDeleteEvidence evidence,
        FileCrossVolumeMoveActionHistory history)
    {
        var constructor = typeof(FileCrossVolumeMoveSourceDeleteAuthorization)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length == 2 &&
                    parameters[0].ParameterType == typeof(FileCrossVolumeMoveSourceDeleteEvidence) &&
                    parameters[1].ParameterType == typeof(FileCrossVolumeMoveActionHistory);
            });
        return (FileCrossVolumeMoveSourceDeleteAuthorization)constructor.Invoke(
            new object[] { evidence, history });
    }

    private static FileContentFingerprint Fingerprint() =>
        new(
            FileContentFingerprintAlgorithm.Sha256,
            new string('a', FileContentFingerprint.Sha256HexLength));

    private sealed class AlwaysAllowedFidelityVerifier : IFileCrossVolumeMoveFidelityVerifier
    {
        public ValueTask<FileCrossVolumeMoveFidelityClassification> VerifyAsync(
            FileCrossVolumeMoveSourceDeleteRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new FileCrossVolumeMoveFidelityClassification(
                    CanDeleteSourceAfterDurableBarrier: true,
                    Array.Empty<FileCrossVolumeMoveFidelityBlocker>(),
                    "allowed"));
        }
    }

    private sealed class NonReportingDeletePrimitive : IFileCrossVolumeMoveSourceDeletePrimitive
    {
        public bool MarkDeletePendingCalled { get; private set; }

        public ValueTask<IFileCrossVolumeMoveSourceDeleteLease> AcquireAsync(
            FileCrossVolumeMoveSourceDeleteRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IFileCrossVolumeMoveSourceDeleteLease>(
                new NonReportingDeleteLease(
                    new FileCrossVolumeMoveSourceDeleteEvidence(request),
                    this));
        }

        private sealed class NonReportingDeleteLease : IFileCrossVolumeMoveSourceDeleteLease
        {
            private readonly NonReportingDeletePrimitive _owner;

            public NonReportingDeleteLease(
                FileCrossVolumeMoveSourceDeleteEvidence evidence,
                NonReportingDeletePrimitive owner)
            {
                Evidence = evidence;
                _owner = owner;
            }

            public FileCrossVolumeMoveSourceDeleteEvidence Evidence { get; }

            public bool DeleteAccessCapabilityHeld => true;

            public bool SourceDeleteMutationPerformed => false;

            public bool SourceDeleteHandleCloseCompleted => false;

            public ValueTask MarkDeletePendingAsync(
                FileCrossVolumeMoveSourceDeleteAuthorization authorization,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(authorization);
                cancellationToken.ThrowIfCancellationRequested();
                Assert.IsTrue(authorization.IsBoundTo(Evidence));
                _owner.MarkDeletePendingCalled = true;
                return ValueTask.CompletedTask;
            }

            public ValueTask CloseSourceDeleteHandleAsync(
                FileCrossVolumeMoveSourceDeleteAuthorization authorization,
                CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException(
                    "The non-reporting fake cannot close before disposition proof exists.");

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
