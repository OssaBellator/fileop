using System;
using System.IO;
using System.Linq;
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
            "Move cancellation requested. Validation may stop immediately; a running same-volume Move settles only between entries and never interrupts a post-MutationStarted rename/commit boundary.";

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

        // This classification is evidence only. The actual executor repeats full
        // validation before history, then again per entry before MutationStarted.
        FileOperationExecutionValidationResult executionValidation;
        try
        {
            executionValidation = await new WindowsFileOperationExecutionValidator()
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
        if (strategy.Strategy == FileMoveExecutionStrategy.CrossVolumeCopyDeleteRequired)
        {
            QueueStatusText.Text =
                "This Move crosses volumes. Cross-volume Move remains disabled until its Copy-plus-source-delete transaction has a separately reviewed durable source-delete/authorization boundary. The queued plan was not consumed.";
            return;
        }

        if (strategy.Strategy == FileMoveExecutionStrategy.Blocked)
        {
            QueueStatusText.Text =
                $"Move cannot execute through the reviewed same-volume boundary: {strategy.Summary} The queued plan was not consumed; rerun preflight after correcting the condition.";
            _preflightSnapshots.Remove(plan.Id);
            RefreshQueuePresentation();
            return;
        }

        if (strategy.Strategy is not FileMoveExecutionStrategy.SameVolumeRenameRequired and
            not FileMoveExecutionStrategy.SkipOnly)
        {
            QueueStatusText.Text = "Move classification returned an unsupported execution strategy. No mutation was attempted.";
            return;
        }

        _moveExecutionRunning = true;
        _moveCancellationRequested = false;
        _activeMoveOperationId = plan.Id;
        _activeMoveExecutor = null;
        BeginMoveProgressPresentation(plan.Intent.Entries.Count);
        SetMoveExecutionUiBusy(true);
        QueueStatusText.Text = strategy.Strategy == FileMoveExecutionStrategy.SkipOnly
            ? $"Move {plan.Id} contains only explicit Skip entries. The reviewed executor will record durable no-mutation settlement."
            : $"Move {plan.Id} is entering the same-volume identity-preserving rename boundary. Fresh validation still runs again before durable history and before every rename.";

        var executorInvoked = false;
        FileOperationExecutionSnapshot? finalSnapshot = null;
        FileOperationActionHistory? finalHistory = null;
        Exception? executionException = null;
        Exception? historyReadException = null;

        try
        {
            using var historyStore = new SqliteFileOperationActionHistoryStore(
                GetFileOperationHistoryDatabasePath());
            var executor = new FileSameVolumeMoveOperationExecutor(
                new WindowsFileOperationExecutionValidator(),
                historyStore,
                new WindowsFileSameVolumeMoveMutationPrimitive());
            _activeMoveExecutor = executor;
            UpdateMoveCancellationAvailability();

            var progress = new Progress<FileOperationExecutionSnapshot>(snapshot =>
            {
                if (_moveExecutionRunning && _activeMoveOperationId == plan.Id)
                {
                    UpdateMoveProgressPresentation(snapshot);
                    QueueStatusText.Text = FormatMoveExecutionProgress(snapshot);
                }
            });

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
                finalHistory = await historyStore.GetAsync(plan.Id);
            }
            catch (Exception exception)
            {
                historyReadException = exception;
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
            // Once the reviewed Move executor is invoked, conservatively treat this
            // operation ID as single-use even if a subsequent history read is ambiguous.
            _queuedOperations.RemoveAll(operation => operation.Id == plan.Id);
            _preflightSnapshots.Remove(plan.Id);
        }

        CompleteMoveProgressPresentation(finalSnapshot, finalHistory);
        QueueStatusText.Text = FormatMoveExecutionOutcome(
            finalSnapshot,
            finalHistory,
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
    }

    private bool CanAttemptMovePlan(FileOperationPlan plan, out string refusal)
    {
        if (plan.Kind != FileOperationKind.Move)
        {
            refusal = "Select a queued Move plan to use the Move executor.";
            return false;
        }

        if (plan.Intent.Entries.Count == 0 || plan.Intent.Entries.Any(static entry => entry.IsDirectory))
        {
            refusal = "The executable Move boundary supports regular files only. Directory Move remains disabled.";
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
            MoveProgressBar.Value = Math.Clamp(snapshot.CompletedEntryCount, 0, snapshot.TotalEntryCount);
        }
    }

    private void CompleteMoveProgressPresentation(
        FileOperationExecutionSnapshot? snapshot,
        FileOperationActionHistory? history)
    {
        if (snapshot is not null)
        {
            UpdateMoveProgressPresentation(snapshot);
            MoveProgressBar.IsIndeterminate = false;
            return;
        }

        if (history is not null)
        {
            MoveProgressBar.Visibility = Visibility.Visible;
            MoveProgressBar.IsIndeterminate = false;
            MoveProgressBar.Maximum = Math.Max(1, history.Entries.Count);
            MoveProgressBar.Value = history.Entries.Count(static entry =>
                entry.State is FileOperationActionEntryState.Committed or
                    FileOperationActionEntryState.Skipped or
                    FileOperationActionEntryState.Failed or
                    FileOperationActionEntryState.RecoveryRequired);
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
                "The indexed backing source changed while a same-volume Move was already active. Non-running plans were discarded; the active Move remains governed only by its direct filesystem identities/durable history and will not be replayed automatically.";
        }
        else if (_copyExecutionRunning)
        {
            SetCopyExecutionUiBusy(true);
        }
    }

    private static string FormatMoveExecutionProgress(FileOperationExecutionSnapshot snapshot) =>
        snapshot.State switch
        {
            FileOperationExecutionState.Planned => "Move is preparing its reviewed execution boundary.",
            FileOperationExecutionState.Validating =>
                "Move is revalidating canonical paths, source identity and same-volume/local strategy. No rename has occurred.",
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

    private static string FormatMoveHistoryCounts(FileOperationActionHistory history)
    {
        var committed = history.Entries.Count(static entry => entry.State == FileOperationActionEntryState.Committed);
        var skipped = history.Entries.Count(static entry => entry.State == FileOperationActionEntryState.Skipped);
        var failed = history.Entries.Count(static entry => entry.State == FileOperationActionEntryState.Failed);
        var recovery = history.Entries.Count(static entry =>
            entry.State is FileOperationActionEntryState.MutationStarted or FileOperationActionEntryState.RecoveryRequired);
        return $"{committed:N0} committed, {skipped:N0} skipped, {failed:N0} failed, {recovery:N0} recovery-sensitive.";
    }
}
