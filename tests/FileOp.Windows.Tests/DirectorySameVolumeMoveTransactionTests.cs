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
public sealed class DirectorySameVolumeMoveTransactionTests
{
    [TestMethod]
    public void DirectoryStrategyRequiresReadySameVolumeDirectoryEvidence()
    {
        var plan = CreatePlan();
        var validation = CreateValidation(plan);

        var classification = DirectorySameVolumeMoveExecutionStrategyClassifier.Classify(validation);

        Assert.AreEqual(
            DirectorySameVolumeMoveExecutionStrategy.SameVolumeDirectoryRenameRequired,
            classification.Strategy);
        Assert.IsFalse(classification.GrantsMutationAuthority);
    }

    [TestMethod]
    public void DirectoryStrategyRejectsRegularFileEntry()
    {
        var plan = CreatePlan(isDirectory: false);
        var validation = CreateValidation(plan, isDirectory: false);

        var classification = DirectorySameVolumeMoveExecutionStrategyClassifier.Classify(validation);

        Assert.AreEqual(DirectorySameVolumeMoveExecutionStrategy.Blocked, classification.Strategy);
        StringAssert.Contains(classification.Summary, "directory");
    }

    [TestMethod]
    public async Task SqliteHistoryPersistsMutationBarrierAndCommitAcrossReopen()
    {
        var temp = CreateTempDirectory();
        try
        {
            var databasePath = Path.Combine(temp, "history.sqlite");
            var plan = CreatePlan();
            var validation = CreateValidation(plan);
            var now = DateTimeOffset.UtcNow;
            using (var store = new SqliteDirectorySameVolumeMoveActionHistoryStore(databasePath))
            {
                var begun = await store.BeginAsync(validation, now);
                Assert.AreEqual(DirectorySameVolumeMoveActionEntryState.Pending, begun.Entries[0].State);
                Assert.IsFalse(begun.GrantsAutomaticReplayAuthority);

                var started = await store.MarkMutationStartedAsync(plan.Id, 0, now.AddSeconds(1));
                Assert.AreEqual(DirectorySameVolumeMoveActionEntryState.MutationStarted, started.Entries[0].State);
                Assert.IsTrue(started.RequiresRecovery);

                var committed = await store.CommitAsync(
                    plan.Id,
                    0,
                    validation.Items[0].Source.Identity!.Value,
                    now.AddSeconds(2));
                Assert.AreEqual(DirectorySameVolumeMoveActionEntryState.Committed, committed.Entries[0].State);
                await store.CompleteAsync(
                    plan.Id,
                    DirectorySameVolumeMoveActionTerminalState.Succeeded,
                    now.AddSeconds(3));
            }

            using var reopened = new SqliteDirectorySameVolumeMoveActionHistoryStore(databasePath);
            var durable = await reopened.GetAsync(plan.Id);
            Assert.IsNotNull(durable);
            Assert.AreEqual(DirectorySameVolumeMoveActionTerminalState.Succeeded, durable.TerminalState);
            Assert.AreEqual(DirectorySameVolumeMoveActionEntryState.Committed, durable.Entries[0].State);
            Assert.AreEqual(
                validation.Items[0].Source.Identity!.Value,
                durable.Entries[0].DestinationIdentity!.Value);
            Assert.IsFalse(durable.RequiresRecovery);
            Assert.IsFalse(durable.GrantsRollbackAuthority);
        }
        finally
        {
            DeleteTreeNoThrow(temp);
        }
    }

    [TestMethod]
    public async Task MutationSensitiveHistoryCannotSettleAsOrdinaryFailure()
    {
        var temp = CreateTempDirectory();
        try
        {
            var plan = CreatePlan();
            var validation = CreateValidation(plan);
            using var store = new SqliteDirectorySameVolumeMoveActionHistoryStore(
                Path.Combine(temp, "history.sqlite"));
            await store.BeginAsync(validation, DateTimeOffset.UtcNow);
            await store.MarkMutationStartedAsync(plan.Id, 0, DateTimeOffset.UtcNow);

            await AssertThrowsAsync<InvalidOperationException>(async () =>
                await store.CompleteAsync(
                    plan.Id,
                    DirectorySameVolumeMoveActionTerminalState.Failed,
                    DateTimeOffset.UtcNow));

            var history = await store.GetAsync(plan.Id);
            Assert.IsNotNull(history);
            Assert.IsNull(history.TerminalState);
            Assert.IsTrue(history.RequiresRecovery);
        }
        finally
        {
            DeleteTreeNoThrow(temp);
        }
    }

