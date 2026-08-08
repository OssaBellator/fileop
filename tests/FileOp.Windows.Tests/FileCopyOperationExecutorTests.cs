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
public sealed class FileCopyOperationExecutorTests
{
    [TestMethod]
    public async Task SuccessfulCopyCommitsHistoryBeforeReportingProgress()
    {
        var events = new List<string>();
        var plan = CreatePlan(1);
        var validator = new FakeValidator((candidate, _, _) =>
        {
            events.Add("validate");
            return ValueTask.FromResult(CreateValidation(candidate));
        });
        var history = new RecordingHistoryStore(events);
        var mutation = new FakeMutation((item, _) =>
        {
            events.Add("mutate");
            return ValueTask.FromResult(CreateReceipt(item));
        });
        using var executor = new FileCopyOperationExecutor(validator, history, mutation);
        var progress = new RecordingProgress(events);

        var result = await executor.ExecuteAsync(plan, progress);

        Assert.AreEqual(FileOperationExecutionState.Succeeded, result.State);
        Assert.AreEqual(1, result.CompletedEntryCount);
        Assert.AreEqual(2, validator.CallCount);
        Assert.AreEqual(1, mutation.CallCount);
        Assert.IsTrue(IndexOf(events, "history:start:0") < IndexOf(events, "mutate"));
        Assert.IsTrue(IndexOf(events, "mutate") < IndexOf(events, "history:commit:0"));
        Assert.IsTrue(IndexOf(events, "history:commit:0") < IndexOf(events, "progress:Running:1"));
        Assert.AreEqual(
            "history:complete:Succeeded",
            events.Last(static value => value.StartsWith("history:complete:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task CancellationDuringMutationStopsAfterCommittedFileBoundary()
    {
        var events = new List<string>();
        var plan = CreatePlan(2);
        var validator = new FakeValidator((candidate, _, _) =>
            ValueTask.FromResult(CreateValidation(candidate)));
        var history = new RecordingHistoryStore(events);
        FileCopyOperationExecutor? executor = null;
        var mutation = new FakeMutation(async (item, call) =>
        {
            events.Add($"mutate:{call}");
            var activeExecutor = executor ?? throw new InvalidOperationException("Executor not assigned.");
            Assert.IsTrue(await activeExecutor.RequestCancellationAsync(plan.Id));
            return CreateReceipt(item);
        });
        executor = new FileCopyOperationExecutor(validator, history, mutation);
        using (executor)
        {
            var result = await executor.ExecuteAsync(plan, new RecordingProgress(events));

            Assert.AreEqual(FileOperationExecutionState.Cancelled, result.State);
            Assert.AreEqual(1, result.CompletedEntryCount);
            Assert.AreEqual(1, mutation.CallCount);
            Assert.IsTrue(
                IndexOf(events, "history:commit:0") <
                IndexOf(events, "history:complete:Cancelled"));
        }
    }

    [TestMethod]
    public async Task CommitFailureAfterMutationForcesRecoveryPath()
    {
        var events = new List<string>();
        var plan = CreatePlan(1);
        var validator = new FakeValidator((candidate, _, _) =>
            ValueTask.FromResult(CreateValidation(candidate)));
        var history = new RecordingHistoryStore(events) { ThrowOnCommit = true };
        var mutation = new FakeMutation((item, _) =>
            ValueTask.FromResult(CreateReceipt(item)));
        using var executor = new FileCopyOperationExecutor(validator, history, mutation);
        var progress = new RecordingProgress(events);

        var result = await executor.ExecuteAsync(plan, progress);

        Assert.AreEqual(FileOperationExecutionState.Failed, result.State);
        Assert.AreEqual("CopyCommitBarrierFailed", result.Failure?.Code);
        Assert.AreEqual(0, result.CompletedEntryCount);
        Assert.IsTrue(events.Contains("history:recovery:0"));
        Assert.IsTrue(events.Contains("history:complete:RecoveryRequired"));
        Assert.IsFalse(events.Contains("progress:Running:1"));
    }

    [TestMethod]
    public async Task MutationFailureAfterBarrierForcesRecoveryPath()
    {
        var events = new List<string>();
        var plan = CreatePlan(1);
        var validator = new FakeValidator((candidate, _, _) =>
            ValueTask.FromResult(CreateValidation(candidate)));
        var history = new RecordingHistoryStore(events);
        var mutation = new FakeMutation((_, _) =>
            new ValueTask<FileCopyMutationReceipt>(
                Task.FromException<FileCopyMutationReceipt>(new IOException("copy failed"))));
        using var executor = new FileCopyOperationExecutor(validator, history, mutation);

        var result = await executor.ExecuteAsync(plan, new RecordingProgress(events));

        Assert.AreEqual(FileOperationExecutionState.Failed, result.State);
        Assert.AreEqual("CopyMutationFailed", result.Failure?.Code);
        Assert.IsTrue(events.Contains("history:start:0"));
        Assert.IsTrue(events.Contains("history:recovery:0"));
        Assert.IsTrue(events.Contains("history:complete:RecoveryRequired"));
    }

    [TestMethod]
    public async Task FreshIdentityChangeFailsBeforeMutation()
    {
        var events = new List<string>();
        var plan = CreatePlan(1);
        var validator = new FakeValidator((candidate, _, call) =>
            ValueTask.FromResult(CreateValidation(
                candidate,
                sourceIdentityBase: call == 1 ? 100UL : 900UL)));
        var history = new RecordingHistoryStore(events);
        var mutation = new FakeMutation((item, _) =>
            ValueTask.FromResult(CreateReceipt(item)));
        using var executor = new FileCopyOperationExecutor(validator, history, mutation);

        var result = await executor.ExecuteAsync(plan, new RecordingProgress(events));

        Assert.AreEqual(FileOperationExecutionState.Failed, result.State);
        Assert.AreEqual("EntryRevalidationChanged", result.Failure?.Code);
        Assert.AreEqual(0, mutation.CallCount);
        Assert.IsTrue(events.Contains("history:failed-before:0"));
        Assert.IsTrue(events.Contains("history:complete:Failed"));
        Assert.IsFalse(events.Contains("history:start:0"));
    }

    [TestMethod]
    public async Task ValidatorReturningDifferentPlanInstanceIsRejected()
    {
        var events = new List<string>();
        var plan = CreatePlan(1);
        var validator = new FakeValidator((candidate, _, _) =>
        {
            var cloned = new FileOperationPlan(
                candidate.Id,
                candidate.QueuedAtUtc,
                candidate.Kind,
                candidate.CollisionPolicy,
                candidate.Intent);
            return ValueTask.FromResult(CreateValidation(cloned));
        });
        var history = new RecordingHistoryStore(events);
        var mutation = new FakeMutation((item, _) =>
            ValueTask.FromResult(CreateReceipt(item)));
        using var executor = new FileCopyOperationExecutor(validator, history, mutation);

        var result = await executor.ExecuteAsync(plan, new RecordingProgress(events));

        Assert.AreEqual(FileOperationExecutionState.Failed, result.State);
        Assert.AreEqual("ExecutionValidationBlocked", result.Failure?.Code);
        Assert.AreEqual(0, mutation.CallCount);
        Assert.IsFalse(events.Contains("history:begin"));
    }

    [TestMethod]
    public async Task FreshCollisionChangeFailsInsteadOfRewritingSkipDecision()
    {
        var events = new List<string>();
        var plan = CreatePlan(1, FileOperationCollisionPolicy.Skip);
        var validator = new FakeValidator((candidate, _, call) =>
            ValueTask.FromResult(CreateValidation(
                candidate,
                decisions: call == 1
                    ? new[] { FileOperationExecutionValidationDecision.Ready }
                    : new[] { FileOperationExecutionValidationDecision.Skip })));
        var history = new RecordingHistoryStore(events);
        var mutation = new FakeMutation((item, _) =>
            ValueTask.FromResult(CreateReceipt(item)));
        using var executor = new FileCopyOperationExecutor(validator, history, mutation);

        var result = await executor.ExecuteAsync(plan, new RecordingProgress(events));

        Assert.AreEqual(FileOperationExecutionState.Failed, result.State);
        Assert.AreEqual("EntryRevalidationChanged", result.Failure?.Code);
        Assert.AreEqual(0, mutation.CallCount);
        Assert.IsTrue(events.Contains("history:failed-before:0"));
    }

    [TestMethod]
    public async Task InitialSkippedEntriesNeverReachMutationPrimitive()
    {
        var events = new List<string>();
        var plan = CreatePlan(2, FileOperationCollisionPolicy.Skip);
        var validator = new FakeValidator((candidate, _, _) =>
            ValueTask.FromResult(CreateValidation(
                candidate,
                decisions: Enumerable.Repeat(
                    FileOperationExecutionValidationDecision.Skip,
                    candidate.Intent.Entries.Count).ToArray())));
        var history = new RecordingHistoryStore(events);
        var mutation = new FakeMutation((item, _) =>
            ValueTask.FromResult(CreateReceipt(item)));
        using var executor = new FileCopyOperationExecutor(validator, history, mutation);

        var result = await executor.ExecuteAsync(plan, new RecordingProgress(events));

        Assert.AreEqual(FileOperationExecutionState.Succeeded, result.State);
        Assert.AreEqual(2, result.CompletedEntryCount);
        Assert.AreEqual(1, validator.CallCount);
        Assert.AreEqual(0, mutation.CallCount);
        Assert.IsFalse(events.Any(static value => value.StartsWith("history:start:", StringComparison.Ordinal)));
        Assert.IsTrue(events.Contains("history:complete:Succeeded"));
    }

    [TestMethod]
    public async Task DirectoryAndMovePlansAreRejectedBeforeValidation()
    {
        var events = new List<string>();
        var validator = new FakeValidator((candidate, _, _) =>
            ValueTask.FromResult(CreateValidation(candidate)));
        var history = new RecordingHistoryStore(events);
        var mutation = new FakeMutation((item, _) =>
            ValueTask.FromResult(CreateReceipt(item)));
        using var executor = new FileCopyOperationExecutor(validator, history, mutation);

        var directoryResult = await executor.ExecuteAsync(
            CreatePlan(1, firstIsDirectory: true));
        var moveResult = await executor.ExecuteAsync(
            CreatePlan(1, kind: FileOperationKind.Move));

        Assert.AreEqual(FileOperationExecutionState.Failed, directoryResult.State);
        Assert.AreEqual(FileOperationExecutionState.Failed, moveResult.State);
        Assert.AreEqual("UnsupportedCopyPlan", directoryResult.Failure?.Code);
        Assert.AreEqual("UnsupportedCopyPlan", moveResult.Failure?.Code);
        Assert.AreEqual(0, validator.CallCount);
        Assert.AreEqual(0, mutation.CallCount);
    }

    private static int IndexOf(IReadOnlyList<string> values, string value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] == value)
            {
                return index;
            }
        }

        Assert.Fail($"Event not found: {value}");
        return -1;
    }

    private static FileOperationPlan CreatePlan(
        int entryCount,
        FileOperationCollisionPolicy collisionPolicy = FileOperationCollisionPolicy.Ask,
        bool firstIsDirectory = false,
        FileOperationKind kind = FileOperationKind.Copy)
    {
        var entries = Enumerable.Range(0, entryCount)
            .Select(index => new FileOperationEntry(
                Path.Combine(
                    @"C:\Source",
                    index == 0 && firstIsDirectory ? "Folder" : $"file-{index}.dat"),
                index == 0 && firstIsDirectory ? "Folder" : $"file-{index}.dat",
                IsDirectory: index == 0 && firstIsDirectory))
            .ToArray();
        return new FileOperationPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            kind,
            collisionPolicy,
            new FileOperationIntent(
                "Left",
                Guid.NewGuid(),
                @"C:\Source",
                entries,
                "Right",
                Guid.NewGuid(),
                @"D:\Destination"));
    }

