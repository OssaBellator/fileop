using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;

namespace FileOp.Core.Operations;

public sealed record FileSameVolumeMoveMutationReceipt(
    string CanonicalSourcePath,
    string CanonicalDestinationPath,
    FileIdentity SourceIdentity,
    FileIdentity DestinationIdentity);

public sealed record FileSameVolumeMoveMutationRequest(
    FileOperationExecutionValidationItem Item,
    FileOperationCanonicalPath SourceDirectory,
    FileOperationCanonicalPath DestinationDirectory);

/// <summary>
/// Keeps the identity-binding source/destination handles alive through durable Move commit.
/// </summary>
public interface IFileSameVolumeMoveMutationLease : IAsyncDisposable
{
    FileSameVolumeMoveMutationReceipt Receipt { get; }
}

public interface IFileSameVolumeMoveMutationPrimitive
{
    ValueTask<IFileSameVolumeMoveMutationLease> RenameFileAsync(
        FileSameVolumeMoveMutationRequest request);
}

/// <summary>
/// Executes regular-file Move only when execution-grade evidence proves the operation
/// is a same-volume identity-preserving rename. Cross-volume Move is deliberately refused.
/// </summary>
public sealed class FileSameVolumeMoveOperationExecutor : IFileOperationExecutor
{
    private readonly IFileOperationExecutionValidator _validator;
    private readonly IFileMoveOperationActionHistoryStore _historyStore;
    private readonly IFileSameVolumeMoveMutationPrimitive _mutation;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly object _activeLock = new();
    private ActiveExecution? _active;

    public FileSameVolumeMoveOperationExecutor(
        IFileOperationExecutionValidator validator,
        IFileMoveOperationActionHistoryStore historyStore,
        IFileSameVolumeMoveMutationPrimitive mutation,
        TimeProvider? timeProvider = null)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        _mutation = mutation ?? throw new ArgumentNullException(nameof(mutation));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<FileOperationExecutionSnapshot> ExecuteAsync(
        FileOperationPlan plan,
        IProgress<FileOperationExecutionSnapshot>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await _executionGate.WaitAsync().ConfigureAwait(false);
        var context = new ActiveExecution(plan.Id);
        lock (_activeLock)
        {
            _active = context;
        }

        try
        {
            return await ExecuteCoreAsync(plan, progress, context).ConfigureAwait(false);
        }
        finally
        {
            lock (_activeLock)
            {
                if (ReferenceEquals(_active, context))
                {
                    _active = null;
                }
            }

            context.Dispose();
            _executionGate.Release();
        }
    }

