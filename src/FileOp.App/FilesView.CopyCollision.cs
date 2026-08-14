using FileOp.Core.Operations;
using Microsoft.UI.Xaml;

namespace FileOp.App;

public sealed partial class FilesView
{
    private bool _copyCollisionResolutionUiInitialized;

    private void ResolveCopyCollisionSkipButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (_copyCollisionResolutionUiInitialized)
        {
            return;
        }

        _copyCollisionResolutionUiInitialized = true;
        OperationQueueList.SelectionChanged += CopyCollisionOperationQueueList_SelectionChanged;
        LeftPane.IntentStateChanged += CopyCollisionPane_IntentStateChanged;
        RightPane.IntentStateChanged += CopyCollisionPane_IntentStateChanged;
        UpdateCopyCollisionResolutionAvailability();
    }

    private void CopyCollisionOperationQueueList_SelectionChanged(
        object sender,
        Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e) =>
        UpdateCopyCollisionResolutionAvailability();

    private void CopyCollisionPane_IntentStateChanged(object? sender, EventArgs e) =>
        UpdateCopyCollisionResolutionAvailability();

    private void ResolveCopyCollisionSkipButton_Click(object sender, RoutedEventArgs e) =>
        ResolveSelectedCopyCollision(FileOperationCollisionPolicy.Skip);

    private void ResolveCopyCollisionStopButton_Click(object sender, RoutedEventArgs e) =>
        ResolveSelectedCopyCollision(FileOperationCollisionPolicy.Stop);

    private void ResolveSelectedCopyCollision(FileOperationCollisionPolicy resolvedPolicy)
    {
        if (_copyExecutionRunning ||
            resolvedPolicy is not FileOperationCollisionPolicy.Skip and
                not FileOperationCollisionPolicy.Stop ||
            OperationQueueList.SelectedItem is not FileBrowserQueuedOperationRow row)
        {
            return;
        }

        var index = _queuedOperations.FindIndex(operation => operation.Id == row.Id);
        if (index < 0)
        {
            RefreshQueuePresentation();
            UpdateCopyCollisionResolutionAvailability();
            return;
        }

        var original = _queuedOperations[index];
        if (!CanResolveCopyCollision(original))
        {
            QueueStatusText.Text =
                "The selected Copy no longer has a current Ask-later collision decision to resolve. Run fresh preflight if the filesystem or pane context changed.";
            UpdateCopyCollisionResolutionAvailability();
            return;
        }

        var resolved = new FileOperationPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            original.Kind,
            resolvedPolicy,
            original.Intent);

        _queuedOperations[index] = resolved;
        _preflightSnapshots.Remove(original.Id);

        QueueStatusText.Text = resolvedPolicy == FileOperationCollisionPolicy.Skip
            ? "Created a fresh immutable Copy plan with Skip existing. The old Ask-later preflight was discarded; run fresh preflight before execution can become available."
            : "Created a fresh immutable Copy plan with Stop on collision. The old Ask-later preflight was discarded; fresh preflight will remain blocked if any collision still exists.";

        RefreshQueuePresentation();
        if (OperationQueueList.Items
            .OfType<FileBrowserQueuedOperationRow>()
            .FirstOrDefault(item => item.Id == resolved.Id) is { } resolvedRow)
        {
            OperationQueueList.SelectedItem = resolvedRow;
        }

        UpdateCopyCollisionResolutionAvailability();
        UpdateCopyExecutionAvailability();
    }

    private bool CanResolveCopyCollision(FileOperationPlan plan)
    {
        if (_copyExecutionRunning ||
            plan.Kind != FileOperationKind.Copy ||
            plan.CollisionPolicy != FileOperationCollisionPolicy.Ask ||
            plan.Intent.Entries.Count == 0 ||
            plan.Intent.Entries.Any(static entry => entry.IsDirectory) ||
            !_preflightSnapshots.TryGetValue(plan.Id, out var preflight) ||
            !ReferenceEquals(preflight.Result.Plan, plan) ||
            preflight.Result.Status != FileOperationPreflightStatus.NeedsDecision ||
            preflight.Result.NeedsDecisionCount == 0 ||
            preflight.Result.BlockedCount != 0)
        {
            return false;
        }

        return IsPlanBoundToCurrentFilesState(plan);
    }

    private void UpdateCopyCollisionResolutionAvailability()
    {
        if (!_copyCollisionResolutionUiInitialized ||
            _copyExecutionRunning ||
            OperationQueueList.SelectedItem is not FileBrowserQueuedOperationRow row)
        {
            SetCopyCollisionResolutionVisible(false);
            return;
        }

        var plan = _queuedOperations.FirstOrDefault(operation => operation.Id == row.Id);
        var canResolve = plan is not null && CanResolveCopyCollision(plan);
        SetCopyCollisionResolutionVisible(canResolve);
    }

    private void SetCopyCollisionResolutionVisible(bool visible)
    {
        CopyCollisionDecisionPanel.Visibility = visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        ResolveCopyCollisionSkipButton.IsEnabled = visible;
        ResolveCopyCollisionStopButton.IsEnabled = visible;
    }
}