    private static FileOperationExecutionValidationResult CreateValidation(
        FileOperationPlan plan,
        ulong sourceIdentityBase = 100,
        IReadOnlyList<FileOperationExecutionValidationDecision>? decisions = null)
    {
        var sourceRoot = new FileOperationCanonicalPath(
            plan.Intent.SourceDirectoryPath,
            @"C:\Real\Source",
            FileOperationCanonicalPathState.Directory,
            IsLeafReparsePoint: false,
            Identity: new FileIdentity(1, 10));
        var destinationRoot = new FileOperationCanonicalPath(
            plan.Intent.DestinationDirectoryPath,
            @"D:\Real\Destination",
            FileOperationCanonicalPathState.Directory,
            IsLeafReparsePoint: false,
            Identity: new FileIdentity(2, 20));
        var items = plan.Intent.Entries
            .Select((entry, index) =>
            {
                var decision = decisions is null
                    ? FileOperationExecutionValidationDecision.Ready
                    : decisions[index];
                var destinationState = decision == FileOperationExecutionValidationDecision.Skip
                    ? FileOperationCanonicalPathState.File
                    : FileOperationCanonicalPathState.Missing;
                return new FileOperationExecutionValidationItem(
                    entry,
                    new FileOperationCanonicalPath(
                        entry.Path,
                        Path.Combine(sourceRoot.CanonicalPath, entry.Name),
                        entry.IsDirectory
                            ? FileOperationCanonicalPathState.Directory
                            : FileOperationCanonicalPathState.File,
                        IsLeafReparsePoint: false,
                        Identity: new FileIdentity(1, sourceIdentityBase + (ulong)index)),
                    new FileOperationCanonicalPath(
                        Path.Combine(plan.Intent.DestinationDirectoryPath, entry.Name),
                        Path.Combine(destinationRoot.CanonicalPath, entry.Name),
                        destinationState,
                        IsLeafReparsePoint: false,
                        Identity: destinationState == FileOperationCanonicalPathState.File
                            ? new FileIdentity(2, 200UL + (ulong)index)
                            : null),
                    decision,
                    decision == FileOperationExecutionValidationDecision.Skip ? "skip" : "ready");
            })
            .ToArray();
        var status = items.Any(static item =>
                item.Decision == FileOperationExecutionValidationDecision.Blocked)
            ? FileOperationExecutionValidationStatus.Blocked
            : items.Any(static item =>
                item.Decision == FileOperationExecutionValidationDecision.NeedsDecision)
                ? FileOperationExecutionValidationStatus.NeedsDecision
                : FileOperationExecutionValidationStatus.Ready;
        return new FileOperationExecutionValidationResult(
            plan,
            sourceRoot,
            destinationRoot,
            items,
            status,
            DateTimeOffset.UtcNow,
            "test validation");
    }

