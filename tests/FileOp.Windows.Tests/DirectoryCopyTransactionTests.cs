using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class DirectoryCopyTransactionTests
{
    private const uint SourceVolume = 51;
    private const uint DestinationVolume = 91;

    [TestMethod]
    public async Task SqliteHistoryPersistsParentLinkedRecursiveActions()
    {
        var database = TempDatabasePath();
        try
        {
            var plan = CreatePlan();
            var fresh = ReadyGate(plan.ReviewedManifest);
            using (var store = new SqliteDirectoryCopyActionHistoryStore(database))
            {
                var history = await store.BeginAsync(plan, fresh, DateTimeOffset.UtcNow);
                Assert.AreEqual(4, history.Entries.Count);
                Assert.AreEqual(DirectoryCopyActionKind.CreateDirectory, history.Entries[0].Kind);
                Assert.AreEqual(string.Empty, history.Entries[0].RelativePath);
                Assert.IsNull(history.Entries[0].DestinationParentOrdinal);
                Assert.AreEqual(0, history.Entries[1].DestinationParentOrdinal);
                Assert.AreEqual(0, history.Entries[2].DestinationParentOrdinal);
                Assert.AreEqual(1, history.Entries[3].DestinationParentOrdinal);

                await store.MarkMutationStartedAsync(plan.OperationId, 0, DateTimeOffset.UtcNow);
                await store.CommitAsync(
                    plan.OperationId,
                    0,
                    new FileIdentity(DestinationVolume, 100),
                    destinationContentFingerprint: null,
                    committedAtUtc: DateTimeOffset.UtcNow);
            }

            using var reopened = new SqliteDirectoryCopyActionHistoryStore(database);
            var persisted = await reopened.GetAsync(plan.OperationId);
            Assert.IsNotNull(persisted);
            Assert.AreEqual(DirectoryCopyActionEntryState.Committed, persisted.Entries[0].State);
            Assert.AreEqual(new FileIdentity(DestinationVolume, 100), persisted.Entries[0].DestinationIdentity);
            Assert.IsNull(persisted.Entries[0].DestinationContentFingerprint);
            Assert.IsFalse(persisted.GrantsAutomaticReplayAuthority);
            Assert.IsFalse(persisted.GrantsRollbackAuthority);
            Assert.IsFalse(persisted.GrantsDeleteAuthority);
        }
        finally
        {
            DeleteNoThrow(database);
        }
    }

    [TestMethod]
    public async Task ExecutorPersistsMutationBarrierAndChainsCommittedParentIdentity()
    {
        var database = TempDatabasePath();
        try
        {
            var plan = CreatePlan();
            using var store = new SqliteDirectoryCopyActionHistoryStore(database);
            var acquirer = new FakeAcquirer(Ready(plan.ReviewedManifest));
            var expectedParents = new Queue<FileIdentity>(new[]
            {
                plan.DestinationParent.Identity!.Value,
                new FileIdentity(DestinationVolume, 100),
                new FileIdentity(DestinationVolume, 100),
                new FileIdentity(DestinationVolume, 101),
            });
            var nextDestinationReference = 100UL;
            var primitive = new FakePrimitive(async request =>
            {
                var durable = await store.GetAsync(plan.OperationId);
                Assert.IsNotNull(durable);
                Assert.AreEqual(
                    DirectoryCopyActionEntryState.MutationStarted,
                    durable.Entries[request.Action.Ordinal].State,
                    "MutationStarted must be durable before provider invocation.");
                Assert.AreEqual(expectedParents.Dequeue(), request.DestinationParentIdentity);
                var destinationIdentity = new FileIdentity(DestinationVolume, nextDestinationReference++);
                return new FakeLease(new DirectoryCopyMutationReceipt(
                    request.Action.Ordinal,
                    request.Action.Kind,
                    request.Action.CanonicalSourcePath,
                    request.Action.SourceIdentity,
                    request.Action.CanonicalDestinationPath,
                    destinationIdentity,
                    request.Action.Kind == DirectoryCopyActionKind.CopyFile ? Fingerprint() : null));
            });
            var executor = new DirectoryCopyTransactionExecutor(
                new DirectoryCopyFreshManifestGate(acquirer),
                store,
                primitive);

            var result = await executor.ExecuteAsync(plan);

            Assert.AreEqual(DirectoryCopyActionTerminalState.Succeeded, result.TerminalState);
            Assert.IsTrue(result.Entries.All(static entry => entry.State == DirectoryCopyActionEntryState.Committed));
            Assert.IsTrue(result.Entries
                .Where(static entry => entry.Kind == DirectoryCopyActionKind.CopyFile)
                .All(static entry => entry.DestinationContentFingerprint is not null));
            Assert.AreEqual(4, primitive.CallCount);
            Assert.AreEqual(1, acquirer.CallCount);
            Assert.AreEqual(0, expectedParents.Count);
            Assert.IsFalse(result.RequiresRecovery);
        }
        finally
        {
            DeleteNoThrow(database);
        }
    }

    [TestMethod]
    public async Task ChangedFreshManifestBlocksBeforeDurableHistoryAndMutation()
    {
        var database = TempDatabasePath();
        try
        {
            var plan = CreatePlan();
            var changed = CreateManifest(rootReference: 1, folderReference: 2, rootFileReference: 30, childReference: 4);
            using var store = new SqliteDirectoryCopyActionHistoryStore(database);
            var primitive = new FakePrimitive(_ =>
                throw new AssertFailedException("Mutation provider must not be called."));
            var executor = new DirectoryCopyTransactionExecutor(
                new DirectoryCopyFreshManifestGate(new FakeAcquirer(Ready(changed))),
                store,
                primitive);

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await executor.ExecuteAsync(plan));

            Assert.IsNull(await store.GetAsync(plan.OperationId));
            Assert.AreEqual(0, primitive.CallCount);
        }
        finally
        {
            DeleteNoThrow(database);
        }
    }

    [TestMethod]
    public async Task ProviderFailureAfterMutationStartedSettlesRecoveryRequired()
    {
        var database = TempDatabasePath();
        try
        {
            var plan = CreatePlan();
            using var store = new SqliteDirectoryCopyActionHistoryStore(database);
            var primitive = new FakePrimitive(_ =>
                throw new IOException("simulated provider ambiguity"));
            var executor = new DirectoryCopyTransactionExecutor(
                new DirectoryCopyFreshManifestGate(new FakeAcquirer(Ready(plan.ReviewedManifest))),
                store,
                primitive);

            var result = await executor.ExecuteAsync(plan);

            Assert.AreEqual(DirectoryCopyActionTerminalState.RecoveryRequired, result.TerminalState);
            Assert.IsTrue(result.RequiresRecovery);
            Assert.AreEqual(DirectoryCopyActionEntryState.RecoveryRequired, result.Entries[0].State);
            Assert.AreEqual("DirectoryCopyMutationAmbiguous", result.Entries[0].Failure?.Code);
            Assert.AreEqual(1, primitive.CallCount);
        }
        finally
        {
            DeleteNoThrow(database);
        }
    }

    [TestMethod]
    public async Task CancellationDuringMutationCommitsCurrentActionThenStopsAtBoundary()
    {
        var database = TempDatabasePath();
        try
        {
            var plan = CreatePlan();
            using var store = new SqliteDirectoryCopyActionHistoryStore(database);
            using var cancellation = new CancellationTokenSource();
            var primitive = new FakePrimitive(request =>
            {
                cancellation.Cancel();
                return ValueTask.FromResult<IDirectoryCopyMutationLease>(new FakeLease(
                    new DirectoryCopyMutationReceipt(
                        request.Action.Ordinal,
                        request.Action.Kind,
                        request.Action.CanonicalSourcePath,
                        request.Action.SourceIdentity,
                        request.Action.CanonicalDestinationPath,
                        new FileIdentity(DestinationVolume, 100),
                        request.Action.Kind == DirectoryCopyActionKind.CopyFile ? Fingerprint() : null)));
            });
            var executor = new DirectoryCopyTransactionExecutor(
                new DirectoryCopyFreshManifestGate(new FakeAcquirer(Ready(plan.ReviewedManifest))),
                store,
                primitive);

            var result = await executor.ExecuteAsync(plan, cancellation.Token);

            Assert.AreEqual(DirectoryCopyActionTerminalState.Cancelled, result.TerminalState);
            Assert.AreEqual(DirectoryCopyActionEntryState.Committed, result.Entries[0].State);
            Assert.IsTrue(result.Entries.Skip(1).All(static entry => entry.State == DirectoryCopyActionEntryState.Pending));
            Assert.AreEqual(1, primitive.CallCount);
            Assert.IsFalse(result.RequiresRecovery);
        }
        finally
        {
            DeleteNoThrow(database);
        }
    }

    [TestMethod]
    public async Task FileReceiptWithoutFingerprintCannotCommitAndRequiresRecovery()
    {
        var database = TempDatabasePath();
        try
        {
            var plan = CreatePlan(singleRootFile: true);
            using var store = new SqliteDirectoryCopyActionHistoryStore(database);
            var next = 100UL;
            var primitive = new FakePrimitive(request =>
                ValueTask.FromResult<IDirectoryCopyMutationLease>(new FakeLease(
                    new DirectoryCopyMutationReceipt(
                        request.Action.Ordinal,
                        request.Action.Kind,
                        request.Action.CanonicalSourcePath,
                        request.Action.SourceIdentity,
                        request.Action.CanonicalDestinationPath,
                        new FileIdentity(DestinationVolume, next++),
                        DestinationContentFingerprint: null))));
            var executor = new DirectoryCopyTransactionExecutor(
                new DirectoryCopyFreshManifestGate(new FakeAcquirer(Ready(plan.ReviewedManifest))),
                store,
                primitive);

            var result = await executor.ExecuteAsync(plan);

            Assert.AreEqual(DirectoryCopyActionTerminalState.RecoveryRequired, result.TerminalState);
            Assert.AreEqual(DirectoryCopyActionEntryState.Committed, result.Entries[0].State);
            Assert.AreEqual(DirectoryCopyActionEntryState.RecoveryRequired, result.Entries[1].State);
            StringAssert.Contains(result.Entries[1].Failure?.Message, "SHA-256");
        }
        finally
        {
            DeleteNoThrow(database);
        }
    }

    [TestMethod]
    public void TransactionPlanRejectsDestinationInsideReviewedSourceTree()
    {
        var manifest = CreateManifest(1, 2, 3, 4);
        var inside = Path.Combine(manifest.CanonicalRootPath, "nested-destination");
        var destinationParent = new FileOperationCanonicalPath(
            inside,
            inside,
            FileOperationCanonicalPathState.Directory,
            false,
            new FileIdentity(SourceVolume, 99));

        Assert.Throws<ArgumentException>(() =>
            new DirectoryCopyTransactionPlan(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                manifest,
                destinationParent,
                Path.Combine(inside, "copy")));
    }

    private static DirectoryCopyTransactionPlan CreatePlan(bool singleRootFile = false)
    {
        var manifest = singleRootFile
            ? CreateSingleFileManifest()
            : CreateManifest(1, 2, 3, 4);
        var destinationParentPath = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(), "FileOp.DirectoryCopyTransaction.Tests", "destination"));
        var destinationParent = new FileOperationCanonicalPath(
            destinationParentPath,
            destinationParentPath,
            FileOperationCanonicalPathState.Directory,
            IsLeafReparsePoint: false,
            Identity: new FileIdentity(DestinationVolume, 50));
        return new DirectoryCopyTransactionPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            manifest,
            destinationParent,
            Path.Combine(destinationParentPath, "copied-root"));
    }

    private static DirectoryOperationTreeManifest CreateSingleFileManifest()
    {
        var root = SourceRoot();
        return DirectoryOperationTreeManifest.Create(
            PlainFidelity(root),
            RootEvidence(root, new FileIdentity(SourceVolume, 1)),
            new[] { Entry(root, "file.txt", 2, DirectoryOperationTreeEntryKind.File) });
    }

    private static DirectoryOperationTreeManifest CreateManifest(
        ulong rootReference,
        ulong folderReference,
        ulong rootFileReference,
        ulong childReference)
    {
        var root = SourceRoot();
        return DirectoryOperationTreeManifest.Create(
            PlainFidelity(root),
            RootEvidence(root, new FileIdentity(SourceVolume, rootReference)),
            new[]
            {
                Entry(root, "folder", folderReference, DirectoryOperationTreeEntryKind.Directory),
                Entry(root, "root.txt", rootFileReference, DirectoryOperationTreeEntryKind.File),
                Entry(root, Path.Combine("folder", "child.txt"), childReference, DirectoryOperationTreeEntryKind.File),
            });
    }

    private static DirectoryOperationTreeEntryEvidence Entry(
        string root,
        string relativePath,
        ulong reference,
        DirectoryOperationTreeEntryKind kind)
    {
        var path = Path.Combine(root, relativePath);
        return new DirectoryOperationTreeEntryEvidence(
            relativePath,
            new FileOperationCanonicalPath(
                path,
                path,
                kind == DirectoryOperationTreeEntryKind.Directory
                    ? FileOperationCanonicalPathState.Directory
                    : FileOperationCanonicalPathState.File,
                IsLeafReparsePoint: false,
                Identity: new FileIdentity(SourceVolume, reference)));
    }

    private static DirectoryOperationTreeManifestAcquisitionResult Ready(
        DirectoryOperationTreeManifest manifest) =>
        DirectoryOperationTreeManifestAcquisitionResult.Ready(
            manifest,
            PlainFidelity(manifest.CanonicalRootPath),
            "fresh complete plain-tree evidence");

    private static DirectoryCopyFreshManifestGateResult ReadyGate(
        DirectoryOperationTreeManifest manifest)
    {
        var acquisition = Ready(manifest);
        var revalidation = DirectoryOperationTreeManifestRevalidator.Compare(manifest, manifest);
        return new DirectoryCopyFreshManifestGateResult(
            DirectoryCopyFreshManifestGateStatus.ReadyForDurableHistory,
            manifest,
            acquisition,
            revalidation,
            "ready");
    }

    private static DirectoryOperationFidelityEvidence PlainFidelity(string root) =>
        new(root, DirectoryOperationFidelityFeature.None, true, true);

    private static FileOperationCanonicalPath RootEvidence(string root, FileIdentity identity) =>
        new(root, root, FileOperationCanonicalPathState.Directory, false, identity);

    private static string SourceRoot() => Path.GetFullPath(Path.Combine(
        Path.GetTempPath(), "FileOp.DirectoryCopyTransaction.Tests", "source"));

    private static FileContentFingerprint Fingerprint() =>
        new(FileContentFingerprintAlgorithm.Sha256, new string('a', 64));

    private static string TempDatabasePath() => Path.Combine(
        Path.GetTempPath(), "FileOp.DirectoryCopyTransaction.Tests", Guid.NewGuid().ToString("N") + ".sqlite");

    private static void DeleteNoThrow(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        try { if (File.Exists(path + "-wal")) File.Delete(path + "-wal"); } catch { }
        try { if (File.Exists(path + "-shm")) File.Delete(path + "-shm"); } catch { }
    }

    private sealed class FakeAcquirer : IDirectoryOperationTreeManifestAcquirer
    {
        private readonly DirectoryOperationTreeManifestAcquisitionResult _result;
        public FakeAcquirer(DirectoryOperationTreeManifestAcquisitionResult result) => _result = result;
        public int CallCount { get; private set; }
        public ValueTask<DirectoryOperationTreeManifestAcquisitionResult> AcquireFreshAsync(
            DirectoryOperationTreeManifest reviewedManifest,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(_result);
        }
    }

    private sealed class FakePrimitive : IDirectoryCopyMutationPrimitive
    {
        private readonly Func<DirectoryCopyMutationRequest, ValueTask<IDirectoryCopyMutationLease>> _callback;
        public FakePrimitive(Func<DirectoryCopyMutationRequest, ValueTask<IDirectoryCopyMutationLease>> callback) => _callback = callback;
        public int CallCount { get; private set; }
        public ValueTask<IDirectoryCopyMutationLease> ExecuteNoReplaceAsync(DirectoryCopyMutationRequest request)
        {
            CallCount++;
            return _callback(request);
        }
    }

    private sealed class FakeLease : IDirectoryCopyMutationLease
    {
        public FakeLease(DirectoryCopyMutationReceipt receipt) => Receipt = receipt;
        public DirectoryCopyMutationReceipt Receipt { get; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
