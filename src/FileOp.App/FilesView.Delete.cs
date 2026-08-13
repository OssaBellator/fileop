using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class FilesView
{
    private readonly IFileDeleteOperationPreflightValidator _deletePreflightValidator =
        new WindowsFileDeleteOperationPreflightValidator();
    private readonly IFileDeleteOperationExecutionValidator _deleteExecutionValidator =
        new WindowsFileDeleteOperationExecutionValidator();
    private readonly IFileDeleteOperationUserAuthorizationIssuer _deleteAuthorizationIssuer =
        new FileDeleteOperationUserAuthorizationIssuer();

    private FileDeleteOperationFinalLeaseReleaseException? _pendingDeleteLeaseRelease;
    private bool _deleteFeatureInitialized;
    private bool _deleteSessionRunning;

    public event EventHandler<FilesDeleteSessionFinishedEventArgs>? DeleteSessionFinished;

    private void FilesView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_deleteFeatureInitialized)
        {
            UpdateDeleteAvailability();
            return;
        }

        _deleteFeatureInitialized = true;
        LeftPane.IntentStateChanged += DeletePane_IntentStateChanged;
        RightPane.IntentStateChanged += DeletePane_IntentStateChanged;
        if (Application.Current is App app && app.MainWindow is { } window)
        {
            DeleteSessionFinished += window.FilesView_DeleteSessionFinished;
            window.Closed += FilesDeleteHostWindow_Closed;
        }
        UpdateDeleteAvailability();
    }

    private async void FilesDeleteHostWindow_Closed(object sender, WindowEventArgs args) =>
        await TryReleasePendingDeleteCleanupAsync();

    private void DeletePane_IntentStateChanged(object? sender, EventArgs e) =>
        UpdateDeleteAvailability();

    private async void ReviewDeleteLeftButton_Click(object sender, RoutedEventArgs e) =>
        await RunDeleteSessionAsync(LeftPane, RightPane);

    private async void ReviewDeleteRightButton_Click(object sender, RoutedEventArgs e) =>
        await RunDeleteSessionAsync(RightPane, LeftPane);

    private async void RetryDeleteCleanupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_deleteSessionRunning ||
            _pendingDeleteLeaseRelease is not { FinalLeaseReleasePending: true } pending)
        {
            UpdateDeleteAvailability();
            return;
        }

        RetryDeleteCleanupButton.IsEnabled = false;
        DeleteStatusText.Text =
            "Retrying release of the retained cleanup-only final delete capability. This does not authorize another filesystem mutation.";
        try
        {
            await pending.RetryFinalLeaseReleaseAsync();
            if (!pending.FinalLeaseReleasePending)
            {
                _pendingDeleteLeaseRelease = null;
                DeleteStatusText.Text =
                    "The retained final delete capability was released. Durable recovery history still requires explicit reconciliation before another delete session can start.";
            }
        }
        catch (Exception exception)
        {
            DeleteStatusText.Text =
                $"Cleanup-only final capability release is still pending: {exception.Message} No new mutation authority was created.";
        }
        finally
        {
            UpdateDeleteAvailability();
        }
    }

    internal async ValueTask TryReleasePendingDeleteCleanupAsync()
    {
        var pending = _pendingDeleteLeaseRelease;
        if (pending is null || !pending.FinalLeaseReleasePending)
        {
            _pendingDeleteLeaseRelease = null;
            return;
        }

        try
        {
            await pending.RetryFinalLeaseReleaseAsync();
            if (!pending.FinalLeaseReleasePending)
            {
                _pendingDeleteLeaseRelease = null;
            }
        }
        catch
        {
            // Process teardown will close the retained OS handle. Durable recovery-sensitive
            // history remains the restart-time authority; shutdown must not recreate mutation.
        }
    }

    private async Task RunDeleteSessionAsync(
        FilesPaneView source,
        FilesPaneView other)
    {
        if (_deleteSessionRunning)
        {
            return;
        }
        if (_pendingDeleteLeaseRelease is { FinalLeaseReleasePending: true })
        {
            DeleteStatusText.Text =
                "A prior delete session still owns cleanup-only final capability release. Retry that cleanup before reviewing another deletion.";
            UpdateDeleteAvailability();
            return;
        }
        if (!TryCreateDeletePlan(source, other, out var plan, out var captureError))
        {
            DeleteStatusText.Text = captureError;
            UpdateDeleteAvailability();
            return;
        }

        var sourceDirectoryPath = plan.Intent.SourceDirectoryPath;
        var sourcePane = plan.Intent.SourcePane;
        var sourceTabId = plan.Intent.SourceTabId;
        var historyBegun = false;
        _deleteSessionRunning = true;
        UpdateDeleteAvailability();

        try
        {
            var historyPath = GetDeleteHistoryDatabasePath();
            DeleteStatusText.Text =
                "Checking durable delete recovery history before starting a new destructive session.";
            var recovery = await GetRecoveryCandidateAsync(historyPath);
            if (recovery is not null)
            {
                DeleteStatusText.Text =
                    $"Delete review is blocked because operation {recovery.OperationId:D} retains recovery-sensitive durable history. FileOp will not mint new delete consent or replay mutation until that history is explicitly reconciled.";
                return;
            }

            DeleteStatusText.Text =
                $"Read-only delete preflight is checking {plan.Intent.Entries.Count:N0} captured file{(plan.Intent.Entries.Count == 1 ? string.Empty : "s")}. No deletion is authorized.";
            var preflight = await _deletePreflightValidator
                .ValidateAsync(plan, CancellationToken.None);
            if (!preflight.IsReadyForFurtherReview)
            {
                DeleteStatusText.Text =
                    $"Delete preflight blocked this selection. {preflight.Summary}";
                return;
            }

            DeleteStatusText.Text =
                "Read-only execution validation is resolving canonical paths, exact file identities and protected-location policy before authorization review.";
            var validation = await _deleteExecutionValidator
                .ValidateAsync(plan, CancellationToken.None);
            if (!validation.CanRequestAuthorizationReview)
            {
                DeleteStatusText.Text =
                    $"Delete authorization review is blocked. {validation.Summary}";
                return;
            }

            var confirmed = await ShowDeleteConfirmationAsync(validation);
            if (!confirmed)
            {
                DeleteStatusText.Text =
                    "Delete review was cancelled. No user-authorization receipt was issued and no filesystem mutation was attempted.";
                return;
            }

            // Confirmation can remain open while another process changes durable recovery state.
            // Recheck immediately before minting the session authorization receipt.
            recovery = await GetRecoveryCandidateAsync(historyPath);
            if (recovery is not null)
            {
                DeleteStatusText.Text =
                    $"Delete authorization was not issued because recovery-sensitive operation {recovery.OperationId:D} appeared while confirmation was open. No new mutation was attempted.";
                return;
            }

            var authorization = _deleteAuthorizationIssuer
                .IssueAfterExplicitUserConfirmation(validation);

            await using var historyStore =
                new SqliteFileDeleteOperationActionHistoryStore(historyPath);
            await historyStore.BeginAsync(authorization, CancellationToken.None);
            historyBegun = true;

            DeleteStatusText.Text =
                "Explicit authorization is recorded. FileOp is executing the reviewed identity-bound same-handle delete pipeline; durable history controls recovery and replay safety.";

            var protectedLocationPolicy = new WindowsFileDeleteProtectedLocationPolicy();
            var result = await FileDeleteOperationOrchestrator.ExecuteAsync(
                authorization,
                new WindowsFileDeleteOperationStabilityLeaseProvider(protectedLocationPolicy),
                new WindowsFileDeleteOperationFinalMutationLeaseProvider(protectedLocationPolicy),
                historyStore,
                CancellationToken.None);

            DeleteStatusText.Text =
                $"Delete session completed with durable {result.CompletedHistory.TerminalState} history. " +
                $"This invocation mutated {result.MutatedEntryCount:N0} file{(result.MutatedEntryCount == 1 ? string.Empty : "s")}; " +
                "no reusable mutation authority remains.";
        }
        catch (FileDeleteOperationFinalLeaseReleaseException releaseException)
        {
            _pendingDeleteLeaseRelease = releaseException;
            try
            {
                await releaseException.RetryFinalLeaseReleaseAsync();
                if (!releaseException.FinalLeaseReleasePending)
                {
                    _pendingDeleteLeaseRelease = null;
                    DeleteStatusText.Text =
                        "A post-mutation final-lease release problem entered recovery-sensitive history, and the cleanup-only release retry succeeded. Explicit recovery reconciliation is still required before another delete session.";
                }
            }
            catch (Exception retryException)
            {
                DeleteStatusText.Text =
                    $"A post-mutation final capability still requires cleanup-only release: {retryException.Message} " +
                    "Use Retry pending cleanup. No new mutation authority is available.";
            }
        }
        catch (FileDeleteOperationOrchestrationRecoveryRequiredException recoveryException)
        {
            DeleteStatusText.Text =
                $"Delete orchestration stopped on recovery-sensitive durable history for operation {recoveryException.History.OperationId:D}. " +
                "FileOp will not automatically replay deletion or mint replacement mutation authority.";
        }
        catch (Exception exception)
        {
            DeleteStatusText.Text =
                $"Delete session stopped: {exception.Message} Durable history, if begun, remains the authority for any later recovery decision; FileOp did not auto-replay mutation.";
        }
        finally
        {
            if (historyBegun)
            {
                DeleteSessionFinished?.Invoke(
                    this,
                    new FilesDeleteSessionFinishedEventArgs(
                        sourcePane,
                        sourceTabId,
                        sourceDirectoryPath));
            }

            _deleteSessionRunning = false;
            UpdateDeleteAvailability();
        }
    }

    private bool TryCreateDeletePlan(
        FilesPaneView source,
        FilesPaneView other,
        out FileDeleteOperationPlan plan,
        out string error)
    {
        plan = null!;
        error = string.Empty;

        if (!source.IsDirectoryReady ||
            source.ActiveTabId == Guid.Empty ||
            string.IsNullOrWhiteSpace(source.CurrentPath))
        {
            error = "Delete review requires one ready source pane with a current directory and active tab.";
            return false;
        }
        if (source.SelectedCount == 0)
        {
            error = "Select one or more files in a single pane before reviewing deletion.";
            return false;
        }
        if (other.SelectedCount != 0)
        {
            error = "Delete review requires selection in exactly one pane. Clear the other pane selection first.";
            return false;
        }

        var selectedRows = source.SelectedRows;
        if (selectedRows.Count != source.SelectedCount)
        {
            error = "The current Files selection could not be captured exactly.";
            return false;
        }
        if (selectedRows.Any(static row => row.IsDirectory))
        {
            error = "Directory deletion is not supported. Select files only; recursion and directory recovery semantics remain out of scope.";
            return false;
        }

        var sourceDirectory = source.CurrentPath;
        try
        {
            foreach (var row in selectedRows)
            {
                var parent = Path.GetDirectoryName(NormalizeOperationPath(row.Path));
                if (string.IsNullOrWhiteSpace(parent) ||
                    !PathsEqual(parent, sourceDirectory))
                {
                    error =
                        $"The selected file is no longer a direct child of the current Files directory: {row.Name}. Refresh the pane before reviewing deletion.";
                    return false;
                }
            }
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"A selected file path is invalid and cannot be reviewed for deletion: {exception.Message}";
            return false;
        }

        var entries = selectedRows
            .Select(static row => new FileOperationEntry(
                row.Path,
                row.Name,
                IsDirectory: false))
            .ToArray();
        plan = new FileDeleteOperationPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new FileDeleteOperationIntent(
                source.PaneTitle,
                source.ActiveTabId,
                sourceDirectory,
                entries));
        return true;
    }

    private async Task<bool> ShowDeleteConfirmationAsync(
        FileDeleteOperationExecutionValidationResult validation)
    {
        var xamlRoot = XamlRoot
            ?? throw new InvalidOperationException(
                "Delete confirmation requires the Files view to be attached to a XAML root.");
        var paths = validation.Items
            .Select(static item => item.Source.CanonicalPath)
            .ToArray();
        var listedPaths = paths
            .Take(8)
            .Select(static path => $"• {path}")
            .ToList();
        if (paths.Length > listedPaths.Count)
        {
            listedPaths.Add($"• …and {paths.Length - listedPaths.Count:N0} more exact file path(s)");
        }

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text =
                $"This will permanently delete {paths.Length:N0} file{(paths.Length == 1 ? string.Empty : "s")} using the reviewed identity-bound same-handle pipeline. " +
                "This slice does not use the Recycle Bin and has no undo/restore action. Final leases will recheck the exact authorized file identities before mutation.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = string.Join(Environment.NewLine, listedPaths),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 240,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = paths.Length == 1 ? "Permanently delete this file?" : "Permanently delete these files?",
            Content = content,
            PrimaryButtonText = "Delete permanently",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private static string GetDeleteHistoryDatabasePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException(
                "Per-user local application data is unavailable; durable delete history cannot be stored safely.");
        }

        return Path.Combine(
            localAppData,
            "FileOp",
            "delete-action-history.sqlite");
    }

    private static async Task<FileDeleteOperationActionHistory?> GetRecoveryCandidateAsync(
        string historyPath)
    {
        if (!File.Exists(historyPath))
        {
            return null;
        }

        await using var reader = new SqliteFileDeleteOperationRecoveryHistoryReader(historyPath);
        var candidates = await reader.GetRecoveryCandidatesAsync(
            limit: 1,
            cancellationToken: CancellationToken.None);
        return candidates.Count == 0 ? null : candidates[0];
    }

    private void UpdateDeleteAvailability()
    {
        if (!_deleteFeatureInitialized)
        {
            return;
        }

        var cleanupPending = _pendingDeleteLeaseRelease is { FinalLeaseReleasePending: true };
        var canStart = !_deleteSessionRunning && !cleanupPending;
        ReviewDeleteLeftButton.IsEnabled =
            canStart && HasExclusiveFileSelection(LeftPane, RightPane);
        ReviewDeleteRightButton.IsEnabled =
            canStart && HasExclusiveFileSelection(RightPane, LeftPane);

        RetryDeleteCleanupButton.Visibility = cleanupPending
            ? Visibility.Visible
            : Visibility.Collapsed;
        RetryDeleteCleanupButton.IsEnabled = cleanupPending && !_deleteSessionRunning;
    }

    private static bool HasExclusiveFileSelection(
        FilesPaneView source,
        FilesPaneView other)
    {
        if (!source.IsDirectoryReady ||
            source.ActiveTabId == Guid.Empty ||
            string.IsNullOrWhiteSpace(source.CurrentPath) ||
            source.SelectedCount == 0 ||
            other.SelectedCount != 0)
        {
            return false;
        }

        var selectedRows = source.SelectedRows;
        return selectedRows.Count == source.SelectedCount &&
            selectedRows.All(static row => !row.IsDirectory);
    }
}

public sealed class FilesDeleteSessionFinishedEventArgs : EventArgs
{
    public FilesDeleteSessionFinishedEventArgs(
        string sourcePane,
        Guid sourceTabId,
        string sourceDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePane);
        if (sourceTabId == Guid.Empty)
        {
            throw new ArgumentException(
                "Delete session refresh requires a non-empty source tab ID.",
                nameof(sourceTabId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectoryPath);

        SourcePane = sourcePane;
        SourceTabId = sourceTabId;
        SourceDirectoryPath = sourceDirectoryPath;
    }

    public string SourcePane { get; }

    public Guid SourceTabId { get; }

    public string SourceDirectoryPath { get; }
}