    private static FileCopyMutationReceipt CreateReceipt(
        FileOperationExecutionValidationItem item)
    {
        Assert.IsTrue(item.Source.Identity.HasValue);
        return new FileCopyMutationReceipt(
            item.Source.CanonicalPath,
            item.Destination.CanonicalPath,
            item.Source.Identity.Value,
            new FileIdentity(3, 500));
    }

    private sealed class FakeValidator : IFileOperationExecutionValidator
    {
        private readonly Func<
            FileOperationPlan,
            CancellationToken,
            int,
            ValueTask<FileOperationExecutionValidationResult>> _callback;

        public FakeValidator(
            Func<FileOperationPlan, CancellationToken, int, ValueTask<FileOperationExecutionValidationResult>> callback)
        {
            _callback = callback;
        }

        public int CallCount { get; private set; }

        public ValueTask<FileOperationExecutionValidationResult> ValidateAsync(
            FileOperationPlan plan,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return _callback(plan, cancellationToken, CallCount);
        }
    }

    private sealed class FakeMutation : IFileCopyMutationPrimitive
    {
        private readonly Func<
            FileOperationExecutionValidationItem,
            int,
            ValueTask<FileCopyMutationReceipt>> _callback;

        public FakeMutation(
            Func<FileOperationExecutionValidationItem, int, ValueTask<FileCopyMutationReceipt>> callback)
        {
            _callback = callback;
        }

