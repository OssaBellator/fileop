using System;
using System.IO;
using System.Linq;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.UI.Xaml;

namespace FileOp.App;

public sealed partial class FilesView
{
    private const string FileOperationHistoryDatabaseName = "file-operation-actions.sqlite";

    private bool _copyExecutionUiInitialized;
    private bool _copyExecutionRunning;
    private Guid? _activeCopyOperationId;

    private void RunQueuedCopyButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (_copyExecutionUiInitialized)
        {
            return;
        }

        _copyExecutionUiInitialized = true;
        RunQueuedCopyButton.Click += RunQueuedCopyButton_Click;
        OperationQueueList.SelectionChanged += CopyOperationQueueList_SelectionChanged;
        LeftPane.IntentStateChanged += CopyPane_IntentStateChanged;
        RightPane.IntentStateChanged += CopyPane_IntentStateChanged;
        UpdateCopyExecutionAvailability();
    }

    private void CopyOperationQueueList_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e) =>
        UpdateCopyExecutionAvailability();

    private void CopyPane_IntentStateChanged(object? sender, EventArgs e) =>
        UpdateCopyExecutionAvailability();

    private async void RunQueuedCopyButton_Click(object sender, RoutedEventArgs e) =>
        await RunSelectedCopyAsync();

    private async Task RunSelectedCopyAsync()
    {
        if (_copyExecutionRunning ||
            OperationQueueList.SelectedItem is not FileBrowserQueuedOperationRow row)
        {
            return;
        }

        var plan = _queuedOperations.FirstOrDefault(operation => operation.Id == row.Id);
        if (plan is null)
        {
            RefreshQueuePresentation();
            UpdateCopyExecutionAvailability();
            return;
        }

        if (!CanRunCopyPlan(plan, out var refusal))
        {
            QueueStatusText.Text = refusal;
            UpdateCopyExecutionAvailability();
            return;
        }

        _copyExecutionRunning = true;
        _activeCopyOperationId = plan.Id;
        SetCopyExecutionUiBusy(true);
        QueueStatusText.Text =
            $"Copy {plan.Id} is revalidating the exact current source/destination objects before durable history or mutation. " +
            "The indexing helper is not used for this filesystem mutation.";

        var executorInvoked = false;
        FileOperationExecutionSnapshot? finalSnapshot = null;
        try
        {
            using var historyStore = new SqliteFileOperationActionHistoryStore(
                GetFileOperationHistoryDatabasePath());
            var executor = new FileCopyOperationExecutor(
                new WindowsFileOperationExecutionValidator(),
                historyStore,
                new WindowsFileCopyMutationPrimitive());
            var progress = new Progress<FileOperationExecutionSnapshot>(snapshot =>
            {
                if (_copyExecutionRunning && _activeCopyOperationId == plan.Id)
                {
                    QueueStatusText.Text = FormatCopyExecutionProgress(snapshot);
                }
            });

            executorInvoked = true;
            finalSnapshot = await executor.ExecuteAsync(plan, progress);
        }
        catch (Exception exception)
        {
            QueueStatusText.Text = executorInvoked
                ? $"Copy execution ended unexpectedly: {exception.Message} The exact queued operation will not be reused; inspect durable action history before creating a fresh plan."
                : $"Copy could not start: {exception.Message} No Copy executor was invoked and no filesystem mutation was requested.";
        }
        finally
        {
            _activeCopyOperationId = null;
            _copyExecutionRunning = false;
        }

        if (executorInvoked)
        {
            // A FileCopyOperationExecutor plan ID is single-use once execution can
            // reach durable history. Never offer the same immutable operation ID
            // for replay, including after recovery-sensitive or ambiguous failure.
            _queuedOperations.RemoveAll(operation => operation.Id == plan.Id);
            _preflightSnapshots.Remove(plan.Id);
        }

        if (finalSnapshot is not null)
        {
            QueueStatusText.Text = FormatCopyExecutionTerminal(finalSnapshot);
        }

        RefreshQueuePresentation();
        RequestRefreshForCopyDestination(plan.Intent.DestinationDirectoryPath);
        SetCopyExecutionUiBusy(false);
        UpdateCopyExecutionAvailability();
    }

    private bool CanRunCopyPlan(FileOperationPlan plan, out string refusal)
    {
        if (plan.Kind != FileOperationKind.Copy)
        {
            refusal = "Only regular-file Copy execution is wired in this slice. Move execution remains disabled.";
            return false;
        }

        if (plan.Intent.Entries.Count == 0 || plan.Intent.Entries.Any(static entry => entry.IsDirectory))
        {
            refusal = "This Copy executor slice supports regular files only. Directory Copy remains disabled.";
            return false;
        }

        if (!_preflightSnapshots.TryGetValue(plan.Id, out var preflight) ||
            !ReferenceEquals(preflight.Result.Plan, plan))
        {
            refusal = "Run read-only preflight for this exact queued Copy plan before execution.";
            return false;
        }

        if (preflight.Result.Status != FileOperationPreflightStatus.Ready)
        {
            refusal = preflight.Result.Status == FileOperationPreflightStatus.NeedsDecision
                ? "This Copy still needs an explicit collision decision. Requeue it with Skip existing or Stop on collision, or wait for the separately reviewed decision UX."
                : $"This Copy is blocked by read-only preflight: {preflight.Result.Summary}";
            return false;
        }

        if (!IsPlanBoundToCurrentFilesState(plan))
        {
            refusal =
                "The active pane/tab/path context no longer matches this queued Copy plan. Create and preflight a fresh plan from the current Files state.";
            return false;
        }

        refusal = string.Empty;
        return true;
    }

    private bool IsPlanBoundToCurrentFilesState(FileOperationPlan plan)
    {
        var intent = plan.Intent;
        if (!TryGetPane(intent.SourcePane, out var source) ||
            !TryGetPane(intent.DestinationPane, out var destination) ||
            ReferenceEquals(source, destination) ||
            !source.IsDirectoryReady ||
            !destination.IsDirectoryReady ||
            source.ActiveTabId != intent.SourceTabId ||
            destination.ActiveTabId != intent.DestinationTabId ||
            source.CurrentPath is not { } sourcePath ||
            destination.CurrentPath is not { } destinationPath)
        {
            return false;
        }

        try
        {
            return PathsEqual(sourcePath, intent.SourceDirectoryPath) &&
                PathsEqual(destinationPath, intent.DestinationDirectoryPath);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private bool TryGetPane(string paneName, out FilesPaneView pane)
    {
        if (string.Equals(paneName, LeftPane.PaneTitle, StringComparison.Ordinal))
        {
            pane = LeftPane;
            return true;
        }

        if (string.Equals(paneName, RightPane.PaneTitle, StringComparison.Ordinal))
        {
            pane = RightPane;
            return true;
        }

        pane = null!;
        return false;
    }

    private void RequestRefreshForCopyDestination(string destinationDirectoryPath)
    {
        foreach (var pane in new[] { LeftPane, RightPane })
        {
            if (!pane.IsDirectoryReady || pane.CurrentPath is not { } currentPath)
            {
                continue;
            }

            try
            {
                if (PathsEqual(currentPath, destinationDirectoryPath))
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

    private void SetCopyExecutionUiBusy(bool busy)
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
            RemoveQueuedOperationButton.IsEnabled = false;
            ClearQueueButton.IsEnabled = false;
            RunQueuedCopyButton.IsEnabled = false;
            return;
        }

        CollisionPolicyBox.IsEnabled = true;
        UpdateIntentAvailability();
        UpdateQueueActions();
    }

    private void UpdateCopyExecutionAvailability()
    {
        if (!_copyExecutionUiInitialized)
        {
            return;
        }

        if (_copyExecutionRunning)
        {
            // Base pane/queue handlers may recalculate their own controls when
            // selection or source state changes. Reassert Copy's exclusive UI
            // ownership last so no second plan/preflight can be started while the
            // reviewed executor is active.
            SetCopyExecutionUiBusy(true);
            return;
        }

        if (OperationQueueList.SelectedItem is not FileBrowserQueuedOperationRow row)
        {
            RunQueuedCopyButton.IsEnabled = false;
            return;
        }

        var plan = _queuedOperations.FirstOrDefault(operation => operation.Id == row.Id);
        RunQueuedCopyButton.IsEnabled = plan is not null && CanRunCopyPlan(plan, out _);
    }

    public void ResetOperationPlanningForSourceChange()
    {
        ClearPreparedIntent();

        if (_copyExecutionRunning && _activeCopyOperationId is Guid activeId)
        {
            _queuedOperations.RemoveAll(operation => operation.Id != activeId);
            foreach (var operationId in _preflightSnapshots.Keys.Where(id => id != activeId).ToArray())
            {
                _preflightSnapshots.Remove(operationId);
            }

            QueueStatusText.Text =
                "The indexed backing source changed. Non-running plans were discarded. The already-started Copy remains governed by its direct filesystem identity/history boundary and will not be replayed automatically.";
        }
        else
        {
            _queuedOperations.Clear();
            _preflightSnapshots.Clear();
            QueueStatusText.Text =
                "The indexed backing source changed. Prepared and queued operation plans were discarded; create a fresh plan from the current Files source.";
        }

        RefreshQueuePresentation();
        UpdateIntentAvailability();
        UpdateCopyExecutionAvailability();
    }

    private static string GetFileOperationHistoryDatabasePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileOp",
            "Control",
            FileOperationHistoryDatabaseName);

    private static string FormatCopyExecutionProgress(FileOperationExecutionSnapshot snapshot) =>
        snapshot.State switch
        {
            FileOperationExecutionState.Planned => "Copy is preparing its reviewed execution boundary.",
            FileOperationExecutionState.Validating =>
                "Copy is revalidating canonical source/destination paths and stable identities. No mutation has been authorized by the UI.",
            FileOperationExecutionState.Running =>
                $"Copy running: {snapshot.CompletedEntryCount:N0}/{snapshot.TotalEntryCount:N0} entries reached durable progress" +
                (string.IsNullOrWhiteSpace(snapshot.CurrentPath) ? "." : $" · {snapshot.CurrentPath}"),
            FileOperationExecutionState.CancellationRequested =>
                $"Copy cancellation is pending a safe boundary: {snapshot.CompletedEntryCount:N0}/{snapshot.TotalEntryCount:N0} entries settled.",
            FileOperationExecutionState.Succeeded =>
                $"Copy completed: {snapshot.CompletedEntryCount:N0}/{snapshot.TotalEntryCount:N0} entries settled.",
            FileOperationExecutionState.Cancelled =>
                $"Copy cancelled at a safe boundary after {snapshot.CompletedEntryCount:N0}/{snapshot.TotalEntryCount:N0} entries.",
            FileOperationExecutionState.Failed =>
                $"Copy failed: {snapshot.Failure?.Message ?? "The reviewed executor reported a failure."}",
            _ => snapshot.State.ToString(),
        };

    private static string FormatCopyExecutionTerminal(FileOperationExecutionSnapshot snapshot) =>
        snapshot.State switch
        {
            FileOperationExecutionState.Succeeded =>
                $"Copy completed through the reviewed durable executor: {snapshot.CompletedEntryCount:N0}/{snapshot.TotalEntryCount:N0} entries settled. Matching destination panes are refreshing.",
            FileOperationExecutionState.Cancelled =>
                $"Copy cancelled at a safe boundary after {snapshot.CompletedEntryCount:N0}/{snapshot.TotalEntryCount:N0} entries. The same operation ID will not be reused.",
            FileOperationExecutionState.Failed =>
                $"Copy failed: {snapshot.Failure?.Message ?? "The reviewed executor reported a failure."} " +
                "Durable history remains recovery evidence only; FileOp will not automatically replay this operation.",
            _ => FormatCopyExecutionProgress(snapshot),
        };
}