    [TestMethod]
    public async Task ExecutorPersistsMutationStartedBeforeProviderAndCommitsIdentity()
    {
        var temp = CreateTempDirectory();
        try
        {
            var plan = CreatePlan();
            using var store = new SqliteDirectorySameVolumeMoveActionHistoryStore(
                Path.Combine(temp, "history.sqlite"));
            var validator = new StableDirectoryValidator();
            var mutation = new BarrierCheckingMutation(store, plan.Id);
            var executor = new DirectorySameVolumeMoveOperationExecutor(validator, store, mutation);

            var result = await executor.ExecuteAsync(plan);

            Assert.AreEqual(FileOperationExecutionState.Succeeded, result.State);
            Assert.AreEqual(1, result.CompletedEntryCount);
            Assert.AreEqual(1, mutation.CallCount);
            var history = await store.GetAsync(plan.Id);
            Assert.IsNotNull(history);
            Assert.AreEqual(DirectorySameVolumeMoveActionTerminalState.Succeeded, history.TerminalState);
            Assert.AreEqual(DirectorySameVolumeMoveActionEntryState.Committed, history.Entries[0].State);
        }
        finally
        {
            DeleteTreeNoThrow(temp);
        }
    }

    [TestMethod]
    public async Task ProviderFailureAfterMutationBarrierBecomesRecoveryRequired()
    {
        var temp = CreateTempDirectory();
        try
        {
            var plan = CreatePlan();
            using var store = new SqliteDirectorySameVolumeMoveActionHistoryStore(
                Path.Combine(temp, "history.sqlite"));
            var executor = new DirectorySameVolumeMoveOperationExecutor(
                new StableDirectoryValidator(),
                store,
                new ThrowingMutation());

            var result = await executor.ExecuteAsync(plan);

            Assert.AreEqual(FileOperationExecutionState.Failed, result.State);
            Assert.AreEqual("DirectoryMoveMutationFailed", result.Failure?.Code);
            var history = await store.GetAsync(plan.Id);
            Assert.IsNotNull(history);
            Assert.AreEqual(
                DirectorySameVolumeMoveActionTerminalState.RecoveryRequired,
                history.TerminalState);
            Assert.AreEqual(
                DirectorySameVolumeMoveActionEntryState.RecoveryRequired,
                history.Entries[0].State);
            Assert.IsTrue(history.RequiresRecovery);
        }
        finally
        {
            DeleteTreeNoThrow(temp);
        }
    }

    [TestMethod]
    public async Task RawGuardRequiresExactNtfsBeforeVolumeRelationshipOrRename()
    {
        var request = CreateRequest(CreateValidation(CreatePlan()));
        var inner = new FakeDirectoryMutation();
        var filesystem = new QueueFilesystemProbe(Capability(
            request.SourceDirectory,
            WindowsMutationFilesystemCapabilityState.UnsupportedFilesystem,
            "ReFS"));
        var relationship = new FakeVolumeRelationshipProbe(FileOperationVolumeRelationshipState.SameVolume);
        var guarded = new WindowsNtfsDirectorySameVolumeMoveMutationPrimitive(
            inner,
            filesystem,
            relationship);

        await AssertThrowsAsync<NotSupportedException>(async () =>
            await guarded.RenameDirectoryAsync(request));

        Assert.AreEqual(0, inner.CallCount);
        Assert.AreEqual(1, filesystem.CallCount);
        Assert.AreEqual(0, relationship.CallCount);
    }

    [TestMethod]
    public async Task RawGuardRequiresStrongerSameVolumeRelationshipBeforeRename()
    {
        var request = CreateRequest(CreateValidation(CreatePlan()));
        var inner = new FakeDirectoryMutation();
        var filesystem = new QueueFilesystemProbe(
            Capability(request.SourceDirectory, WindowsMutationFilesystemCapabilityState.SupportedNtfs, "NTFS"),
            Capability(request.DestinationDirectory, WindowsMutationFilesystemCapabilityState.SupportedNtfs, "NTFS"));
        var relationship = new FakeVolumeRelationshipProbe(FileOperationVolumeRelationshipState.DifferentVolume);
        var guarded = new WindowsNtfsDirectorySameVolumeMoveMutationPrimitive(
            inner,
            filesystem,
            relationship);

        await AssertThrowsAsync<NotSupportedException>(async () =>
            await guarded.RenameDirectoryAsync(request));

        Assert.AreEqual(0, inner.CallCount);
        Assert.AreEqual(2, filesystem.CallCount);
        Assert.AreEqual(1, relationship.CallCount);
    }