        public int CallCount { get; private set; }

        public ValueTask<FileCopyMutationReceipt> CopyNewFileAsync(
            FileOperationExecutionValidationItem validation)
        {
            CallCount++;
            return _callback(validation, CallCount);
        }
    }

    private sealed class RecordingHistoryStore : IFileOperationActionHistoryStore
    {
        private readonly List<string> _events;
        private FileOperationActionHistory? _history;

        public RecordingHistoryStore(List<string> events)
        {
            _events = events;
        }

        public bool ThrowOnCommit { get; set; }

        public ValueTask<FileOperationActionHistory> BeginAsync(
            FileOperationExecutionValidationResult validation,
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken = default)
        {
            _events.Add("history:begin");
            var entries = validation.Items
                .Select((item, ordinal) => new FileOperationActionEntry(
                    ordinal,
                    item.Entry,
                    item.Source.CanonicalPath,
                    item.Destination.CanonicalPath,
                    item.Decision == FileOperationExecutionValidationDecision.Skip
                        ? FileOperationActionEntryState.Skipped
                        : FileOperationActionEntryState.Pending,
                    MutationStartedAtUtc: null,
                    CompletedAtUtc: item.Decision == FileOperationExecutionValidationDecision.Skip
                        ? startedAtUtc
                        : null,
                    item.Source.Identity,
                    item.Destination.Identity,
                    FileOperationUndoKind.None,
                    Failure: null))
                .ToArray();
            _history = new FileOperationActionHistory(
                validation.Plan.Id,
                validation.Plan.QueuedAtUtc,
                validation.ValidatedAtUtc,
                startedAtUtc,
                CompletedAtUtc: null,
                validation.Plan.Kind,
                validation.Plan.CollisionPolicy,
                validation.Plan.Intent.SourceDirectoryPath,
                validation.Plan.Intent.DestinationDirectoryPath,
                validation.SourceDirectory.CanonicalPath,
                validation.DestinationDirectory.CanonicalPath,
                TerminalState: null,
                entries);
            return ValueTask.FromResult(_history);
        }

