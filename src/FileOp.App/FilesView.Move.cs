using System;
using System.IO;
using System.Linq;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.UI.Xaml;

namespace FileOp.App;

public sealed partial class FilesView
{
    private bool _moveExecutionUiInitialized;
    private bool _moveExecutionRunning;
    private bool _moveCancellationRequested;
    private Guid? _activeMoveOperationId;
    private IFileOperationExecutor? _activeMoveExecutor;

    public bool IsFileOperationExecutionBusy => _copyExecutionRunning || _moveExecutionRunning;

    private void RunQueuedMoveButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (_moveExecutionUiInitialized)
        {
            return;
        }

        _moveExecutionUiInitialized = true;
        RunQueuedMoveButton.Click += RunQueuedMoveButton_Click;
        CancelQueuedMoveButton.Click += CancelQueuedMoveButton_Click;
        OperationQueueList.SelectionChanged += MoveOperationQueueList_SelectionChanged;
        LeftPane.IntentStateChanged += MovePane_IntentStateChanged;
        RightPane.IntentStateChanged += MovePane_IntentStateChanged;
        UpdateMoveExecutionAvailability();
    }

    private void MoveOperationQueueList_SelectionChanged(
        object sender,
        Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e) =>
        UpdateMoveExecutionAvailability();

    private void MovePane_IntentStateChanged(object? sender, EventArgs e) =>
        UpdateMoveExecutionAvailability();

    private async void RunQueuedMoveButton_Click(object sender, RoutedEventArgs e) =>
        await RunSelectedMoveAsync();

    private async void CancelQueuedMoveButton_Click(object sender, RoutedEventArgs e)
    {
        var executor = _activeMoveExecutor;
        if (!_moveExecutionRunning ||
            _moveCancellationRequested ||
            _activeMoveOperationId is not Guid operationId ||
            executor is null)
        {
            return;
        }

        _moveCancellationRequested = true;
        UpdateMoveCancellationAvailability();
        QueueStatusText.Text =
            "Move cancellation requested. Validation may stop immediately; active mutation settles only at reviewed safe boundaries. A cross-volume Move may stop after durable destination Copy commit while retaining the original source, but cancellation never interrupts a post-source-delete-barrier mutation/commit boundary.";

        try
        {
            var accepted = await executor.RequestCancellationAsync(operationId);
            if (!accepted && _moveExecutionRunning && _activeMoveOperationId == operationId)
            {
                QueueStatusText.Text =
                    "The Move executor is already settling and did not accept another cancellation request. No new mutation authority was created.";
            }
        }
        catch (Exception exception)
        {
            if (_moveExecutionRunning && _activeMoveOperationId == operationId)
            {
                QueueStatusText.Text =
                    $"Move cancellation could not be registered: {exception.Message} The active operation remains governed by its existing durable boundary.";
            }
        }
    }

    private async Task RunSelectedMoveAsync()
    {
        if (IsFileOperationExecutionBusy ||
            _preflightRunning ||
            OperationQueueList.SelectedItem is not FileBrowserQueuedOperationRow row)
        {
            return;
        }

        var plan = _queuedOperations.FirstOrDefault(operation => operation.Id == row.Id);
        if (plan is null)
        {
            RefreshQueuePresentation();
            UpdateMoveExecutionAvailability();
            return;
        }

        if (!CanAttemptMovePlan(plan, out var refusal))
        {
            QueueStatusText.Text = refusal;
            UpdateMoveExecutionAvailability();
            return;
        }

        // This classification is evidence only. The selected executor repeats full
        // validation before history, then again at every mutation phase boundary.
        FileOperationExecutionValidationResult executionValidation;
        try
        {
            executionValidation = await new WindowsMoveOperationExecutionValidator()
                .ValidateAsync(plan);
        }
        catch (Exception exception)
        {
            QueueStatusText.Text =
                $"Move execution-grade classification could not complete: {exception.Message} No durable history or filesystem mutation was attempted.";
            return;
        }

        if (_queuedOperations.All(operation => operation.Id != plan.Id) ||
            !IsPlanBoundToCurrentFilesState(plan))
        {
            QueueStatusText.Text =
                "The queued Move changed or its pane/tab/path context became stale during classification. Run fresh preflight on a current plan.";
            _preflightSnapshots.Remove(plan.Id);
            RefreshQueuePresentation();
            return;
        }

        var strategy = FileMoveExecutionStrategyClassifier.Classify(executionValidation);
        if (strategy.Strategy == FileMoveExecutionStrategy.Blocked)
        {
            QueueStatusText.Text =
                $"Move cannot execute through a reviewed file boundary: {strategy.Summary} The queued plan was not consumed; rerun preflight after correcting the condition.";
            _preflightSnapshots.Remove(plan.Id);
            RefreshQueuePresentation();
            return;
        }

        if (executionValidation.SourceDirectory.Identity is not FileIdentity sourceRootIdentity ||
            executionValidation.DestinationDirectory.Identity is not FileIdentity destinationRootIdentity)
        {
            QueueStatusText.Text =
                "Move classification did not retain stable source/destination root identities. No durable history or mutation was attempted.";
            return;
        }

        var crossVolume =
            sourceRootIdentity.VolumeSerialNumber != destinationRootIdentity.VolumeSerialNumber;
        if (crossVolume)
        {
            if (strategy.Strategy is not FileMoveExecutionStrategy.CrossVolumeCopyDeleteRequired and
                not FileMoveExecutionStrategy.SkipOnly)
            {
                QueueStatusText.Text =
                    "Move root identities prove different volumes, but classification did not return the reviewed cross-volume or all-Skip strategy. No mutation was attempted.";
                return;
            }
        }
        else if (strategy.Strategy is not FileMoveExecutionStrategy.SameVolumeRenameRequired and
            not FileMoveExecutionStrategy.SkipOnly)
        {
            QueueStatusText.Text =
                "Move root identities prove one volume, but classification did not return the reviewed same-volume or all-Skip strategy. No mutation was attempted.";
            return;
        }

        _moveExecutionRunning = true;
        _moveCancellationRequested = false;
        _activeMoveOperationId = plan.Id;
        _activeMoveExecutor = null;
        BeginMoveProgressPresentation(plan.Intent.Entries.Count);
        SetMoveExecutionUiBusy(true);
        QueueStatusText.Text = strategy.Strategy == FileMoveExecutionStrategy.SkipOnly
            ? $"Move {plan.Id} contains only explicit Skip entries. The {(crossVolume ? "cross-volume composite" : "same-volume")} journal will record durable no-mutation settlement."
            : crossVolume
                ? $"Move {plan.Id} is entering the cross-volume Copy-plus-identity-bound-source-delete boundary. Copy commit is a safe cancellation checkpoint; source deletion requires a separate durable barrier, complete pinned fidelity proof and live capability authorization."
                : $"Move {plan.Id} is entering the same-volume identity-preserving rename boundary. Fresh validation still runs again before durable history and before every rename.";

        var executorInvoked = false;
        FileOperationExecutionSnapshot? finalSnapshot = null;
        FileOperationActionHistory? finalSameVolumeHistory = null;
        FileCrossVolumeMoveActionHistory? finalCrossVolumeHistory = null;
        Exception? executionException = null;
        Exception? historyReadException = null;

        try
        {
            if (crossVolume)
            {
                using var historyStore = new SqliteFileCrossVolumeMoveActionHistoryStore(
                    GetFileOperationHistoryDatabasePath());
                var executor = new FileCrossVolumeMoveOperationExecutor(
                    new WindowsMoveOperationExecutionValidator(),
                    historyStore,
                    new WindowsFileCopyMutationPrimitive(),
                    new WindowsFidelityVerifiedFileCrossVolumeMoveSourceDeletePrimitive());
                _activeMoveExecutor = executor;
                UpdateMoveCancellationAvailability();

                var progress = CreateMoveProgress(plan.Id);
                executorInvoked = true;
                try
                {
                    finalSnapshot = await executor.ExecuteAsync(plan, progress);
                }
                catch (Exception exception)
                {
                    executionException = exception;
                }
                finally
                {
                    _activeMoveExecutor = null;
                    UpdateMoveCancellationAvailability();
                }

                try
                {
                    finalCrossVolumeHistory = await historyStore.GetAsync(plan.Id);
                }
                catch (Exception exception)
                {
                    historyReadException = exception;
                }
            }
            else
            {
                using var historyStore = new SqliteFileOperationActionHistoryStore(
                    GetFileOperationHistoryDatabasePath());
                var executor = new FileSameVolumeMoveOperationExecutor(
                    new WindowsMoveOperationExecutionValidator(),
                    historyStore,
                    new WindowsFileSameVolumeMoveMutationPrimitive());
                _activeMoveExecutor = executor;
                UpdateMoveCancellationAvailability();

                var progress = CreateMoveProgress(plan.Id);
                executorInvoked = true;
                try
                {
                    finalSnapshot = await executor.ExecuteAsync(plan, progress);
                }
                catch (Exception exception)
                {
                    executionException = exception;
                }
                finally
                {
                    _activeMoveExecutor = null;
                    UpdateMoveCancellationAvailability();
                }

                try
                {
                    finalSameVolumeHistory = await historyStore.GetAsync(plan.Id);
                }
                catch (Exception exception)
                {
                    historyReadException = exception;
                }
            }
        }
        catch (Exception exception)
        {
            executionException = exception;
        }
        finally
        {
            _activeMoveExecutor = null;
            _activeMoveOperationId = null;
            _moveCancellationRequested = false;
            _moveExecutionRunning = false;
        }

        if (executorInvoked)
        {
            // Both Move journals make operation IDs single-use once execution can reach
            // durable history. Never replay the same immutable ID after any ambiguous result.
            _queuedOperations.RemoveAll(operation => operation.Id == plan.Id);
            _preflightSnapshots.Remove(plan.Id);
        }

        CompleteMoveProgressPresentation(
            finalSnapshot,
            finalSameVolumeHistory,
            finalCrossVolumeHistory);
        QueueStatusText.Text = crossVolume
            ? FormatCrossVolumeMoveExecutionOutcome(
                finalSnapshot,
                finalCrossVolumeHistory,
                executionException,
                historyReadException,
                executorInvoked)
            : FormatMoveExecutionOutcome(
                finalSnapshot,
                finalSameVolumeHistory,
                executionException,
                historyReadException,
                executorInvoked);

        RefreshQueuePresentation();
        if (executorInvoked)
        {
            RequestRefreshForMoveEndpoint(plan.Intent.SourceDirectoryPath);
            RequestRefreshForMoveEndpoint(plan.Intent.DestinationDirectoryPath);
        }
        SetMoveExecutionUiBusy(false);
        UpdateCopyExecutionAvailability();
        UpdateMoveExecutionAvailability();
        UpdateDirectoryMoveExecutionAvailability();
    }

    private Progress<FileOperationExecutionSnapshot> CreateMoveProgress(Guid operationId) =>
        new(snapshot =>
        {
            if (_moveExecutionRunning && _activeMoveOperationId == operationId)
            {
                UpdateMoveProgressPresentation(snapshot);
                QueueStatusText.Text = FormatMoveExecutionProgress(snapshot);
            }
        });

    private bool CanAttemptMovePlan(FileOperationPlan plan, out string refusal)
    {
        if (_preflightRunning)
        {
            refusal = "Wait for the current read-only preflight to finish before running Move.";
            return false;
        }

        if (plan.Kind != FileOperationKind.Move)
        {
            refusal = "Select a queued Move plan to use the Move executor.";
            return false;
        }

        if (plan.Intent.Entries.Count == 0 || plan.Intent.Entries.Any(static entry => entry.IsDirectory))
        {
            refusal = "The File Move executor supports regular files only. Use the separate Directory Move executor for a homogeneous directory-only plan.";
            return false;
        }

        if (!_preflightSnapshots.TryGetValue(plan.Id, out var preflight) ||
            !ReferenceEquals(preflight.Result.Plan, plan))
        {
            refusal = "Run read-only preflight for this exact queued Move plan before execution.";
            return false;
        }

        if (preflight.Result.Status != FileOperationPreflightStatus.Ready)
        {
            refusal = preflight.Result.Status == FileOperationPreflightStatus.NeedsDecision
                ? "This Move still needs an explicit collision decision. Requeue with Skip existing or Stop on collision; overwrite/replacement remains unsupported."
                : $"This Move is blocked by read-only preflight: {preflight.Result.Summary}";
            return false;
        }

        if (!IsPlanBoundToCurrentFilesState(plan))
        {
            refusal =
                "The active pane/tab/path context no longer matches this queued Move plan. Create and preflight a fresh plan from current Files state.";
            return false;
        }

        refusal = string.Empty;
        return true;
    }

    private void RequestRefreshForMoveEndpoint(string directoryPath)
    {
        foreach (var pane in new[] { LeftPane, RightPane })
        {
            if (!pane.IsDirectoryReady || pane.CurrentPath is not { } currentPath)
            {
                continue;
            }

            try
            {
                if (PathsEqual(currentPath, directoryPath))
                {
                    pane.RequestRefresh();
                }
            }
            catch (Exception exception)
                when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }
    }

    private void BeginMoveProgressPresentation(int totalEntryCount)
    {
        MoveProgressBar.Visibility = Visibility.Visible;
        MoveProgressBar.IsIndeterminate = true;
        MoveProgressBar.Maximum = Math.Max(1, totalEntryCount);
        MoveProgressBar.Value = 0;
    }

    private void UpdateMoveProgressPresentation(FileOperationExecutionSnapshot snapshot)
    {
        MoveProgressBar.Visibility = Visibility.Visible;
        MoveProgressBar.Maximum = Math.Max(1, snapshot.TotalEntryCount);
        MoveProgressBar.IsIndeterminate =
            snapshot.State is FileOperationExecutionState.Planned or
                FileOperationExecutionState.Validating;
        if (!MoveProgressBar.IsIndeterminate)
        {
            MoveProgressBar.Value = Math.Clamp(
                snapshot.CompletedEntryCount,
                0,
                snapshot.TotalEntryCount);
        }
    }

    private void CompleteMoveProgressPresentation(
        FileOperationExecutionSnapshot? snapshot,
        FileOperationActionHistory? sameVolumeHistory,
        FileCrossVolumeMoveActionHistory? crossVolumeHistory)
    {
        if (snapshot is not null)
        {
            UpdateMoveProgressPresentation(snapshot);
            MoveProgressBar.IsIndeterminate = false;
            return;
        }

        if (sameVolumeHistory is not null)
        {
            MoveProgressBar.Visibility = Visibility.Visible;
            MoveProgressBar.IsIndeterminate = false;
            MoveProgressBar.Maximum = Math.Max(1, sameVolumeHistory.Entries.Count);
            MoveProgressBar.Value = sameVolumeHistory.Entries.Count(static entry =>
                entry.State is FileOperationActionEntryState.Committed or
                    FileOperationActionEntryState.Skipped or
                    FileOperationActionEntryState.Failed or
                    FileOperationActionEntryState.RecoveryRequired);
            return;
        }

        if (crossVolumeHistory is not null)
        {
            MoveProgressBar.Visibility = Visibility.Visible;
            MoveProgressBar.IsIndeterminate = false;
            MoveProgressBar.Maximum = Math.Max(1, crossVolumeHistory.Entries.Count);
            MoveProgressBar.Value = crossVolumeHistory.Entries.Count(static entry =>
                entry.State is FileCrossVolumeMoveEntryState.Moved or
                    FileCrossVolumeMoveEntryState.Skipped or
                    FileCrossVolumeMoveEntryState.Failed or
                    FileCrossVolumeMoveEntryState.RecoveryRequired);
            return;
        }

        MoveProgressBar.IsIndeterminate = false;
        MoveProgressBar.Visibility = Visibility.Collapsed;
    }

    private void SetMoveExecutionUiBusy(bool busy)
    {
        if (busy)
        {
            PrepareLeftToRightButton.IsEnabled = false;
            PrepareRightToLeftButton.IsEnabled = false;
            CollisionPolicyBox.IsEnabled = false;
            QueueCopyButton.IsEnabled = false;
            QueueMoveButton.IsEnabled = false;
            OperationQueueList.IsEnabled = false;
            PreflightQueuedOperationButton.IsEnabled = false;
            RunQueuedCopyButton.IsEnabled = false;
            RunQueuedMoveButton.IsEnabled = false;
            RunQueuedDirectoryMoveButton.IsEnabled = false;
            RemoveQueuedOperationButton.IsEnabled = false;
            ClearQueueButton.IsEnabled = false;
            CancelQueuedCopyButton.IsEnabled = false;
            CancelQueuedMoveButton.Visibility = Visibility.Visible;
            UpdateMoveCancellationAvailability();
            return;
        }

        CancelQueuedMoveButton.IsEnabled = false;
        CancelQueuedMoveButton.Visibility = Visibility.Collapsed;
        CollisionPolicyBox.IsEnabled = true;
        UpdateIntentAvailability();
        UpdateQueueActions();
    }

    private void UpdateMoveCancellationAvailability()
    {
        CancelQueuedMoveButton.IsEnabled =
            _moveExecutionRunning &&
            !_moveCancellationRequested &&
            _activeMoveOperationId.HasValue &&
            _activeMoveExecutor is not null;
    }

    private void UpdateMoveExecutionAvailability()
    {
        if (!_moveExecutionUiInitialized)
        {
            return;
        }

        if (_moveExecutionRunning)
        {
            SetMoveExecutionUiBusy(true);
            return;
        }

        if (_copyExecutionRunning ||
            OperationQueueList.SelectedItem is not FileBrowserQueuedOperationRow row)
        {
            RunQueuedMoveButton.IsEnabled = false;
            return;
        }

        var plan = _queuedOperations.FirstOrDefault(operation => operation.Id == row.Id);
        RunQueuedMoveButton.IsEnabled = plan is not null && CanAttemptMovePlan(plan, out _);
    }

    /// <summary>
    /// Reasserts the execution lock after MainWindow has invalidated planning state
    /// because the indexed backing source changed while a direct filesystem executor
    /// was already active.
    /// </summary>
    public void ReassertOperationExecutionBusyAfterSourceChange()
    {
        if (_moveExecutionRunning)
        {
            SetMoveExecutionUiBusy(true);
            QueueStatusText.Text =
                "The indexed backing source changed while a Move was already active. Non-running plans were discarded; the active Move remains governed only by its direct filesystem identities/durable history and will not be replayed automatically.";
        }
        else if (_copyExecutionRunning)
        {
            SetCopyExecutionUiBusy(true);
        }
    }

    private static string FormatMoveExecutionProgress(FileOperationExecutionSnapshot snapshot) =>
        snapshot.State switch
        {
            FileOperationExecutionState.Planned =>
                "Move is preparing its reviewed execution boundary.",
            FileOperationExecutionState.Validating =>
                "Move is revalidating canonical paths, source identity and current volume strategy. No mutation has occurred.",
            FileOperationExecutionState.Running =>
                $"Move running: {snapshot.CompletedEntryCount:N0}/{snapshot.TotalEntryCount:N0} entries reached durable settlement" +
                (string.IsNullOrWhiteSpace(snapshot.CurrentPath) ? "." : $" · {snapshot.CurrentPath}"),
            FileOperationExecutionState.CancellationRequested =>
                $"Move cancellation is pending a safe boundary: {snapshot.CompletedEntryCount:N0}/{snapshot.TotalEntryCount:N0} entries settled.",
            FileOperationExecutionState.Succeeded =>
                $"Move completed: {snapshot.CompletedEntryCount:N0}/{snapshot.TotalEntryCount:N0} entries settled.",
            FileOperationExecutionState.Cancelled =>
                $"Move cancelled at a safe boundary after {snapshot.CompletedEntryCount:N0}/{snapshot.TotalEntryCount:N0} entries.",
            FileOperationExecutionState.Failed =>
                $"Move failed: {snapshot.Failure?.Message ?? "The reviewed Move executor reported a failure."}",
            _ => snapshot.State.ToString(),
        };

    private static string FormatMoveExecutionOutcome(
        FileOperationExecutionSnapshot? snapshot,
        FileOperationActionHistory? history,
        Exception? executionException,
        Exception? historyReadException,
        bool executorInvoked)
    {
        if (history?.RequiresRecovery == true)
        {
            return
                $"Move requires recovery inspection. {FormatMoveHistoryCounts(history)} " +
                "Durable MutationStarted/RecoveryRequired history is evidence only; FileOp will not replay, rollback or reinterpret the original operation ID automatically.";
        }

        if (history?.TerminalState == FileOperationActionTerminalState.Succeeded)
        {
            return
                $"Move completed through the reviewed same-volume durable executor. {FormatMoveHistoryCounts(history)} " +
                "Matching source and destination panes are refreshing.";
        }

        if (history?.TerminalState == FileOperationActionTerminalState.Cancelled)
        {
            return
                $"Move cancelled at a reviewed safe boundary. {FormatMoveHistoryCounts(history)} The operation ID is single-use.";
        }

        if (history?.TerminalState == FileOperationActionTerminalState.Failed)
        {
            return
                $"Move failed before any unresolved mutation boundary remained. {FormatMoveHistoryCounts(history)} " +
                (executionException is null ? string.Empty : executionException.Message);
        }

        if (history is not null && history.TerminalState is null)
        {
            return
                $"Move durable history remains non-terminal. {FormatMoveHistoryCounts(history)} Treat this as recovery evidence; no automatic retry or replay is authorized.";
        }

        if (historyReadException is not null)
        {
            return
                $"Move execution settled but durable history could not be read: {historyReadException.Message} " +
                "The original operation ID will not be reused; inspect the action-history database before further mutation.";
        }

        if (executionException is not null)
        {
            return executorInvoked
                ? $"Move execution ended unexpectedly: {executionException.Message} The original operation ID was consumed conservatively."
                : $"Move could not start: {executionException.Message} No reviewed executor or filesystem mutation was invoked.";
        }

        return snapshot is not null
            ? FormatMoveExecutionProgress(snapshot)
            : "Move did not produce an execution result. No additional mutation authority was created.";
    }

    private static string FormatCrossVolumeMoveExecutionOutcome(
        FileOperationExecutionSnapshot? snapshot,
        FileCrossVolumeMoveActionHistory? history,
        Exception? executionException,
        Exception? historyReadException,
        bool executorInvoked)
    {
        if (history?.RequiresRecovery == true)
        {
            return
                $"Cross-volume Move requires recovery inspection. {FormatCrossVolumeMoveHistoryCounts(history)} " +
                "Copy/source-delete barrier history is evidence only; FileOp will not replay Copy, delete the source, or reuse the original operation ID automatically.";
        }

        if (history?.TerminalState == FileCrossVolumeMoveTerminalState.Succeeded)
        {
            return
                $"Cross-volume Move completed through the reviewed Copy-plus-fidelity-verified-identity-bound-source-delete executor. {FormatCrossVolumeMoveHistoryCounts(history)} Matching source and destination panes are refreshing.";
        }

        if (history?.TerminalState == FileCrossVolumeMoveTerminalState.Cancelled)
        {
            return history.HasRetainedSourceDuplicates
                ? $"Cross-volume Move cancelled at a safe boundary after one or more destination copies were durably committed. {FormatCrossVolumeMoveHistoryCounts(history)} The copied destination file(s) remain and the original source file(s) were retained; no automatic cleanup or replay is authorized."
                : $"Cross-volume Move cancelled before any unresolved destructive boundary. {FormatCrossVolumeMoveHistoryCounts(history)} The operation ID is single-use.";
        }

        if (history?.TerminalState == FileCrossVolumeMoveTerminalState.Failed)
        {
            var retained = history.HasRetainedSourceDuplicates
                ? " One or more destination copies were durably committed while their original sources were retained."
                : string.Empty;
            return
                $"Cross-volume Move failed without an unresolved mutation barrier. {FormatCrossVolumeMoveHistoryCounts(history)}{retained} No automatic delete or replay is authorized. " +
                (executionException is null ? string.Empty : executionException.Message);
        }

        if (history is not null && history.TerminalState is null)
        {
            return
                $"Cross-volume Move composite history remains non-terminal. {FormatCrossVolumeMoveHistoryCounts(history)} Treat this as recovery evidence; no automatic Copy retry or source deletion is authorized.";
        }

        if (historyReadException is not null)
        {
            return
                $"Cross-volume Move execution settled but composite history could not be read: {historyReadException.Message} The original operation ID will not be reused; inspect durable history before further mutation.";
        }

        if (executionException is not null)
        {
            return executorInvoked
                ? $"Cross-volume Move execution ended unexpectedly: {executionException.Message} The original operation ID was consumed conservatively."
                : $"Cross-volume Move could not start: {executionException.Message} No reviewed executor or filesystem mutation was invoked.";
        }

        return snapshot is not null
            ? FormatMoveExecutionProgress(snapshot)
            : "Cross-volume Move did not produce an execution result. No additional mutation authority was created.";
    }

    private static string FormatMoveHistoryCounts(FileOperationActionHistory history)
    {
        var committed = history.Entries.Count(static entry =>
            entry.State == FileOperationActionEntryState.Committed);
        var skipped = history.Entries.Count(static entry =>
            entry.State == FileOperationActionEntryState.Skipped);
        var failed = history.Entries.Count(static entry =>
            entry.State == FileOperationActionEntryState.Failed);
        var recovery = history.Entries.Count(static entry =>
            entry.State is FileOperationActionEntryState.MutationStarted or
                FileOperationActionEntryState.RecoveryRequired);
        return
            $"{committed:N0} committed, {skipped:N0} skipped, {failed:N0} failed, {recovery:N0} recovery-sensitive.";
    }

    private static string FormatCrossVolumeMoveHistoryCounts(
        FileCrossVolumeMoveActionHistory history)
    {
        var moved = history.Entries.Count(static entry =>
            entry.State == FileCrossVolumeMoveEntryState.Moved);
        var copiedSourceRetained = history.Entries.Count(static entry =>
            entry.State is FileCrossVolumeMoveEntryState.DestinationCommitted or
                FileCrossVolumeMoveEntryState.Failed &&
                entry.DestinationIdentity.HasValue &&
                entry.DestinationContentFingerprint is not null);
        var skipped = history.Entries.Count(static entry =>
            entry.State == FileCrossVolumeMoveEntryState.Skipped);
        var failed = history.Entries.Count(static entry =>
            entry.State == FileCrossVolumeMoveEntryState.Failed);
        var recovery = history.Entries.Count(static entry =>
            entry.State is FileCrossVolumeMoveEntryState.CopyMutationStarted or
                FileCrossVolumeMoveEntryState.SourceDeleteStarted or
                FileCrossVolumeMoveEntryState.RecoveryRequired);
        return
            $"{moved:N0} moved, {copiedSourceRetained:N0} copied/source-retained, {skipped:N0} skipped, {failed:N0} failed, {recovery:N0} recovery-sensitive.";
    }
}
