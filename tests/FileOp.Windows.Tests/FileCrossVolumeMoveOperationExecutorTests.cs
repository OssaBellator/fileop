using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileCrossVolumeMoveOperationExecutorTests
{
    [TestMethod]
    public async Task SuccessfulMoveCommitsCopyBeforeSourceDeleteBarrierAndAuthorization()
    {
        using var fixture = new HistoryFixture();
        var events = new List<string>();
        var plan = CreatePlan();
        var validator = new FakeValidator((candidate, _) => CreateValidation(candidate));
        using var history = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.DatabasePath);
        var copy = new FakeCopyMutation(events);
        var sourceDelete = new FakeSourceDeletePrimitive(events);
        var executor = new FileCrossVolumeMoveOperationExecutor(
            validator,
            history,
            copy,
            sourceDelete);

        var result = await executor.ExecuteAsync(plan);

        Assert.AreEqual(FileOperationExecutionState.Succeeded, result.State);
        Assert.AreEqual(1, result.CompletedEntryCount);
        Assert.AreEqual(1, copy.CallCount);
        Assert.AreEqual(1, sourceDelete.CallCount);
        Assert.IsTrue(sourceDelete.LastLease?.AuthorizationObserved == true);
        Assert.IsTrue(sourceDelete.LastLease?.MutationPerformed == true);
        Assert.IsTrue(IndexOf(events, "copy") < IndexOf(events, "copy-lease:dispose"));
        Assert.IsTrue(IndexOf(events, "copy-lease:dispose") < IndexOf(events, "delete:acquire"));
        Assert.IsTrue(IndexOf(events, "delete:acquire") < IndexOf(events, "delete:authorized"));
        Assert.IsTrue(IndexOf(events, "delete:authorized") < IndexOf(events, "delete-lease:dispose"));

        var persisted = await history.GetAsync(plan.Id);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(FileCrossVolumeMoveTerminalState.Succeeded, persisted.TerminalState);
        Assert.AreEqual(FileCrossVolumeMoveEntryState.Moved, persisted.Entries[0].State);
        Assert.IsFalse(persisted.RequiresRecovery);
    }

    [TestMethod]
    public async Task CancellationRequestedDuringCopyStopsAfterDestinationCommitWithoutDeleteAuthority()
    {
        using var fixture = new HistoryFixture();
        var events = new List<string>();
        var plan = CreatePlan();
        var validator = new FakeValidator((candidate, _) => CreateValidation(candidate));
        using var history = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.DatabasePath);
        FileCrossVolumeMoveOperationExecutor? executor = null;
        var copy = new FakeCopyMutation(events, async () =>
        {
            var active = executor ?? throw new InvalidOperationException("executor not assigned");
            Assert.IsTrue(await active.RequestCancellationAsync(plan.Id));
        });
        var sourceDelete = new FakeSourceDeletePrimitive(events);
        executor = new FileCrossVolumeMoveOperationExecutor(
            validator,
            history,
            copy,
            sourceDelete);

        var result = await executor.ExecuteAsync(plan);

        Assert.AreEqual(FileOperationExecutionState.Cancelled, result.State);
        Assert.AreEqual(0, result.CompletedEntryCount);
        Assert.AreEqual(1, copy.CallCount);
        Assert.AreEqual(0, sourceDelete.CallCount);
        Assert.IsFalse(events.Contains("delete:acquire"));
        Assert.IsFalse(events.Contains("delete:authorized"));

        var persisted = await history.GetAsync(plan.Id);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(FileCrossVolumeMoveTerminalState.Cancelled, persisted.TerminalState);
        Assert.AreEqual(
            FileCrossVolumeMoveEntryState.DestinationCommitted,
            persisted.Entries[0].State);
        Assert.IsTrue(persisted.HasRetainedSourceDuplicates);
        Assert.IsFalse(persisted.RequiresRecovery);
    }

    [TestMethod]
    public async Task SourceDeletePreparationFailureIsSafeFailureWithCommittedDestinationAndRetainedSource()
    {
        using var fixture = new HistoryFixture();
        var events = new List<string>();
        var plan = CreatePlan();
        var validator = new FakeValidator((candidate, _) => CreateValidation(candidate));
        using var history = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.DatabasePath);
        var copy = new FakeCopyMutation(events);
        var sourceDelete = new FakeSourceDeletePrimitive(events)
        {
            ThrowOnAcquire = true,
        };
        var executor = new FileCrossVolumeMoveOperationExecutor(
            validator,
            history,
            copy,
            sourceDelete);

        var result = await executor.ExecuteAsync(plan);

        Assert.AreEqual(FileOperationExecutionState.Failed, result.State);
        Assert.AreEqual("CrossVolumeMoveSourceDeletePreparationFailed", result.Failure?.Code);
        Assert.AreEqual(1, sourceDelete.CallCount);

        var persisted = await history.GetAsync(plan.Id);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(FileCrossVolumeMoveTerminalState.Failed, persisted.TerminalState);
        Assert.AreEqual(FileCrossVolumeMoveEntryState.Failed, persisted.Entries[0].State);
        Assert.IsNotNull(persisted.Entries[0].DestinationIdentity);
        Assert.IsNotNull(persisted.Entries[0].DestinationContentFingerprint);
        Assert.IsTrue(persisted.HasRetainedSourceDuplicates);
        Assert.IsFalse(persisted.RequiresRecovery);
    }

    [TestMethod]
    public async Task SourceDeleteMutationFailureAfterBarrierRequiresRecovery()
    {
        using var fixture = new HistoryFixture();
        var events = new List<string>();
        var plan = CreatePlan();
        var validator = new FakeValidator((candidate, _) => CreateValidation(candidate));
        using var history = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.DatabasePath);
        var copy = new FakeCopyMutation(events);
        var sourceDelete = new FakeSourceDeletePrimitive(events)
        {
            ThrowOnMutation = true,
        };
        var executor = new FileCrossVolumeMoveOperationExecutor(
            validator,
            history,
            copy,
            sourceDelete);

        var result = await executor.ExecuteAsync(plan);

        Assert.AreEqual(FileOperationExecutionState.Failed, result.State);
        Assert.AreEqual("CrossVolumeMoveSourceDeleteFailed", result.Failure?.Code);
        Assert.IsTrue(sourceDelete.LastLease?.AuthorizationObserved == true);

        var persisted = await history.GetAsync(plan.Id);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(
            FileCrossVolumeMoveTerminalState.RecoveryRequired,
            persisted.TerminalState);
        Assert.AreEqual(
            FileCrossVolumeMoveEntryState.RecoveryRequired,
            persisted.Entries[0].State);
        Assert.IsTrue(persisted.RequiresRecovery);
    }

    [TestMethod]
    public async Task SourceDeleteSuccessWithoutMutationProofRequiresRecovery()
    {
        using var fixture = new HistoryFixture();
        var events = new List<string>();
        var plan = CreatePlan();
        var validator = new FakeValidator((candidate, _) => CreateValidation(candidate));
        using var history = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.DatabasePath);
        var copy = new FakeCopyMutation(events);
        var sourceDelete = new FakeSourceDeletePrimitive(events)
        {
            ReportMutationPerformed = false,
        };
        var executor = new FileCrossVolumeMoveOperationExecutor(
            validator,
            history,
            copy,
            sourceDelete);

        var result = await executor.ExecuteAsync(plan);

        Assert.AreEqual(FileOperationExecutionState.Failed, result.State);
        Assert.AreEqual("CrossVolumeMoveSourceDeleteFailed", result.Failure?.Code);
        Assert.IsTrue(sourceDelete.LastLease?.AuthorizationObserved == true);
        Assert.IsFalse(sourceDelete.LastLease?.MutationPerformed == true);

        var persisted = await history.GetAsync(plan.Id);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(
            FileCrossVolumeMoveTerminalState.RecoveryRequired,
            persisted.TerminalState);
        Assert.AreEqual(
            FileCrossVolumeMoveEntryState.RecoveryRequired,
            persisted.Entries[0].State);
        Assert.IsTrue(persisted.RequiresRecovery);
    }

    [TestMethod]
    public async Task InvalidCopyReceiptNeverAcquiresSourceDeleteCapability()
    {
        using var fixture = new HistoryFixture();
        var events = new List<string>();
        var plan = CreatePlan();
        var validator = new FakeValidator((candidate, _) => CreateValidation(candidate));
        using var history = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.DatabasePath);
        var copy = new FakeCopyMutation(events)
        {
            ReturnInvalidSourceIdentity = true,
        };
        var sourceDelete = new FakeSourceDeletePrimitive(events);
        var executor = new FileCrossVolumeMoveOperationExecutor(
            validator,
            history,
            copy,
            sourceDelete);

        var result = await executor.ExecuteAsync(plan);

        Assert.AreEqual(FileOperationExecutionState.Failed, result.State);
        Assert.AreEqual("CrossVolumeMoveCopyReceiptInvalid", result.Failure?.Code);
        Assert.AreEqual(0, sourceDelete.CallCount);
        Assert.IsFalse(events.Contains("delete:acquire"));

        var persisted = await history.GetAsync(plan.Id);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(
            FileCrossVolumeMoveTerminalState.RecoveryRequired,
            persisted.TerminalState);
        Assert.IsTrue(persisted.RequiresRecovery);
    }

    [TestMethod]
    public async Task FreshSourceIdentityChangeFailsBeforeCopyBarrier()
    {
        using var fixture = new HistoryFixture();
        var events = new List<string>();
        var plan = CreatePlan();
        var validator = new FakeValidator((candidate, call) =>
            CreateValidation(candidate, sourceFileReference: call == 1 ? 100UL : 999UL));
        using var history = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.DatabasePath);
        var copy = new FakeCopyMutation(events);
        var sourceDelete = new FakeSourceDeletePrimitive(events);
        var executor = new FileCrossVolumeMoveOperationExecutor(
            validator,
            history,
            copy,
            sourceDelete);

        var result = await executor.ExecuteAsync(plan);

        Assert.AreEqual(FileOperationExecutionState.Failed, result.State);
        Assert.AreEqual("CrossVolumeMoveEntryRevalidationChanged", result.Failure?.Code);
        Assert.AreEqual(0, copy.CallCount);
        Assert.AreEqual(0, sourceDelete.CallCount);

        var persisted = await history.GetAsync(plan.Id);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(FileCrossVolumeMoveTerminalState.Failed, persisted.TerminalState);
        Assert.AreEqual(FileCrossVolumeMoveEntryState.Failed, persisted.Entries[0].State);
        Assert.IsFalse(persisted.RequiresRecovery);
    }

    private static int IndexOf(IReadOnlyList<string> events, string value)
    {
        for (var index = 0; index < events.Count; index++)
        {
            if (events[index] == value)
            {
                return index;
            }
        }
        return -1;
    }

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
            new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero),
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

    private static FileOperationExecutionValidationResult CreateValidation(
        FileOperationPlan plan,
        ulong sourceFileReference = 100)
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
                        Identity: new FileIdentity(1, sourceFileReference)),
                    new FileOperationCanonicalPath(
                        Path.Combine(plan.Intent.DestinationDirectoryPath, entry.Name),
                        Path.Combine(canonicalDestinationDirectory, entry.Name),
                        FileOperationCanonicalPathState.Missing,
                        IsLeafReparsePoint: false),
                    FileOperationExecutionValidationDecision.Ready,
                    "ready"),
            },
            FileOperationExecutionValidationStatus.Ready,
            new DateTimeOffset(2026, 8, 14, 0, 1, 0, TimeSpan.Zero),
            "ready");
    }

    private static FileContentFingerprint CreateFingerprint() =>
        new(FileContentFingerprintAlgorithm.Sha256, new string('a', 64));

    private sealed class FakeValidator : IFileOperationExecutionValidator
    {
        private readonly Func<FileOperationPlan, int, FileOperationExecutionValidationResult> _factory;
        private int _callCount;

        public FakeValidator(
            Func<FileOperationPlan, int, FileOperationExecutionValidationResult> factory) =>
            _factory = factory;

        public ValueTask<FileOperationExecutionValidationResult> ValidateAsync(
            FileOperationPlan plan,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _callCount);
            return ValueTask.FromResult(_factory(plan, call));
        }
    }

    private sealed class FakeCopyMutation : IFileCopyMutationPrimitive
    {
        private readonly List<string> _events;
        private readonly Func<ValueTask>? _onCopy;

        public FakeCopyMutation(List<string> events, Func<ValueTask>? onCopy = null)
        {
            _events = events;
            _onCopy = onCopy;
        }

        public int CallCount { get; private set; }

        public bool ReturnInvalidSourceIdentity { get; set; }

        public async ValueTask<IFileCopyMutationLease> CopyNewFileAsync(
            FileCopyMutationRequest request)
        {
            CallCount++;
            _events.Add("copy");
            if (_onCopy is not null)
            {
                await _onCopy().ConfigureAwait(false);
            }

            var sourceIdentity = ReturnInvalidSourceIdentity
                ? new FileIdentity(1, 9999)
                : request.Item.Source.Identity!.Value;
            return new FakeCopyLease(
                new FileCopyMutationReceipt(
                    request.Item.Source.CanonicalPath,
                    request.Item.Destination.CanonicalPath,
                    sourceIdentity,
                    new FileIdentity(2, 500),
                    CreateFingerprint()),
                _events);
        }
    }

    private sealed class FakeCopyLease : IFileCopyMutationLease
    {
        private readonly List<string> _events;

        public FakeCopyLease(FileCopyMutationReceipt receipt, List<string> events)
        {
            Receipt = receipt;
            _events = events;
        }

        public FileCopyMutationReceipt Receipt { get; }

        public ValueTask DisposeAsync()
        {
            _events.Add("copy-lease:dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSourceDeletePrimitive : IFileCrossVolumeMoveSourceDeletePrimitive
    {
        private readonly List<string> _events;

        public FakeSourceDeletePrimitive(List<string> events) => _events = events;

        public int CallCount { get; private set; }

        public bool ThrowOnAcquire { get; set; }

        public bool ThrowOnMutation { get; set; }

        public bool ReportMutationPerformed { get; set; } = true;

        public FakeSourceDeleteLease? LastLease { get; private set; }

        public ValueTask<IFileCrossVolumeMoveSourceDeleteLease> AcquireAsync(
            FileCrossVolumeMoveSourceDeleteRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            _events.Add("delete:acquire");
            if (ThrowOnAcquire)
            {
                throw new IOException("source delete capability unavailable");
            }

            LastLease = new FakeSourceDeleteLease(
                new FileCrossVolumeMoveSourceDeleteEvidence(request),
                _events,
                ThrowOnMutation,
                ReportMutationPerformed);
            return ValueTask.FromResult<IFileCrossVolumeMoveSourceDeleteLease>(LastLease);
        }
    }

    private sealed class FakeSourceDeleteLease : IFileCrossVolumeMoveSourceDeleteLease
    {
        private readonly List<string> _events;
        private readonly bool _throwOnMutation;
        private readonly bool _reportMutationPerformed;

        public FakeSourceDeleteLease(
            FileCrossVolumeMoveSourceDeleteEvidence evidence,
            List<string> events,
            bool throwOnMutation,
            bool reportMutationPerformed)
        {
            Evidence = evidence;
            _events = events;
            _throwOnMutation = throwOnMutation;
            _reportMutationPerformed = reportMutationPerformed;
        }

        public FileCrossVolumeMoveSourceDeleteEvidence Evidence { get; }

        public bool DeleteAccessCapabilityHeld => true;

        public bool SourceDeleteMutationPerformed => MutationPerformed;

        public bool AuthorizationObserved { get; private set; }

        public bool MutationPerformed { get; private set; }

        public ValueTask MarkDeletePendingAsync(
            FileCrossVolumeMoveSourceDeleteAuthorization authorization,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.IsTrue(authorization.SourceDeleteBarrierSatisfied);
            Assert.IsTrue(authorization.SourceDeleteMutationAuthorized);
            Assert.IsTrue(authorization.IsBoundTo(Evidence));
            Assert.AreEqual(
                FileCrossVolumeMoveEntryState.SourceDeleteStarted,
                authorization.BarrierHistory.Entries[Evidence.Ordinal].State);
            AuthorizationObserved = true;
            _events.Add("delete:authorized");
            if (_throwOnMutation)
            {
                throw new IOException("delete failed after barrier");
            }
            if (_reportMutationPerformed)
            {
                MutationPerformed = true;
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _events.Add("delete-lease:dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class HistoryFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "FileOp.CrossVolumeMoveExecutor.Tests",
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