    [TestMethod]
    public async Task ExactNtfsAndSameVolumeEvidenceDelegatesToRawDirectoryRename()
    {
        var request = CreateRequest(CreateValidation(CreatePlan()));
        var inner = new FakeDirectoryMutation();
        var filesystem = new QueueFilesystemProbe(
            Capability(request.SourceDirectory, WindowsMutationFilesystemCapabilityState.SupportedNtfs, "NTFS"),
            Capability(request.DestinationDirectory, WindowsMutationFilesystemCapabilityState.SupportedNtfs, "NTFS"));
        var relationship = new FakeVolumeRelationshipProbe(FileOperationVolumeRelationshipState.SameVolume);
        var guarded = new WindowsNtfsDirectorySameVolumeMoveMutationPrimitive(
            inner,
            filesystem,
            relationship);

        var lease = await guarded.RenameDirectoryAsync(request);

        Assert.AreEqual(1, inner.CallCount);
        Assert.AreSame(inner.Lease, lease);
        await lease.DisposeAsync();
    }

    [TestMethod]
    public async Task NativeDirectoryRenamePreservesIdentityAndNestedDescendants()
    {
        var temp = CreateTempDirectory();
        try
        {
            var sourceRootPath = Path.Combine(temp, "source-parent");
            var destinationRootPath = Path.Combine(temp, "destination-parent");
            Directory.CreateDirectory(sourceRootPath);
            Directory.CreateDirectory(destinationRootPath);
            var sourcePath = Path.Combine(sourceRootPath, "MoveMe");
            var nestedDirectory = Path.Combine(sourcePath, "nested");
            Directory.CreateDirectory(nestedDirectory);
            File.WriteAllText(Path.Combine(nestedDirectory, "payload.txt"), "directory-move");

            var resolver = new WindowsFileOperationCanonicalPathResolver();
            var sourceRoot = await resolver.ResolveAsync(sourceRootPath);
            var destinationRoot = await resolver.ResolveAsync(destinationRootPath);
            var source = await resolver.ResolveAsync(sourcePath);
            var destinationPath = Path.Combine(destinationRootPath, "MoveMe");
            var destination = await resolver.ResolveAsync(destinationPath, allowMissingLeaf: true);
            Assert.AreEqual(FileOperationCanonicalPathState.Directory, source.State);
            Assert.AreEqual(FileOperationCanonicalPathState.Missing, destination.State);
            Assert.AreEqual(sourceRoot.Identity!.Value.VolumeSerialNumber, destinationRoot.Identity!.Value.VolumeSerialNumber);

            var entry = new FileOperationEntry(sourcePath, "MoveMe", IsDirectory: true);
            var item = new FileOperationExecutionValidationItem(
                entry,
                source,
                destination,
                FileOperationExecutionValidationDecision.Ready,
                "ready");
            var request = new DirectorySameVolumeMoveMutationRequest(item, sourceRoot, destinationRoot);
            var primitive = new WindowsDirectorySameVolumeMoveMutationPrimitive();

            await using (var lease = await primitive.RenameDirectoryAsync(request))
            {
                Assert.AreEqual(source.Identity!.Value, lease.Receipt.SourceIdentity);
                Assert.AreEqual(source.Identity!.Value, lease.Receipt.DestinationIdentity);
                Assert.IsFalse(Directory.Exists(sourcePath));
                Assert.IsTrue(Directory.Exists(destinationPath));
                Assert.IsTrue(File.Exists(Path.Combine(destinationPath, "nested", "payload.txt")));

                var moved = await resolver.ResolveAsync(destinationPath);
                Assert.AreEqual(FileOperationCanonicalPathState.Directory, moved.State);
                Assert.AreEqual(source.Identity!.Value, moved.Identity!.Value);
            }
        }
        finally
        {
            DeleteTreeNoThrow(temp);
        }
    }