        public ValueTask<FileOperationActionHistory> MarkMutationStartedAsync(
            Guid operationId,
            int ordinal,
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken = default)
        {
            _events.Add($"history:start:{ordinal}");
            return Current();
        }

        public ValueTask<FileOperationActionHistory> MarkEntryFailedBeforeMutationAsync(
            Guid operationId,
            int ordinal,
            FileOperationFailure failure,
            DateTimeOffset failedAtUtc,
            CancellationToken cancellationToken = default)
        {
            _events.Add($"history:failed-before:{ordinal}");
            return Current();
        }

        public ValueTask<FileOperationActionHistory> CommitCopyAsync(
            Guid operationId,
            int ordinal,
            FileIdentity destinationIdentity,
            DateTimeOffset committedAtUtc,
            CancellationToken cancellationToken = default)
        {
            _events.Add($"history:commit:{ordinal}");
            if (ThrowOnCommit)
            {
                throw new IOException("history commit failed");
            }

            return Current();
        }

        public ValueTask<FileOperationActionHistory> MarkMutationRecoveryRequiredAsync(
            Guid operationId,
            int ordinal,
            FileOperationFailure failure,
            DateTimeOffset failedAtUtc,
            CancellationToken cancellationToken = default)
        {
            _events.Add($"history:recovery:{ordinal}");
            return Current();
        }

        public ValueTask<FileOperationActionHistory> CompleteAsync(
            Guid operationId,
            FileOperationActionTerminalState terminalState,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken = default)
        {
            _events.Add($"history:complete:{terminalState}");
            return Current();
        }

        public ValueTask<FileOperationActionHistory?> GetAsync(
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_history);

        public ValueTask<IReadOnlyList<FileOperationActionHistory>> GetRecentAsync(
            int limit = 100,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<FileOperationActionHistory>>(
                _history is null
                    ? Array.Empty<FileOperationActionHistory>()
                    : new[] { _history });

        private ValueTask<FileOperationActionHistory> Current() =>
            ValueTask.FromResult(
                _history ?? throw new InvalidOperationException("History has not begun."));
    }

    private sealed class RecordingProgress : IProgress<FileOperationExecutionSnapshot>
    {
        private readonly List<string> _events;

        public RecordingProgress(List<string> events)
        {
            _events = events;
        }

        public void Report(FileOperationExecutionSnapshot value) =>
            _events.Add($"progress:{value.State}:{value.CompletedEntryCount}");
    }
}
