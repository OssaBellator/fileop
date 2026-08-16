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
public sealed class FileCrossVolumeMoveFidelityLeaseTests
{
    [TestMethod]
    public async Task PreBarrierFidelityRefusalRetainsSourceWithoutDeleteBarrier()
    {
        using var fixture = new HistoryFixture();
        var plan = CreatePlan();
        using var history = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.DatabasePath);
        var inner = new FakeInnerDeletePrimitive();
        var verifier = new FakeFidelityVerifier(Blocked(FileCrossVolumeMoveFidelityBlocker.SourceContentChanged));
        var sourceDelete = new WindowsFidelityVerifiedFileCrossVolumeMoveSourceDeletePrimitive(
            inner,
            verifier);
        var executor = CreateExecutor(plan, history, sourceDelete);

        var result = await executor.ExecuteAsync(plan);

        Assert.AreEqual(FileOperationExecutionState.Failed, result.State);
        Assert.AreEqual("CrossVolumeMoveSourceDeletePreparationFailed", result.Failure?.Code);
        Assert.AreEqual(1, verifier.CallCount);
        Assert.AreEqual(1, inner.AcquireCount);
        Assert.AreEqual(0, inner.MutationCount);
        Assert.AreEqual(0, inner.CloseCount);
        Assert.AreEqual(1, inner.DisposeCount);

