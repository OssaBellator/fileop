using System;
using System.Linq;
using System.Threading.Tasks;
using FileOp.Core.Operations;
using Microsoft.UI.Xaml;

namespace FileOp.App;

public sealed partial class FilesView
{
    private bool _directoryMoveExecutionUiInitialized;

    private void RunQueuedDirectoryMoveButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (_directoryMoveExecutionUiInitialized)
        {
            return;
        }

        _directoryMoveExecutionUiInitialized = true;
        RunQueuedDirectoryMoveButton.Click += RunQueuedDirectoryMoveButton_Click;
        OperationQueueList.SelectionChanged += DirectoryMoveOperationQueueList_SelectionChanged;
        LeftPane.IntentStateChanged += DirectoryMovePane_IntentStateChanged;
        RightPane.IntentStateChanged += DirectoryMovePane_IntentStateChanged;
        UpdateDirectoryMoveExecutionAvailability();
    }

    private void DirectoryMoveOperationQueueList_SelectionChanged(
        object sender,
        Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e) =>
        UpdateDirectoryMoveExecutionAvailability();

    private void DirectoryMovePane_IntentStateChanged(object? sender, EventArgs e) =>
        UpdateDirectoryMoveExecutionAvailability();

    private async void RunQueuedDirectoryMoveButton_Click(object sender, RoutedEventArgs e) =>
        await RunSelectedDirectoryMoveAsync();

    private async Task RunSelectedDirectoryMoveAsync()
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
            UpdateDirectoryMoveExecutionAvailability();
            return;
        }

        if (!CanAttemptDirectoryMovePlan(plan, out var refusal))
        {
            QueueStatusText.Text = refusal;
            UpdateDirectoryMoveExecutionAvailability();
            return;
        }

        FileOperationExecutionValidationResult executionValidation;
        try
        {
            executionValidation = await new WindowsMoveOperationExecutionValidator()
                .ValidateAsync(plan);
        }
        catch (Exception exception)
        {
            QueueStatusText.Text =
                $"Directory Move execution-grade classification could not complete: {exception.Message} No durable directory history or filesystem mutation was attempted.";
            return;
        }

        if (_queuedOperations.All(operation => operation.Id != plan.Id) ||
            !IsPlanBoundToCurrentFilesState(plan))
        {
            QueueStatusText.Text =
                "The queued directory Move changed or its pane/tab/path context became stale during classification. Run fresh preflight on a current plan.";
            _preflightSnapshots.Remove(plan.Id);
            RefreshQueuePresentation();
            UpdateDirectoryMoveExecutionAvailability();
            return;
        }

        var strategy = DirectorySameVolumeMoveExecutionStrategyClassifier.Classify(executionValidation);
        if (strategy.Strategy == DirectorySameVolumeMoveExecutionStrategy.Blocked)
        {
            QueueStatusText.Text =
                $"Directory Move cannot execute through the reviewed same-volume boundary: {strategy.Summary} The queued plan was not consumed; rerun preflight after correcting the condition.";
            _preflightSnapshots.Remove(plan.Id);
            RefreshQueuePresentation();
            UpdateDirectoryMoveExecutionAvailability();
            return;
        }

        if (strategy.Strategy is not DirectorySameVolumeMoveExecutionStrategy.SameVolumeDirectoryRenameRequired and
            not DirectorySameVolumeMoveExecutionStrategy.SkipOnly)
        {
            QueueStatusText.Text =
                "Directory Move classification returned an unsupported execution strategy. No mutation was attempted.";
            return;
        }

        _moveExecutionRunning = true;
        _moveCancellationRequested = false;
        _activeMoveOperationId = plan.Id;
        _activeMoveExecutor = null;
        RunQueuedDirectoryMoveButton.IsEnabled = false;
        BeginMoveProgressPresentation(plan.Intent.Entries.Count);
        SetMoveExecutionUiBusy(true);
        QueueStatusText.Text = strategy.Strategy == DirectorySameVolumeMoveExecutionStrategy.SkipOnly
            ? $"Directory Move {plan.Id} contains only explicit Skip entries. The directory-specific executor will record durable no-mutation settlement."
            : $"Directory Move {plan.Id} is entering the reviewed same-volume identity-preserving directory rename transaction. Fresh validation still runs again before durable history and before every rename.";

        var executorInvoked = false;
        FileOperationExecutionSnapshot? finalSnapshot = null;
        DirectorySameVolumeMoveActionHistory? finalHistory = null;
        Exception? executionException = null;
        Exception? historyReadException = null;

        try
        {
            using var historyStore = new SqliteDirectorySameVolumeMoveActionHistoryStore(
                GetFileOperationHistoryDatabasePath());
            var executor = new DirectorySameVolumeMoveOperationExecutor(
                new WindowsMoveOperationExecutionValidator(),
                historyStore,
                new WindowsDirectorySameVolumeMoveMutationPrimitive());
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
            // Directory Move operation IDs are single-use once the reviewed executor is invoked,
            // even if durable history cannot subsequently be read.
            _queuedOperations.RemoveAll(operation => operation.Id == plan.Id);
            _preflightSnapshots.Remove(plan.Id);
        }

        CompleteDirectoryMoveProgressPresentation(finalSnapshot, finalHistory);
        QueueStatusText.Text = FormatDirectoryMoveExecutionOutcome(
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
        UpdateDirectoryMoveExecutionAvailability();
    }

    private bool CanAttemptDirectoryMovePlan(FileOperationPlan plan, out string refusal)
    {
        if (_preflightRunning)
        {
            refusal = "Wait for the current read-only preflight to finish before running Directory Move.";
            return false;
        }

        if (plan.Kind != FileOperationKind.Move)
        {
            refusal = "Select a queued Move plan to use the Directory Move executor.";
            return false;
        }

        if (plan.Intent.Entries.Count == 0)
        {
            refusal = "Directory Move requires at least one queued directory entry.";
            return false;
        }

        if (!plan.Intent.Entries.All(static entry => entry.IsDirectory))
        {
            refusal = plan.Intent.Entries.Any(static entry => entry.IsDirectory)
                ? "Mixed file-and-directory Move batches remain unsupported because file and directory rename use separate durable history schemas. Queue homogeneous plans instead."
                : "Select a directory-only Move plan to use the Directory Move executor.";
            return false;
        }

        if (!_preflightSnapshots.TryGetValue(plan.Id, out var preflight) ||
            !ReferenceEquals(preflight.Result.Plan, plan))
        {
            refusal = "Run read-only preflight for this exact queued directory Move plan before execution.";
            return false;
        }

        if (preflight.Result.Status != FileOperationPreflightStatus.Ready)
        {
            refusal = preflight.Result.Status == FileOperationPreflightStatus.NeedsDecision
                ? "This directory Move still needs an explicit collision decision. Requeue with Skip existing or Stop on collision; overwrite/replacement remains unsupported."
                : $"This directory Move is blocked by read-only preflight: {preflight.Result.Summary}";
            return false;
        }

        if (!IsPlanBoundToCurrentFilesState(plan))
        {
            refusal =
                "The active pane/tab/path context no longer matches this queued directory Move plan. Create and preflight a fresh plan from current Files state.";
            return false;
        }

        refusal = string.Empty;
        return true;
    }

    private void UpdateDirectoryMoveExecutionAvailability()
    {
        if (!_directoryMoveExecutionUiInitialized)
        {
            return;
        }

        if (IsFileOperationExecutionBusy ||
            OperationQueueList.SelectedItem is not FileBrowserQueuedOperationRow row)
        {
            RunQueuedDirectoryMoveButton.IsEnabled = false;
            return;
        }

        var plan = _queuedOperations.FirstOrDefault(operation => operation.Id == row.Id);
        RunQueuedDirectoryMoveButton.IsEnabled =
            plan is not null && CanAttemptDirectoryMovePlan(plan, out _);
    }

    private void CompleteDirectoryMoveProgressPresentation(
        FileOperationExecutionSnapshot? snapshot,
        DirectorySameVolumeMoveActionHistory? history)
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
                entry.State is DirectorySameVolumeMoveActionEntryState.Committed or
                    DirectorySameVolumeMoveActionEntryState.Skipped or
                    DirectorySameVolumeMoveActionEntryState.Failed or
                    DirectorySameVolumeMoveActionEntryState.RecoveryRequired);
            return;
        }

        MoveProgressBar.IsIndeterminate = false;
        MoveProgressBar.Visibility = Visibility.Collapsed;
    }

    private static string FormatDirectoryMoveExecutionOutcome(
        FileOperationExecutionSnapshot? snapshot,
        DirectorySameVolumeMoveActionHistory? history,
        Exception? executionException,
        Exception? historyReadException,
        bool executorInvoked)
    {
        if (history?.RequiresRecovery == true)
        {
            return
                $"Directory Move requires recovery inspection. {FormatDirectoryMoveHistoryCounts(history)} " +
                "Directory MutationStarted/RecoveryRequired history is evidence only; FileOp will not replay, rollback or reinterpret the original operation ID automatically.";
        }

        if (history?.TerminalState == DirectorySameVolumeMoveActionTerminalState.Succeeded)
        {
            return
                $"Directory Move completed through the reviewed same-volume durable executor. {FormatDirectoryMoveHistoryCounts(history)} " +
                "Matching source and destination panes are refreshing.";
        }

        if (history?.TerminalState == DirectorySameVolumeMoveActionTerminalState.Cancelled)
        {
            return
                $"Directory Move cancelled at a reviewed safe boundary. {FormatDirectoryMoveHistoryCounts(history)} The operation ID is single-use.";
        }

        if (history?.TerminalState == DirectorySameVolumeMoveActionTerminalState.Failed)
        {
            return
                $"Directory Move failed before any unresolved mutation boundary remained. {FormatDirectoryMoveHistoryCounts(history)} " +
                (executionException is null ? string.Empty : executionException.Message);
        }

        if (history is not null && history.TerminalState is null)
        {
            return
                $"Directory Move durable history remains non-terminal. {FormatDirectoryMoveHistoryCounts(history)} Treat this as recovery evidence; no automatic retry or replay is authorized.";
        }

        if (historyReadException is not null)
        {
            return
                $"Directory Move execution settled but durable history could not be read: {historyReadException.Message} " +
                "The original operation ID will not be reused; inspect the directory Move action-history tables before further mutation.";
        }

        if (executionException is not null)
        {
            return executorInvoked
                ? $"Directory Move execution ended unexpectedly: {executionException.Message} The original operation ID was consumed conservatively."
                : $"Directory Move could not start: {executionException.Message} No reviewed directory executor or filesystem mutation was invoked.";
        }

        return snapshot is not null
            ? FormatMoveExecutionProgress(snapshot)
            : "Directory Move did not produce an execution result. No additional mutation authority was created.";
    }

    private static string FormatDirectoryMoveHistoryCounts(DirectorySameVolumeMoveActionHistory history)
    {
        var committed = history.Entries.Count(static entry => entry.State == DirectorySameVolumeMoveActionEntryState.Committed);
        var skipped = history.Entries.Count(static entry => entry.State == DirectorySameVolumeMoveActionEntryState.Skipped);
        var failed = history.Entries.Count(static entry => entry.State == DirectorySameVolumeMoveActionEntryState.Failed);
        var recovery = history.Entries.Count(static entry =>
            entry.State is DirectorySameVolumeMoveActionEntryState.MutationStarted or
                DirectorySameVolumeMoveActionEntryState.RecoveryRequired);
        return $"{committed:N0} committed, {skipped:N0} skipped, {failed:N0} failed, {recovery:N0} recovery-sensitive.";
    }
}