    [TestMethod]
    public async Task NativeDirectoryRenameNeverReplacesDestinationCreatedAfterValidation()
    {
        var temp = CreateTempDirectory();
        try
        {
            var sourceRootPath = Path.Combine(temp, "source-parent");
            var destinationRootPath = Path.Combine(temp, "destination-parent");
            Directory.CreateDirectory(sourceRootPath);
            Directory.CreateDirectory(destinationRootPath);
            var sourcePath = Path.Combine(sourceRootPath, "MoveMe");
            Directory.CreateDirectory(sourcePath);

            var resolver = new WindowsFileOperationCanonicalPathResolver();
            var sourceRoot = await resolver.ResolveAsync(sourceRootPath);
            var destinationRoot = await resolver.ResolveAsync(destinationRootPath);
            var source = await resolver.ResolveAsync(sourcePath);
            var destinationPath = Path.Combine(destinationRootPath, "MoveMe");
            var destination = await resolver.ResolveAsync(destinationPath, allowMissingLeaf: true);
            var entry = new FileOperationEntry(sourcePath, "MoveMe", IsDirectory: true);
            var request = new DirectorySameVolumeMoveMutationRequest(
                new FileOperationExecutionValidationItem(
                    entry,
                    source,
                    destination,
                    FileOperationExecutionValidationDecision.Ready,
                    "ready"),
                sourceRoot,
                destinationRoot);

            Directory.CreateDirectory(destinationPath);
            File.WriteAllText(Path.Combine(destinationPath, "keep.txt"), "existing");
            var primitive = new WindowsDirectorySameVolumeMoveMutationPrimitive();

            await AssertThrowsAsync<IOException>(async () =>
                await primitive.RenameDirectoryAsync(request));

            Assert.IsTrue(Directory.Exists(sourcePath));
            Assert.IsTrue(Directory.Exists(destinationPath));
            Assert.AreEqual("existing", File.ReadAllText(Path.Combine(destinationPath, "keep.txt")));
        }
        finally
        {
            DeleteTreeNoThrow(temp);
        }
    }