    public ValueTask<bool> RequestCancellationAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_activeLock)
        {
            if (_active is null || _active.OperationId != operationId)
            {
                return ValueTask.FromResult(false);
            }

            _active.RequestCancellation();
            return ValueTask.FromResult(true);
        }
    }

    private async ValueTask<FileOperationExecutionSnapshot> ExecuteCoreAsync(
        FileOperationPlan plan,
        IProgress<FileOperationExecutionSnapshot>? progress,
        ActiveExecution context)
    {
        var snapshot = FileOperationExecutionSnapshot.CreatePlanned(plan);
        Report(progress, snapshot);
        snapshot = snapshot.BeginValidation();
        Report(progress, snapshot);

        var unsupported = ValidateSupportedPlan(plan);
        if (unsupported is not null)
        {
            return FailAndReport(
                progress,
                snapshot,
                new FileOperationFailure("UnsupportedMovePlan", unsupported, null, false));
        }

        FileOperationExecutionValidationResult validation;
        try
        {
            validation = await _validator
                .ValidateAsync(plan, context.ValidationCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.IsCancellationRequested)
        {
            return CancelBeforeHistory(progress, snapshot);
        }
        catch (Exception exception)
        {
            return FailAndReport(
                progress,
                snapshot,
                FailureFromException(
                    "MoveExecutionValidationFailed",
                    "Move execution validation could not complete.",
                    null,
                    exception,
                    retryable: true));
        }

        if (context.IsCancellationRequested)
        {
            return CancelBeforeHistory(progress, snapshot);
        }

        if (!TryValidateInitialResult(plan, validation, out var initialProblem))
        {
            return FailAndReport(
                progress,
                snapshot,
                new FileOperationFailure(
                    "MoveExecutionValidationBlocked",
                    initialProblem,
                    null,
                    Retryable: true));
        }

        try
        {
            await _historyStore.BeginAsync(validation, UtcNow()).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return FailAndReport(
                progress,
                snapshot,
                FailureFromException(
                    "MoveActionHistoryBeginFailed",
                    "Durable Move action history could not begin; no rename was attempted.",
                    null,
                    exception,
                    retryable: true));
        }

        if (context.IsCancellationRequested)
        {
            try
            {
                await _historyStore
                    .CompleteAsync(plan.Id, FileOperationActionTerminalState.Cancelled, UtcNow())
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return FailAndReport(
                    progress,
                    snapshot,
                    FailureFromException(
                        "MoveActionHistoryCancellationFailed",
                        "Move was cancelled before mutation, but durable history could not be finalized.",
                        null,
                        exception,
                        retryable: true));
            }

            return CancelBeforeHistory(progress, snapshot);
        }

        snapshot = snapshot.BeginRunning();
        Report(progress, snapshot);

        for (var ordinal = 0; ordinal < validation.Items.Count; ordinal++)
        {
            var initialItem = validation.Items[ordinal];
            if (initialItem.Decision == FileOperationExecutionValidationDecision.Skip)
            {
                snapshot = snapshot.ReportProgress(ordinal + 1, initialItem.Entry.Path);
                Report(progress, snapshot);
                if (context.IsCancellationRequested)
                {
                    return await SettleRunningCancellationAsync(plan.Id, progress, snapshot)
                        .ConfigureAwait(false);
                }

                continue;
            }

            if (context.IsCancellationRequested)
            {
                return await SettleRunningCancellationAsync(plan.Id, progress, snapshot)
                    .ConfigureAwait(false);
            }

            var freshPlan = CreateSingleEntryPlan(plan, initialItem.Entry);
            FileOperationExecutionValidationResult freshValidation;
            try
            {
                freshValidation = await _validator
                    .ValidateAsync(freshPlan, context.ValidationCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.IsCancellationRequested)
            {
                return await SettleRunningCancellationAsync(plan.Id, progress, snapshot)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return await FailBeforeMutationAsync(
                    plan.Id,
                    ordinal,
                    progress,
                    snapshot,
                    FailureFromException(
                        "MoveEntryRevalidationFailed",
                        "The source file could not be revalidated immediately before rename.",
                        initialItem.Entry.Path,
                        exception,
                        retryable: true)).ConfigureAwait(false);
            }

            if (!TryValidateFreshResult(
                validation,
                initialItem,
                freshPlan,
                freshValidation,
                out var freshItem,
                out var freshProblem))
            {
                return await FailBeforeMutationAsync(
                    plan.Id,
                    ordinal,
                    progress,
                    snapshot,
                    new FileOperationFailure(
                        "MoveEntryRevalidationChanged",
                        freshProblem,
                        initialItem.Entry.Path,
                        Retryable: true)).ConfigureAwait(false);
            }

            if (context.IsCancellationRequested)
            {
                return await SettleRunningCancellationAsync(plan.Id, progress, snapshot)
                    .ConfigureAwait(false);
            }

            try
            {
                await _historyStore
                    .MarkMutationStartedAsync(plan.Id, ordinal, UtcNow())
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await BestEffortCompleteAsync(plan.Id, FileOperationActionTerminalState.Failed)
                    .ConfigureAwait(false);
                return FailAndReport(
                    progress,
                    snapshot,
                    FailureFromException(
                        "MoveMutationBarrierFailed",
                        "The durable Move MutationStarted barrier could not be recorded; no rename was attempted.",
                        freshItem.Source.CanonicalPath,
                        exception,
                        retryable: true));
            }

            IFileSameVolumeMoveMutationLease? mutationLease = null;
            try
            {
                // Cancellation is intentionally not passed beyond MutationStarted.
                // The lease retains the exact source/destination identity-binding
                // handles until durable Move commit has completed.
                mutationLease = await _mutation
                    .RenameFileAsync(new FileSameVolumeMoveMutationRequest(
                        freshItem,
                        freshValidation.SourceDirectory,
                        freshValidation.DestinationDirectory))
                    .ConfigureAwait(false);
                if (mutationLease is null)
                {
                    return await FailAfterMutationAsync(
                        plan.Id,
                        ordinal,
                        progress,
                        snapshot,
                        new FileOperationFailure(
                            "MoveMutationLeaseMissing",
                            "The rename primitive returned no identity-binding lease after MutationStarted.",
                            freshItem.Destination.CanonicalPath,
                            Retryable: false),
                        observedDestinationIdentity: null).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                return await FailAfterMutationAsync(
                    plan.Id,
                    ordinal,
                    progress,
                    snapshot,
                    FailureFromException(
                        "MoveMutationFailed",
                        "The same-volume rename failed after the durable mutation barrier was crossed.",
                        freshItem.Destination.CanonicalPath,
                        exception,
                        retryable: false),
                    observedDestinationIdentity: null).ConfigureAwait(false);
            }

            try
            {
                FileSameVolumeMoveMutationReceipt receipt;
                try
                {
                    receipt = mutationLease.Receipt;
                    if (!TryValidateMutationReceipt(freshItem, receipt, out var receiptProblem))
                    {
                        return await FailAfterMutationAsync(
                            plan.Id,
                            ordinal,
                            progress,
                            snapshot,
                            new FileOperationFailure(
                                "MoveMutationReceiptInvalid",
                                receiptProblem,
                                freshItem.Destination.CanonicalPath,
                                Retryable: false),
                            observedDestinationIdentity: null).ConfigureAwait(false);
                    }
                }
                catch (Exception exception)
                {
                    return await FailAfterMutationAsync(
                        plan.Id,
                        ordinal,
                        progress,
                        snapshot,
                        FailureFromException(
                            "MoveMutationReceiptInvalid",
                            "The rename primitive could not provide a valid identity-bound receipt.",
                            freshItem.Destination.CanonicalPath,
                            exception,
                            retryable: false),
                        observedDestinationIdentity: null).ConfigureAwait(false);
                }

                try
                {
                    await _historyStore
                        .CommitSameVolumeMoveAsync(
                            plan.Id,
                            ordinal,
                            receipt.DestinationIdentity,
                            UtcNow())
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    return await FailAfterMutationAsync(
                        plan.Id,
                        ordinal,
                        progress,
                        snapshot,
                        FailureFromException(
                            "MoveCommitBarrierFailed",
                            "The file was renamed but its durable Move commit could not be finalized.",
                            receipt.CanonicalDestinationPath,
                            exception,
                            retryable: false),
                        observedDestinationIdentity: receipt.DestinationIdentity).ConfigureAwait(false);
                }

                snapshot = snapshot.ReportProgress(ordinal + 1, receipt.CanonicalDestinationPath);
                Report(progress, snapshot);
            }
            finally
            {
                await DisposeMutationLeaseAsync(mutationLease).ConfigureAwait(false);
            }

            if (context.IsCancellationRequested)
            {
                return await SettleRunningCancellationAsync(plan.Id, progress, snapshot)
                    .ConfigureAwait(false);
            }
        }

        try
        {
            await _historyStore
                .CompleteAsync(plan.Id, FileOperationActionTerminalState.Succeeded, UtcNow())
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return FailAndReport(
                progress,
                snapshot,
                FailureFromException(
                    "MoveActionHistoryFinalizeFailed",
                    "Every Move entry reached a durable state, but operation history could not be marked succeeded.",
                    null,
                    exception,
                    retryable: true));
        }

        snapshot = snapshot.Complete();
        Report(progress, snapshot);
        return snapshot;
    }

    private async ValueTask<FileOperationExecutionSnapshot> SettleRunningCancellationAsync(
        Guid operationId,
        IProgress<FileOperationExecutionSnapshot>? progress,
        FileOperationExecutionSnapshot snapshot)
    {
        var requested = snapshot.RequestCancellation();
        Report(progress, requested);
        var terminalState = requested.CompletedEntryCount == requested.TotalEntryCount
            ? FileOperationActionTerminalState.Succeeded
            : FileOperationActionTerminalState.Cancelled;

        try
        {
            await _historyStore
                .CompleteAsync(operationId, terminalState, UtcNow())
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return FailAndReport(
                progress,
                requested,
                FailureFromException(
                    "MoveActionHistoryCancellationFailed",
                    "Move cancellation reached a safe boundary, but durable history could not be finalized.",
                    null,
                    exception,
                    retryable: true));
        }

        var settled = requested.CancelAtSafeBoundary();
        Report(progress, settled);
        return settled;
    }

    private async ValueTask<FileOperationExecutionSnapshot> FailBeforeMutationAsync(
        Guid operationId,
        int ordinal,
        IProgress<FileOperationExecutionSnapshot>? progress,
        FileOperationExecutionSnapshot snapshot,
        FileOperationFailure failure)
    {
        try
        {
            await _historyStore
                .MarkEntryFailedBeforeMutationAsync(operationId, ordinal, failure, UtcNow())
                .ConfigureAwait(false);
            await _historyStore
                .CompleteAsync(operationId, FileOperationActionTerminalState.Failed, UtcNow())
                .ConfigureAwait(false);
        }
        catch (Exception historyException)
        {
            failure = FailureFromException(
                "MoveActionHistoryFailureRecordFailed",
                failure.Message + " The pre-mutation Move failure could not be fully persisted.",
                failure.Path,
                historyException,
                retryable: true);
        }

        return FailAndReport(progress, snapshot, failure);
    }

    private async ValueTask<FileOperationExecutionSnapshot> FailAfterMutationAsync(
        Guid operationId,
        int ordinal,
        IProgress<FileOperationExecutionSnapshot>? progress,
        FileOperationExecutionSnapshot snapshot,
        FileOperationFailure failure,
        FileIdentity? observedDestinationIdentity)
    {
        var recoveryFinalized = false;
        try
        {
            await _historyStore
                .MarkSameVolumeMoveRecoveryRequiredAsync(
                    operationId,
                    ordinal,
                    failure,
                    UtcNow(),
                    observedDestinationIdentity)
                .ConfigureAwait(false);
        }
        catch
        {
            // MutationStarted remains the durable restart-time recovery signal.
        }

        try
        {
            await _historyStore
                .CompleteAsync(operationId, FileOperationActionTerminalState.RecoveryRequired, UtcNow())
                .ConfigureAwait(false);
            recoveryFinalized = true;
        }
        catch
        {
            // Keep original failure; MutationStarted remains recovery-sensitive.
        }

        if (!recoveryFinalized)
        {
            failure = failure with
            {
                Message = failure.Message +
                    " Durable Move recovery finalization also failed; MutationStarted remains recovery-sensitive.",
            };
        }

        return FailAndReport(progress, snapshot, failure);
    }

    private async ValueTask BestEffortCompleteAsync(
        Guid operationId,
        FileOperationActionTerminalState terminalState)
    {
        try
        {
            await _historyStore.CompleteAsync(operationId, terminalState, UtcNow())
                .ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static async ValueTask DisposeMutationLeaseAsync(
        IFileSameVolumeMoveMutationLease mutationLease)
    {
        try
        {
            await mutationLease.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // SafeHandle-backed leases should not throw. Disposal cannot rewrite
            // an already durable Move state.
        }
    }

    private static string? ValidateSupportedPlan(FileOperationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan.Intent);
        ArgumentNullException.ThrowIfNull(plan.Intent.Entries);
        if (plan.Kind != FileOperationKind.Move)
        {
            return "This executor supports Move plans only.";
        }

        if (plan.Intent.Entries.Count == 0)
        {
            return "A Move plan must contain at least one file entry.";
        }

        if (plan.Intent.Entries.Any(static entry => entry.IsDirectory))
        {
            return "Directory Move is not supported by the same-volume file Move executor.";
        }

        return null;
    }

    private static bool TryValidateInitialResult(
        FileOperationPlan plan,
        FileOperationExecutionValidationResult validation,
        out string problem)
    {
        if (!ReferenceEquals(validation.Plan, plan) ||
            validation.Items.Count != plan.Intent.Entries.Count)
        {
            problem = "Execution validation did not describe the requested immutable Move plan instance.";
            return false;
        }

        var strategy = FileMoveExecutionStrategyClassifier.Classify(validation);
        if (strategy.Strategy is not FileMoveExecutionStrategy.SameVolumeRenameRequired and
            not FileMoveExecutionStrategy.SkipOnly)
        {
            problem = strategy.Summary;
            return false;
        }

        for (var ordinal = 0; ordinal < validation.Items.Count; ordinal++)
        {
            var item = validation.Items[ordinal];
            if (item.Entry != plan.Intent.Entries[ordinal] ||
                item.Entry.IsDirectory ||
                item.Decision is not FileOperationExecutionValidationDecision.Ready and
                    not FileOperationExecutionValidationDecision.Skip)
            {
                problem = "Move validation changed entry ordering/type or returned an unsupported decision.";
                return false;
            }
        }

        problem = string.Empty;
        return true;
    }

    private static bool TryValidateFreshResult(
        FileOperationExecutionValidationResult initialValidation,
        FileOperationExecutionValidationItem initialItem,
        FileOperationPlan freshPlan,
        FileOperationExecutionValidationResult freshValidation,
        out FileOperationExecutionValidationItem freshItem,
        out string problem)
    {
        freshItem = freshValidation.Items.Count == 1
            ? freshValidation.Items[0]
            : initialItem;

        var freshStrategy = FileMoveExecutionStrategyClassifier.Classify(freshValidation);
        if (!ReferenceEquals(freshValidation.Plan, freshPlan) ||
            freshStrategy.Strategy != FileMoveExecutionStrategy.SameVolumeRenameRequired ||
            freshValidation.Items.Count != 1 ||
            freshItem.Entry != initialItem.Entry ||
            freshItem.Decision != FileOperationExecutionValidationDecision.Ready ||
            freshItem.Destination.State != FileOperationCanonicalPathState.Missing)
        {
            problem = "The Move entry's validation/strategy changed after durable history began.";
            return false;
        }

        if (!SameCanonicalObject(initialValidation.SourceDirectory, freshValidation.SourceDirectory) ||
            !SameCanonicalObject(initialValidation.DestinationDirectory, freshValidation.DestinationDirectory))
        {
            problem = "A canonical Move source/destination root changed after the operation began.";
            return false;
        }

        if (!PathsEqual(initialItem.Source.CanonicalPath, freshItem.Source.CanonicalPath) ||
            initialItem.Source.Identity is not FileIdentity initialIdentity ||
            freshItem.Source.Identity is not FileIdentity freshIdentity ||
            initialIdentity != freshIdentity)
        {
            problem = "The Move source file's canonical path or stable identity changed before rename.";
            return false;
        }

        if (!PathsEqual(initialItem.Destination.CanonicalPath, freshItem.Destination.CanonicalPath))
        {
            problem = "The Move destination leaf's canonical path changed before rename.";
            return false;
        }

        problem = string.Empty;
        return true;
    }

    private static bool TryValidateMutationReceipt(
        FileOperationExecutionValidationItem item,
        FileSameVolumeMoveMutationReceipt receipt,
        out string problem)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!PathsEqual(receipt.CanonicalSourcePath, item.Source.CanonicalPath) ||
            !PathsEqual(receipt.CanonicalDestinationPath, item.Destination.CanonicalPath))
        {
            problem = "The rename primitive reported canonical paths different from validation.";
            return false;
        }

        if (item.Source.Identity is not FileIdentity expected ||
            receipt.SourceIdentity != expected ||
            receipt.DestinationIdentity != expected)
        {
            problem = "The rename primitive did not preserve the exact validated source object identity.";
            return false;
        }

        problem = string.Empty;
        return true;
    }

    private static bool SameCanonicalObject(
        FileOperationCanonicalPath initial,
        FileOperationCanonicalPath fresh) =>
        PathsEqual(initial.CanonicalPath, fresh.CanonicalPath) &&
        initial.Identity is FileIdentity initialIdentity &&
        fresh.Identity is FileIdentity freshIdentity &&
        initialIdentity == freshIdentity;

    private static FileOperationPlan CreateSingleEntryPlan(
        FileOperationPlan plan,
        FileOperationEntry entry) =>
        new(
            plan.Id,
            plan.QueuedAtUtc,
            plan.Kind,
            plan.CollisionPolicy,
            new FileOperationIntent(
                plan.Intent.SourcePane,
                plan.Intent.SourceTabId,
                plan.Intent.SourceDirectoryPath,
                new[] { entry },
                plan.Intent.DestinationPane,
                plan.Intent.DestinationTabId,
                plan.Intent.DestinationDirectoryPath));

    private static FileOperationExecutionSnapshot CancelBeforeHistory(
        IProgress<FileOperationExecutionSnapshot>? progress,
        FileOperationExecutionSnapshot snapshot)
    {
        var cancelled = snapshot.RequestCancellation();
        Report(progress, cancelled);
        return cancelled;
    }

    private static FileOperationExecutionSnapshot FailAndReport(
        IProgress<FileOperationExecutionSnapshot>? progress,
        FileOperationExecutionSnapshot snapshot,
        FileOperationFailure failure)
    {
        var failed = snapshot.Fail(failure);
        Report(progress, failed);
        return failed;
    }

    private static FileOperationFailure FailureFromException(
        string code,
        string prefix,
        string? path,
        Exception exception,
        bool retryable) =>
        new(code, $"{prefix} {exception.Message}", path, retryable);

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();

    private static bool PathsEqual(string left, string right) =>
        string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path)
    {
        var normalized = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalized.Length == 2 && normalized[1] == Path.VolumeSeparatorChar)
        {
            normalized += Path.DirectorySeparatorChar;
        }

        return normalized;
    }

    private static void Report(
        IProgress<FileOperationExecutionSnapshot>? progress,
        FileOperationExecutionSnapshot snapshot)
    {
        try
        {
            progress?.Report(snapshot);
        }
        catch
        {
            // Progress is advisory and must never compromise durable execution state.
        }
    }

    private sealed class ActiveExecution : IDisposable
    {
        private int _cancellationRequested;

        public ActiveExecution(Guid operationId) => OperationId = operationId;

        public Guid OperationId { get; }

        public CancellationTokenSource ValidationCancellation { get; } = new();

        public bool IsCancellationRequested => Volatile.Read(ref _cancellationRequested) != 0;

        public void RequestCancellation()
        {
            if (Interlocked.Exchange(ref _cancellationRequested, 1) == 0)
            {
                ValidationCancellation.Cancel();
            }
        }

        public void Dispose() => ValidationCancellation.Dispose();
    }
}