        var persisted = await history.GetAsync(plan.Id);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(FileCrossVolumeMoveTerminalState.Failed, persisted.TerminalState);
        Assert.AreEqual(FileCrossVolumeMoveEntryState.Failed, persisted.Entries[0].State);
        Assert.IsTrue(persisted.HasRetainedSourceDuplicates);
        Assert.IsFalse(persisted.RequiresRecovery);
    }

    [TestMethod]
    public async Task PostBarrierFidelityRefusalRequiresRecoveryWithoutInnerDeleteMutation()
    {
        using var fixture = new HistoryFixture();
        var plan = CreatePlan();
        using var history = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.DatabasePath);
        var inner = new FakeInnerDeletePrimitive();
        var verifier = new FakeFidelityVerifier(
            Allowed(),
            Blocked(FileCrossVolumeMoveFidelityBlocker.SourceExtendedAttributes));
        var sourceDelete = new WindowsFidelityVerifiedFileCrossVolumeMoveSourceDeletePrimitive(
            inner,
            verifier);
        var executor = CreateExecutor(plan, history, sourceDelete);

        var result = await executor.ExecuteAsync(plan);

        Assert.AreEqual(FileOperationExecutionState.Failed, result.State);
        Assert.AreEqual("CrossVolumeMoveSourceDeleteFailed", result.Failure?.Code);
        Assert.AreEqual(2, verifier.CallCount);
        Assert.AreEqual(1, inner.AcquireCount);
        Assert.AreEqual(0, inner.MutationCount);
        Assert.AreEqual(0, inner.CloseCount);
        Assert.AreEqual(1, inner.DisposeCount);

        var persisted = await history.GetAsync(plan.Id);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(FileCrossVolumeMoveTerminalState.RecoveryRequired, persisted.TerminalState);
        Assert.AreEqual(FileCrossVolumeMoveEntryState.RecoveryRequired, persisted.Entries[0].State);
        Assert.IsTrue(persisted.RequiresRecovery);
    }

    [TestMethod]
    public async Task TwoPositiveFidelityProofsPermitExactlyOneInnerDeleteMutationAndCheckedClose()
    {
        using var fixture = new HistoryFixture();
        var plan = CreatePlan();
        using var history = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.DatabasePath);
        var inner = new FakeInnerDeletePrimitive();
        var verifier = new FakeFidelityVerifier(Allowed(), Allowed());
        var sourceDelete = new WindowsFidelityVerifiedFileCrossVolumeMoveSourceDeletePrimitive(
            inner,
            verifier);
        var executor = CreateExecutor(plan, history, sourceDelete);

        var result = await executor.ExecuteAsync(plan);

        Assert.AreEqual(FileOperationExecutionState.Succeeded, result.State);
        Assert.AreEqual(2, verifier.CallCount);
        Assert.AreEqual(1, inner.AcquireCount);
        Assert.AreEqual(1, inner.MutationCount);
        Assert.AreEqual(1, inner.CloseCount);
        Assert.AreEqual(1, inner.DisposeCount);

        var persisted = await history.GetAsync(plan.Id);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(FileCrossVolumeMoveTerminalState.Succeeded, persisted.TerminalState);
        Assert.AreEqual(FileCrossVolumeMoveEntryState.Moved, persisted.Entries[0].State);
        Assert.IsFalse(persisted.RequiresRecovery);
    }

    private static FileCrossVolumeMoveOperationExecutor CreateExecutor(
        FileOperationPlan plan,
        SqliteFileCrossVolumeMoveActionHistoryStore history,
        IFileCrossVolumeMoveSourceDeletePrimitive sourceDelete) =>
        new(
            new FakeValidator(candidate => CreateValidation(candidate)),
            history,
            new FakeCopyMutation(),
            sourceDelete);

    private static FileCrossVolumeMoveFidelityClassification Allowed() =>
        new(
            CanDeleteSourceAfterDurableBarrier: true,
            Array.Empty<FileCrossVolumeMoveFidelityBlocker>(),
            "allowed");

    private static FileCrossVolumeMoveFidelityClassification Blocked(
        FileCrossVolumeMoveFidelityBlocker blocker) =>
        new(
            CanDeleteSourceAfterDurableBarrier: false,
            new[] { blocker },
            "blocked");

    private static FileOperationPlan CreatePlan()
    {
        var sourceDirectory = Path.GetFullPath(@"C:\Source");
        var destinationDirectory = Path.GetFullPath(@"D:\Destination");
        var entry = new FileOperationEntry(
            Path.Combine(sourceDirectory, "a.txt"),
            "a.txt",
            IsDirectory: false);
        return new FileOperationPlan(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero),
            FileOperationKind.Move,
            FileOperationCollisionPolicy.Stop,
            new FileOperationIntent(
                "Left",
                Guid.NewGuid(),
                sourceDirectory,
                new[] { entry },
                "Right",
                Guid.NewGuid(),
                destinationDirectory));
    }

    private static FileOperationExecutionValidationResult CreateValidation(FileOperationPlan plan)
    {
        var canonicalSourceDirectory = Path.GetFullPath(@"C:\Real\Source");
        var canonicalDestinationDirectory = Path.GetFullPath(@"D:\Real\Destination");
        var entry = plan.Intent.Entries[0];
        return new FileOperationExecutionValidationResult(
            plan,
            new FileOperationCanonicalPath(
                plan.Intent.SourceDirectoryPath,
                canonicalSourceDirectory,
                FileOperationCanonicalPathState.Directory,
                IsLeafReparsePoint: false,
                Identity: new FileIdentity(1, 10)),
            new FileOperationCanonicalPath(
                plan.Intent.DestinationDirectoryPath,
                canonicalDestinationDirectory,
                FileOperationCanonicalPathState.Directory,
                IsLeafReparsePoint: false,
                Identity: new FileIdentity(2, 20)),
            new[]
            {
                new FileOperationExecutionValidationItem(
                    entry,
                    new FileOperationCanonicalPath(
                        entry.Path,
                        Path.Combine(canonicalSourceDirectory, entry.Name),
                        FileOperationCanonicalPathState.File,
                        IsLeafReparsePoint: false,
                        Identity: new FileIdentity(1, 100)),
                    new FileOperationCanonicalPath(
                        Path.Combine(plan.Intent.DestinationDirectoryPath, entry.Name),
                        Path.Combine(canonicalDestinationDirectory, entry.Name),
                        FileOperationCanonicalPathState.Missing,
                        IsLeafReparsePoint: false),
                    FileOperationExecutionValidationDecision.Ready,
                    "ready"),
            },
            FileOperationExecutionValidationStatus.Ready,
            new DateTimeOffset(2026, 8, 15, 0, 1, 0, TimeSpan.Zero),
            "ready");
    }

    private sealed class FakeValidator : IFileOperationExecutionValidator
    {
        private readonly Func<FileOperationPlan, FileOperationExecutionValidationResult> _factory;

        public FakeValidator(Func<FileOperationPlan, FileOperationExecutionValidationResult> factory) =>
            _factory = factory;

        public ValueTask<FileOperationExecutionValidationResult> ValidateAsync(
            FileOperationPlan plan,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_factory(plan));
        }
    }

    private sealed class FakeCopyMutation : IFileCopyMutationPrimitive
    {
        public ValueTask<IFileCopyMutationLease> CopyNewFileAsync(FileCopyMutationRequest request)
        {
            var receipt = new FileCopyMutationReceipt(
                request.Item.Source.CanonicalPath,
                request.Item.Destination.CanonicalPath,
                request.Item.Source.Identity!.Value,
                new FileIdentity(2, 500),
                new FileContentFingerprint(
                    FileContentFingerprintAlgorithm.Sha256,
                    new string('a', FileContentFingerprint.Sha256HexLength)));
            return ValueTask.FromResult<IFileCopyMutationLease>(new FakeCopyLease(receipt));
        }
    }

    private sealed class FakeCopyLease : IFileCopyMutationLease
    {
        public FakeCopyLease(FileCopyMutationReceipt receipt) => Receipt = receipt;

        public FileCopyMutationReceipt Receipt { get; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeFidelityVerifier : IFileCrossVolumeMoveFidelityVerifier
    {
        private readonly Queue<FileCrossVolumeMoveFidelityClassification> _results;

        public FakeFidelityVerifier(params FileCrossVolumeMoveFidelityClassification[] results) =>
            _results = new Queue<FileCrossVolumeMoveFidelityClassification>(results);

        public int CallCount { get; private set; }

        public ValueTask<FileCrossVolumeMoveFidelityClassification> VerifyAsync(
            FileCrossVolumeMoveSourceDeleteRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            if (_results.Count == 0)
            {
                throw new InvalidOperationException("No fake fidelity result remains.");
            }
            return ValueTask.FromResult(_results.Dequeue());
        }
    }

    private sealed class FakeInnerDeletePrimitive : IFileCrossVolumeMoveSourceDeletePrimitive
    {
        public int AcquireCount { get; private set; }

        public int MutationCount { get; private set; }

        public int CloseCount { get; private set; }

        public int DisposeCount { get; private set; }

        public ValueTask<IFileCrossVolumeMoveSourceDeleteLease> AcquireAsync(
            FileCrossVolumeMoveSourceDeleteRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcquireCount++;
            return ValueTask.FromResult<IFileCrossVolumeMoveSourceDeleteLease>(
                new FakeInnerDeleteLease(
                    new FileCrossVolumeMoveSourceDeleteEvidence(request),
                    this));
        }

        private sealed class FakeInnerDeleteLease : IFileCrossVolumeMoveSourceDeleteLease
        {
            private readonly FakeInnerDeletePrimitive _owner;

            public FakeInnerDeleteLease(
                FileCrossVolumeMoveSourceDeleteEvidence evidence,
                FakeInnerDeletePrimitive owner)
            {
                Evidence = evidence;
                _owner = owner;
            }

            public FileCrossVolumeMoveSourceDeleteEvidence Evidence { get; }

            public bool DeleteAccessCapabilityHeld => true;

            public bool SourceDeleteMutationPerformed => _owner.MutationCount != 0;

            public bool SourceDeleteHandleCloseCompleted => _owner.CloseCount != 0;

            public ValueTask MarkDeletePendingAsync(
                FileCrossVolumeMoveSourceDeleteAuthorization authorization,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.IsTrue(authorization.SourceDeleteBarrierSatisfied);
                Assert.IsTrue(authorization.SourceDeleteMutationAuthorized);
                Assert.IsTrue(authorization.IsBoundTo(Evidence));
                _owner.MutationCount++;
                return ValueTask.CompletedTask;
            }

            public ValueTask CloseSourceDeleteHandleAsync(
                FileCrossVolumeMoveSourceDeleteAuthorization authorization,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.IsTrue(authorization.IsBoundTo(Evidence));
                Assert.IsTrue(SourceDeleteMutationPerformed);
                _owner.CloseCount++;
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                _owner.DisposeCount++;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class HistoryFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "FileOp.CrossVolumeMoveFidelityLease.Tests",
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
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                try
                {
                    File.Delete(DatabasePath + suffix);
                }
                catch (IOException)
                {
                }
            }
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
