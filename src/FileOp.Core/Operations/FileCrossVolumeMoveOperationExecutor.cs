using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;

namespace FileOp.Core.Operations;

/// <summary>
/// Executes regular-file cross-volume Move as two separately durable mutation phases:
/// exclusive-create Copy first, then identity-bound source deletion. DestinationCommitted is
/// a safe cancellation boundary: the source still exists and no delete authority has crossed.
/// </summary>
public sealed class FileCrossVolumeMoveOperationExecutor : IFileOperationExecutor
{
    private readonly IFileOperationExecutionValidator _validator;
    private readonly IFileCrossVolumeMoveActionHistoryStore _historyStore;
    private readonly IFileCopyMutationPrimitive _copyMutation;
    private readonly IFileCrossVolumeMoveSourceDeletePrimitive _sourceDeleteMutation;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly object _activeLock = new();
    private ActiveExecution? _active;

    public FileCrossVolumeMoveOperationExecutor(
        IFileOperationExecutionValidator validator,
        IFileCrossVolumeMoveActionHistoryStore historyStore,
        IFileCopyMutationPrimitive copyMutation,
        IFileCrossVolumeMoveSourceDeletePrimitive sourceDeleteMutation,
        TimeProvider? timeProvider = null)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        _copyMutation = copyMutation ?? throw new ArgumentNullException(nameof(copyMutation));
        _sourceDeleteMutation = sourceDeleteMutation ??
            throw new ArgumentNullException(nameof(sourceDeleteMutation));
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
                new FileOperationFailure("UnsupportedCrossVolumeMovePlan", unsupported, null, false));
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
                    "CrossVolumeMoveValidationFailed",
                    "Cross-volume Move execution validation could not complete.",
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
                    "CrossVolumeMoveValidationBlocked",
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
                    "CrossVolumeMoveHistoryBeginFailed",
                    "The composite Move journal could not begin; no filesystem mutation was attempted.",
                    null,
                    exception,
                    retryable: true));
        }

        if (context.IsCancellationRequested)
        {
            return await SettleCancellationAsync(plan.Id, progress, snapshot)
                .ConfigureAwait(false);
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
                    return await SettleCancellationAsync(plan.Id, progress, snapshot)
                        .ConfigureAwait(false);
                }
                continue;
            }

            if (context.IsCancellationRequested)
            {
                return await SettleCancellationAsync(plan.Id, progress, snapshot)
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
                return await SettleCancellationAsync(plan.Id, progress, snapshot)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return await FailSafeEntryAsync(
                    plan.Id,
                    ordinal,
                    progress,
                    snapshot,
                    FailureFromException(
                        "CrossVolumeMoveEntryRevalidationFailed",
                        "The source/destination could not be revalidated before Copy.",
                        initialItem.Entry.Path,
                        exception,
                        retryable: true)).ConfigureAwait(false);
            }

            if (!TryValidateFreshCopyResult(
                validation,
                initialItem,
                freshPlan,
                freshValidation,
                out var freshItem,
                out var freshProblem))
            {
                return await FailSafeEntryAsync(
                    plan.Id,
                    ordinal,
                    progress,
                    snapshot,
                    new FileOperationFailure(
                        "CrossVolumeMoveEntryRevalidationChanged",
                        freshProblem,
                        initialItem.Entry.Path,
                        Retryable: true)).ConfigureAwait(false);
            }

            if (context.IsCancellationRequested)
            {
                return await SettleCancellationAsync(plan.Id, progress, snapshot)
                    .ConfigureAwait(false);
            }

            try
            {
                await _historyStore
                    .MarkCopyMutationStartedAsync(plan.Id, ordinal, UtcNow())
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return await FailSafeEntryAsync(
                    plan.Id,
                    ordinal,
                    progress,
                    snapshot,
                    FailureFromException(
                        "CrossVolumeMoveCopyBarrierFailed",
                        "The durable CopyMutationStarted barrier could not be recorded; no Copy was attempted.",
                        freshItem.Source.CanonicalPath,
                        exception,
                        retryable: true)).ConfigureAwait(false);
            }

            IFileCopyMutationLease? copyLease = null;
            FileCopyMutationReceipt? receipt = null;
            try
            {
                // Cancellation stops at the durable CopyMutationStarted barrier. Copy and
                // destination commit must settle together before another safe boundary exists.
                copyLease = await _copyMutation
                    .CopyNewFileAsync(new FileCopyMutationRequest(
                        freshItem,
                        freshValidation.SourceDirectory,
                        freshValidation.DestinationDirectory))
                    .ConfigureAwait(false);
                if (copyLease is null)
                {
                    throw new InvalidOperationException(
                        "The Copy primitive returned no identity-binding lease.");
                }

                receipt = copyLease.Receipt;
                if (!TryValidateCopyReceipt(freshItem, freshValidation, receipt, out var receiptProblem))
                {
                    return await FailCopyBarrierAsync(
                        plan.Id,
                        ordinal,
                        progress,
                        snapshot,
                        new FileOperationFailure(
                            "CrossVolumeMoveCopyReceiptInvalid",
                            receiptProblem,
                            freshItem.Destination.CanonicalPath,
                            Retryable: false),
                        receipt).ConfigureAwait(false);
                }

                try
                {
                    await _historyStore
                        .CommitDestinationAsync(
                            plan.Id,
                            ordinal,
                            receipt.DestinationIdentity,
                            receipt.DestinationContentFingerprint!,
                            UtcNow())
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    return await FailCopyBarrierAsync(
                        plan.Id,
                        ordinal,
                        progress,
                        snapshot,
                        FailureFromException(
                            "CrossVolumeMoveDestinationCommitFailed",
                            "The Copy completed but durable destination commit could not be proven.",
                            receipt.CanonicalDestinationPath,
                            exception,
                            retryable: false),
                        receipt).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (receipt is null)
            {
                return await FailCopyBarrierAsync(
                    plan.Id,
                    ordinal,
                    progress,
                    snapshot,
                    FailureFromException(
                        "CrossVolumeMoveCopyFailed",
                        "The Copy phase failed after its durable mutation barrier.",
                        freshItem.Destination.CanonicalPath,
                        exception,
                        retryable: false),
                    receipt: null).ConfigureAwait(false);
            }
            finally
            {
                if (copyLease is not null)
                {
                    await DisposeCopyLeaseAsync(copyLease).ConfigureAwait(false);
                }
            }

            // DestinationCommitted is deliberately a cancellation-safe checkpoint.
            if (context.IsCancellationRequested)
            {
                return await SettleCancellationAsync(plan.Id, progress, snapshot)
                    .ConfigureAwait(false);
            }

            IFileCrossVolumeMoveSourceDeleteLease? sourceDeleteLease = null;
            FileCrossVolumeMoveSourceDeleteRequest sourceDeleteRequest;
            try
            {
                sourceDeleteRequest = CreateSourceDeleteRequest(
                    plan.Id,
                    ordinal,
                    freshItem,
                    freshValidation,
                    receipt!);
                sourceDeleteLease = await _sourceDeleteMutation
                    .AcquireAsync(sourceDeleteRequest, context.ValidationCancellation.Token)
                    .ConfigureAwait(false);
                if (sourceDeleteLease is null || !sourceDeleteLease.DeleteAccessCapabilityHeld)
                {
                    throw new InvalidOperationException(
                        "The source-delete primitive returned no live DELETE-capable evidence lease.");
                }
                ValidateSourceDeleteEvidence(sourceDeleteRequest, sourceDeleteLease.Evidence);
            }
            catch (OperationCanceledException) when (context.IsCancellationRequested)
            {
                if (sourceDeleteLease is not null)
                {
                    await DisposeSourceDeleteLeaseAsync(sourceDeleteLease).ConfigureAwait(false);
                }
                return await SettleCancellationAsync(plan.Id, progress, snapshot)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (sourceDeleteLease is not null)
                {
                    await DisposeSourceDeleteLeaseAsync(sourceDeleteLease).ConfigureAwait(false);
                }
                return await FailSafeEntryAsync(
                    plan.Id,
                    ordinal,
                    progress,
                    snapshot,
                    FailureFromException(
                        "CrossVolumeMoveSourceDeletePreparationFailed",
                        "The copied destination remains committed and the original source was retained because final source-delete capability could not be proven.",
                        freshItem.Source.CanonicalPath,
                        exception,
                        retryable: true)).ConfigureAwait(false);
            }

            if (context.IsCancellationRequested)
            {
                await DisposeSourceDeleteLeaseAsync(sourceDeleteLease).ConfigureAwait(false);
                return await SettleCancellationAsync(plan.Id, progress, snapshot)
                    .ConfigureAwait(false);
            }

            FileCrossVolumeMoveActionHistory barrierHistory;
            try
            {
                barrierHistory = await _historyStore
                    .MarkSourceDeleteStartedAsync(plan.Id, ordinal, UtcNow())
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await DisposeSourceDeleteLeaseAsync(sourceDeleteLease).ConfigureAwait(false);
                return await ResolveSourceDeleteBarrierFailureAsync(
                    plan.Id,
                    ordinal,
                    progress,
                    snapshot,
                    FailureFromException(
                        "CrossVolumeMoveSourceDeleteBarrierFailed",
                        "The durable source-delete barrier could not be confirmed; no source delete call was issued.",
                        freshItem.Source.CanonicalPath,
                        exception,
                        retryable: false)).ConfigureAwait(false);
            }

            FileCrossVolumeMoveSourceDeleteAuthorization authorization;
            try
            {
                authorization = new FileCrossVolumeMoveSourceDeleteAuthorization(
                    sourceDeleteLease.Evidence,
                    barrierHistory);
            }
            catch (Exception exception)
            {
                await DisposeSourceDeleteLeaseAsync(sourceDeleteLease).ConfigureAwait(false);
                return await FailSourceDeleteBarrierAsync(
                    plan.Id,
                    ordinal,
                    progress,
                    snapshot,
                    FailureFromException(
                        "CrossVolumeMoveSourceDeleteAuthorizationFailed",
                        "The source-delete barrier could not mint exact live capability authority.",
                        freshItem.Source.CanonicalPath,
                        exception,
                        retryable: false)).ConfigureAwait(false);
            }

            try
            {
                // No cancellation is passed beyond SourceDeleteStarted. The exact live
                // source/destination lease must reach delete/release/commit or recovery.
                await sourceDeleteLease
                    .MarkDeletePendingAsync(authorization, CancellationToken.None)
                    .ConfigureAwait(false);
                if (!sourceDeleteLease.SourceDeleteMutationPerformed)
                {
                    throw new InvalidOperationException(
                        "The authorized cross-volume Move source-delete primitive returned without reporting that the exact source disposition was performed.");
                }
            }
            catch (Exception exception)
            {
                await DisposeSourceDeleteLeaseAsync(sourceDeleteLease).ConfigureAwait(false);
                return await FailSourceDeleteBarrierAsync(
                    plan.Id,
                    ordinal,
                    progress,
                    snapshot,
                    FailureFromException(
                        "CrossVolumeMoveSourceDeleteFailed",
                        "The same-handle source-delete mutation failed after the durable delete barrier.",
                        freshItem.Source.CanonicalPath,
                        exception,
                        retryable: false)).ConfigureAwait(false);
            }

            try
            {
                await sourceDeleteLease.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return await FailSourceDeleteBarrierAsync(
                    plan.Id,
                    ordinal,
                    progress,
                    snapshot,
                    FailureFromException(
                        "CrossVolumeMoveSourceDeleteReleaseFailed",
                        "The source was marked for deletion but the exact capability/evidence lease did not release cleanly.",
                        freshItem.Source.CanonicalPath,
                        exception,
                        retryable: false)).ConfigureAwait(false);
            }

            try
            {
                await _historyStore
                    .CommitSourceDeletedAsync(
                        plan.Id,
                        ordinal,
                        sourceDeleteRequest.SourceIdentity,
                        UtcNow())
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                var resolved = await ResolveSourceDeleteCommitAsync(
                    plan.Id,
                    ordinal,
                    exception).ConfigureAwait(false);
                if (!resolved)
                {
                    return await FailSourceDeleteBarrierAsync(
                        plan.Id,
                        ordinal,
                        progress,
                        snapshot,
                        FailureFromException(
                            "CrossVolumeMoveSourceDeleteCommitFailed",
                            "The source delete ran but durable Moved commit could not be proven.",
                            freshItem.Source.CanonicalPath,
                            exception,
                            retryable: false)).ConfigureAwait(false);
                }
            }

            snapshot = snapshot.ReportProgress(ordinal + 1, receipt!.CanonicalDestinationPath);
            Report(progress, snapshot);

            if (context.IsCancellationRequested)
            {
                return await SettleCancellationAsync(plan.Id, progress, snapshot)
                    .ConfigureAwait(false);
            }
        }

        try
        {
            await _historyStore
                .CompleteAsync(
                    plan.Id,
                    FileCrossVolumeMoveTerminalState.Succeeded,
                    UtcNow())
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return FailAndReport(
                progress,
                snapshot,
                FailureFromException(
                    "CrossVolumeMoveHistoryFinalizeFailed",
                    "Every entry settled durably, but the composite operation could not be marked succeeded.",
                    null,
                    exception,
                    retryable: true));
        }

        snapshot = snapshot.Complete();
        Report(progress, snapshot);
        return snapshot;
    }

    private async ValueTask<FileOperationExecutionSnapshot> SettleCancellationAsync(
        Guid operationId,
        IProgress<FileOperationExecutionSnapshot>? progress,
        FileOperationExecutionSnapshot snapshot)
    {
        var requested = snapshot.State == FileOperationExecutionState.Validating
            ? snapshot.RequestCancellation()
            : snapshot.State == FileOperationExecutionState.CancellationRequested
                ? snapshot
                : snapshot.RequestCancellation();
        Report(progress, requested);

        var terminal = requested.CompletedEntryCount == requested.TotalEntryCount
            ? FileCrossVolumeMoveTerminalState.Succeeded
            : FileCrossVolumeMoveTerminalState.Cancelled;
        try
        {
            await _historyStore.CompleteAsync(operationId, terminal, UtcNow())
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return FailAndReport(
                progress,
                requested.State == FileOperationExecutionState.Cancelled
                    ? FileOperationExecutionSnapshot.CreatePlanned(requested.Plan).BeginValidation()
                    : requested,
                FailureFromException(
                    "CrossVolumeMoveCancellationSettlementFailed",
                    "Cross-volume Move reached a safe cancellation boundary but its composite journal could not be finalized.",
                    null,
                    exception,
                    retryable: true));
        }

        if (requested.State == FileOperationExecutionState.Cancelled)
        {
            return requested;
        }

        var settled = requested.CancelAtSafeBoundary();
        Report(progress, settled);
        return settled;
    }

    private async ValueTask<FileOperationExecutionSnapshot> FailSafeEntryAsync(
        Guid operationId,
        int ordinal,
        IProgress<FileOperationExecutionSnapshot>? progress,
        FileOperationExecutionSnapshot snapshot,
        FileOperationFailure failure)
    {
        try
        {
            await _historyStore
                .MarkEntryFailedAsync(operationId, ordinal, failure, UtcNow())
                .ConfigureAwait(false);
            await _historyStore
                .CompleteAsync(operationId, FileCrossVolumeMoveTerminalState.Failed, UtcNow())
                .ConfigureAwait(false);
        }
        catch (Exception historyException)
        {
            failure = FailureFromException(
                "CrossVolumeMoveSafeFailureRecordFailed",
                failure.Message + " Durable safe-failure settlement also failed.",
                failure.Path,
                historyException,
                retryable: true);
        }

        return FailAndReport(progress, snapshot, failure);
    }

    private async ValueTask<FileOperationExecutionSnapshot> FailCopyBarrierAsync(
        Guid operationId,
        int ordinal,
        IProgress<FileOperationExecutionSnapshot>? progress,
        FileOperationExecutionSnapshot snapshot,
        FileOperationFailure failure,
        FileCopyMutationReceipt? receipt)
    {
        try
        {
            await _historyStore
                .MarkRecoveryRequiredAsync(
                    operationId,
                    ordinal,
                    failure,
                    UtcNow(),
                    receipt?.DestinationIdentity,
                    receipt?.DestinationContentFingerprint)
                .ConfigureAwait(false);
        }
        catch
        {
            // CopyMutationStarted remains the durable restart-time recovery signal.
        }

        await BestEffortCompleteAsync(
            operationId,
            FileCrossVolumeMoveTerminalState.RecoveryRequired).ConfigureAwait(false);
        return FailAndReport(progress, snapshot, failure);
    }

    private async ValueTask<FileOperationExecutionSnapshot> ResolveSourceDeleteBarrierFailureAsync(
        Guid operationId,
        int ordinal,
        IProgress<FileOperationExecutionSnapshot>? progress,
        FileOperationExecutionSnapshot snapshot,
        FileOperationFailure failure)
    {
        try
        {
            var observed = await _historyStore.GetAsync(operationId).ConfigureAwait(false);
            if (observed is not null && ordinal < observed.Entries.Count)
            {
                var entry = observed.Entries[ordinal];
                if (entry.State == FileCrossVolumeMoveEntryState.DestinationCommitted)
                {
                    return await FailSafeEntryAsync(
                        operationId,
                        ordinal,
                        progress,
                        snapshot,
                        failure).ConfigureAwait(false);
                }
                if (entry.State is FileCrossVolumeMoveEntryState.SourceDeleteStarted or
                    FileCrossVolumeMoveEntryState.RecoveryRequired)
                {
                    return await FailSourceDeleteBarrierAsync(
                        operationId,
                        ordinal,
                        progress,
                        snapshot,
                        failure).ConfigureAwait(false);
                }
            }
        }
        catch
        {
        }

        failure = failure with
        {
            Message = failure.Message +
                " Durable delete-barrier state could not be inspected; recovery inspection is required before another attempt.",
        };
        return FailAndReport(progress, snapshot, failure);
    }

    private async ValueTask<FileOperationExecutionSnapshot> FailSourceDeleteBarrierAsync(
        Guid operationId,
        int ordinal,
        IProgress<FileOperationExecutionSnapshot>? progress,
        FileOperationExecutionSnapshot snapshot,
        FileOperationFailure failure)
    {
        try
        {
            await _historyStore
                .MarkRecoveryRequiredAsync(
                    operationId,
                    ordinal,
                    failure,
                    UtcNow())
                .ConfigureAwait(false);
        }
        catch
        {
            // SourceDeleteStarted remains an explicit durable recovery signal.
        }

        await BestEffortCompleteAsync(
            operationId,
            FileCrossVolumeMoveTerminalState.RecoveryRequired).ConfigureAwait(false);
        return FailAndReport(progress, snapshot, failure);
    }

    private async ValueTask<bool> ResolveSourceDeleteCommitAsync(
        Guid operationId,
        int ordinal,
        Exception commitException)
    {
        try
        {
            var observed = await _historyStore.GetAsync(operationId).ConfigureAwait(false);
            if (observed is not null &&
                ordinal < observed.Entries.Count &&
                observed.Entries[ordinal].State == FileCrossVolumeMoveEntryState.Moved)
            {
                return true;
            }
        }
        catch
        {
        }

        _ = commitException;
        return false;
    }

    private async ValueTask BestEffortCompleteAsync(
        Guid operationId,
        FileCrossVolumeMoveTerminalState terminalState)
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

    private static string? ValidateSupportedPlan(FileOperationPlan plan)
    {
        if (plan.Kind != FileOperationKind.Move)
        {
            return "This executor supports Move plans only.";
        }
        if (plan.Intent.Entries.Count == 0)
        {
            return "A cross-volume Move plan must contain at least one file entry.";
        }
        if (plan.Intent.Entries.Any(static entry => entry.IsDirectory))
        {
            return "Directory Move is not supported by the cross-volume file Move executor.";
        }
        return null;
    }

    private static bool TryValidateInitialResult(
        FileOperationPlan plan,
        FileOperationExecutionValidationResult validation,
        out string problem)
    {
        if (!ReferenceEquals(validation.Plan, plan) ||
            !validation.CanBeginMutation ||
            validation.Items.Count != plan.Intent.Entries.Count ||
            validation.SourceDirectory.Identity is not FileIdentity sourceRoot ||
            validation.DestinationDirectory.Identity is not FileIdentity destinationRoot ||
            sourceRoot.VolumeSerialNumber == destinationRoot.VolumeSerialNumber)
        {
            problem = "Execution validation did not prove the requested immutable Move on distinct source/destination volumes.";
            return false;
        }

        var strategy = FileMoveExecutionStrategyClassifier.Classify(validation);
        if (strategy.Strategy is not FileMoveExecutionStrategy.CrossVolumeCopyDeleteRequired and
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
                problem = "Cross-volume Move validation changed entry ordering/type or returned an unsupported decision.";
                return false;
            }
        }

        problem = string.Empty;
        return true;
    }

    private static bool TryValidateFreshCopyResult(
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
        var strategy = FileMoveExecutionStrategyClassifier.Classify(freshValidation);
        if (!ReferenceEquals(freshValidation.Plan, freshPlan) ||
            strategy.Strategy != FileMoveExecutionStrategy.CrossVolumeCopyDeleteRequired ||
            freshValidation.Items.Count != 1 ||
            freshItem.Entry != initialItem.Entry ||
            freshItem.Decision != FileOperationExecutionValidationDecision.Ready ||
            freshItem.Destination.State != FileOperationCanonicalPathState.Missing)
        {
            problem = "The Move entry's cross-volume Copy validation/strategy changed after composite history began.";
            return false;
        }

        if (!SameCanonicalObject(initialValidation.SourceDirectory, freshValidation.SourceDirectory) ||
            !SameCanonicalObject(initialValidation.DestinationDirectory, freshValidation.DestinationDirectory) ||
            !PathsEqual(initialItem.Source.CanonicalPath, freshItem.Source.CanonicalPath) ||
            initialItem.Source.Identity is not FileIdentity initialIdentity ||
            freshItem.Source.Identity is not FileIdentity freshIdentity ||
            initialIdentity != freshIdentity ||
            !PathsEqual(initialItem.Destination.CanonicalPath, freshItem.Destination.CanonicalPath))
        {
            problem = "A canonical Move root, source identity, or destination leaf changed before Copy.";
            return false;
        }

        problem = string.Empty;
        return true;
    }

    private static bool TryValidateCopyReceipt(
        FileOperationExecutionValidationItem item,
        FileOperationExecutionValidationResult validation,
        FileCopyMutationReceipt receipt,
        out string problem)
    {
        if (!PathsEqual(receipt.CanonicalSourcePath, item.Source.CanonicalPath) ||
            !PathsEqual(receipt.CanonicalDestinationPath, item.Destination.CanonicalPath) ||
            item.Source.Identity is not FileIdentity sourceIdentity ||
            receipt.SourceIdentity != sourceIdentity ||
            validation.DestinationDirectory.Identity is not FileIdentity destinationRoot ||
            receipt.DestinationIdentity.VolumeSerialNumber != destinationRoot.VolumeSerialNumber ||
            receipt.DestinationContentFingerprint is null)
        {
            problem = "The Copy primitive did not return the exact validated source plus a content-bound destination identity on the destination volume.";
            return false;
        }

        problem = string.Empty;
        return true;
    }

    private static FileCrossVolumeMoveSourceDeleteRequest CreateSourceDeleteRequest(
        Guid operationId,
        int ordinal,
        FileOperationExecutionValidationItem item,
        FileOperationExecutionValidationResult validation,
        FileCopyMutationReceipt receipt) =>
        new(
            operationId,
            ordinal,
            item.Entry,
            validation.SourceDirectory.CanonicalPath,
            validation.SourceDirectory.Identity!.Value,
            validation.DestinationDirectory.CanonicalPath,
            validation.DestinationDirectory.Identity!.Value,
            item.Source.CanonicalPath,
            item.Source.Identity!.Value,
            receipt.CanonicalDestinationPath,
            receipt.DestinationIdentity,
            receipt.DestinationContentFingerprint!);

    private static void ValidateSourceDeleteEvidence(
        FileCrossVolumeMoveSourceDeleteRequest request,
        FileCrossVolumeMoveSourceDeleteEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!ReferenceEquals(evidence.Request, request) ||
            evidence.SourceDeleteMutationAuthorized ||
            evidence.OperationId != request.OperationId ||
            evidence.Ordinal != request.Ordinal ||
            evidence.SourceIdentity != request.SourceIdentity ||
            evidence.DestinationIdentity != request.DestinationIdentity ||
            evidence.DestinationContentFingerprint != request.DestinationContentFingerprint)
        {
            throw new InvalidOperationException(
                "The source-delete primitive returned evidence not bound to the exact pre-barrier request.");
        }
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

    private static async ValueTask DisposeCopyLeaseAsync(IFileCopyMutationLease lease)
    {
        try
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Copy leases are SafeHandle-backed evidence leases. A disposal problem cannot
            // authorize the later source-delete phase; execution will only continue after
            // destination commit and a separately acquired source-delete lease.
        }
    }

    private static async ValueTask DisposeSourceDeleteLeaseAsync(
        IFileCrossVolumeMoveSourceDeleteLease lease)
    {
        try
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Pre-barrier disposal owns no delete mutation. Post-barrier callers route release
            // failures through recovery before invoking this best-effort helper.
        }
    }

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