    private static FileOperationPlan CreatePlan(bool isDirectory = true)
    {
        var entry = new FileOperationEntry(
            @"C:\Source\MoveMe",
            "MoveMe",
            isDirectory);
        return new FileOperationPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            FileOperationKind.Move,
            FileOperationCollisionPolicy.Stop,
            new FileOperationIntent(
                "left",
                Guid.NewGuid(),
                @"C:\Source",
                new[] { entry },
                "right",
                Guid.NewGuid(),
                @"C:\Destination"));
    }

    private static FileOperationExecutionValidationResult CreateValidation(
        FileOperationPlan plan,
        bool isDirectory = true)
    {
        var sourceRoot = new FileOperationCanonicalPath(
            plan.Intent.SourceDirectoryPath,
            plan.Intent.SourceDirectoryPath,
            FileOperationCanonicalPathState.Directory,
            false,
            new FileIdentity(101, 1001));
        var destinationRoot = new FileOperationCanonicalPath(
            plan.Intent.DestinationDirectoryPath,
            plan.Intent.DestinationDirectoryPath,
            FileOperationCanonicalPathState.Directory,
            false,
            new FileIdentity(101, 1002));
        var entry = plan.Intent.Entries[0];
        var source = new FileOperationCanonicalPath(
            entry.Path,
            entry.Path,
            isDirectory
                ? FileOperationCanonicalPathState.Directory
                : FileOperationCanonicalPathState.File,
            false,
            new FileIdentity(101, 2001));
        var destinationPath = Path.Combine(destinationRoot.CanonicalPath, entry.Name);
        var destination = new FileOperationCanonicalPath(
            destinationPath,
            destinationPath,
            FileOperationCanonicalPathState.Missing,
            false);
        return new FileOperationExecutionValidationResult(
            plan,
            sourceRoot,
            destinationRoot,
            new[]
            {
                new FileOperationExecutionValidationItem(
                    entry,
                    source,
                    destination,
                    FileOperationExecutionValidationDecision.Ready,
                    "ready"),
            },
            FileOperationExecutionValidationStatus.Ready,
            DateTimeOffset.UtcNow,
            "ready");
    }

    private static DirectorySameVolumeMoveMutationRequest CreateRequest(
        FileOperationExecutionValidationResult validation) =>
        new(
            validation.Items[0],
            validation.SourceDirectory,
            validation.DestinationDirectory);

    private static WindowsMutationFilesystemCapability Capability(
        FileOperationCanonicalPath root,
        WindowsMutationFilesystemCapabilityState state,
        string? fileSystemName) =>
        new(
            root.CanonicalPath,
            root.Identity!.Value,
            state,
            fileSystemName,
            fileSystemName is null ? "unavailable" : $"filesystem is {fileSystemName}");

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "FileOp-directory-move-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTreeNoThrow(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        Assert.Fail($"Expected {typeof(TException).Name} to be thrown.");
    }

    private sealed class StableDirectoryValidator : IFileOperationExecutionValidator
    {
        public ValueTask<FileOperationExecutionValidationResult> ValidateAsync(
            FileOperationPlan plan,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(CreateValidation(plan));
        }
    }

    private sealed class BarrierCheckingMutation : IDirectorySameVolumeMoveMutationPrimitive
    {
        private readonly IDirectorySameVolumeMoveActionHistoryStore _store;
        private readonly Guid _operationId;

        public BarrierCheckingMutation(
            IDirectorySameVolumeMoveActionHistoryStore store,
            Guid operationId)
        {
            _store = store;
            _operationId = operationId;
        }

        public int CallCount { get; private set; }

        public async ValueTask<IDirectorySameVolumeMoveMutationLease> RenameDirectoryAsync(
            DirectorySameVolumeMoveMutationRequest request)
        {
            CallCount++;
            var history = await _store.GetAsync(_operationId);
            Assert.IsNotNull(history);
            Assert.AreEqual(
                DirectorySameVolumeMoveActionEntryState.MutationStarted,
                history.Entries[0].State,
                "The raw provider must not be called until MutationStarted is durable.");
            var identity = request.Item.Source.Identity!.Value;
            return new TestLease(new DirectorySameVolumeMoveMutationReceipt(
                request.Item.Source.CanonicalPath,
                request.Item.Destination.CanonicalPath,
                identity,
                identity));
        }
    }

    private sealed class ThrowingMutation : IDirectorySameVolumeMoveMutationPrimitive
    {
        public ValueTask<IDirectorySameVolumeMoveMutationLease> RenameDirectoryAsync(
            DirectorySameVolumeMoveMutationRequest request) =>
            ValueTask.FromException<IDirectorySameVolumeMoveMutationLease>(
                new IOException("provider failure after barrier"));
    }

    private sealed class FakeDirectoryMutation : IDirectorySameVolumeMoveMutationPrimitive
    {
        public FakeDirectoryMutation()
        {
            Lease = new TestLease(new DirectorySameVolumeMoveMutationReceipt(
                @"C:\Source\MoveMe",
                @"C:\Destination\MoveMe",
                new FileIdentity(101, 2001),
                new FileIdentity(101, 2001)));
        }

        public int CallCount { get; private set; }

        public TestLease Lease { get; }

        public ValueTask<IDirectorySameVolumeMoveMutationLease> RenameDirectoryAsync(
            DirectorySameVolumeMoveMutationRequest request)
        {
            CallCount++;
            return ValueTask.FromResult<IDirectorySameVolumeMoveMutationLease>(Lease);
        }
    }

    private sealed class TestLease : IDirectorySameVolumeMoveMutationLease
    {
        public TestLease(DirectorySameVolumeMoveMutationReceipt receipt) => Receipt = receipt;

        public DirectorySameVolumeMoveMutationReceipt Receipt { get; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class QueueFilesystemProbe : IWindowsMutationFilesystemCapabilityProbe
    {
        private readonly Queue<WindowsMutationFilesystemCapability> _results;

        public QueueFilesystemProbe(params WindowsMutationFilesystemCapability[] results) =>
            _results = new Queue<WindowsMutationFilesystemCapability>(results);

        public int CallCount { get; private set; }

        public WindowsMutationFilesystemCapability QueryDirectory(
            string canonicalDirectoryPath,
            FileIdentity expectedIdentity)
        {
            CallCount++;
            return _results.Dequeue();
        }
    }

    private sealed class FakeVolumeRelationshipProbe : IFileOperationVolumeRelationshipProbe
    {
        private readonly FileOperationVolumeRelationshipState _state;

        public FakeVolumeRelationshipProbe(FileOperationVolumeRelationshipState state) => _state = state;

        public int CallCount { get; private set; }

        public FileOperationVolumeRelationship Query(
            string canonicalSourceDirectoryPath,
            FileIdentity expectedSourceIdentity,
            string canonicalDestinationDirectoryPath,
            FileIdentity expectedDestinationIdentity)
        {
            CallCount++;
            return new FileOperationVolumeRelationship(
                _state,
                _state == FileOperationVolumeRelationshipState.Unavailable ? null : @"\\?\Volume{source}\",
                _state == FileOperationVolumeRelationshipState.SameVolume
                    ? @"\\?\Volume{source}\"
                    : _state == FileOperationVolumeRelationshipState.DifferentVolume
                        ? @"\\?\Volume{destination}\"
                        : null,
                _state.ToString());
        }
    }
}
