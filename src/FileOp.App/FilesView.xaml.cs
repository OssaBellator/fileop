using FileOp.Core.Operations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class FilesView : UserControl
{
    private readonly List<FileOperationPlan> _queuedOperations = [];
    private FileOperationIntent? _preparedIntent;

    public FilesView()
    {
        InitializeComponent();
        LeftPane.IntentStateChanged += Pane_IntentStateChanged;
        RightPane.IntentStateChanged += Pane_IntentStateChanged;
        CollisionPolicyBox.SelectedIndex = 0;
        UpdateIntentAvailability();
        RefreshQueuePresentation();
    }

    public FilesPaneView LeftPane => LeftPaneControl;

    public FilesPaneView RightPane => RightPaneControl;

    public FileOperationIntent? PreparedIntent => _preparedIntent;

    public IReadOnlyList<FileOperationPlan> QueuedOperations => _queuedOperations.AsReadOnly();

    public void SetSourceDescription(string description)
    {
        ScopeText.Text = description;
    }

    private void Pane_IntentStateChanged(object? sender, EventArgs e)
    {
        ClearPreparedIntent();
        UpdateIntentAvailability();
    }

    private void PrepareLeftToRightButton_Click(object sender, RoutedEventArgs e)
    {
        PrepareIntent(LeftPane, RightPane);
    }

    private void PrepareRightToLeftButton_Click(object sender, RoutedEventArgs e)
    {
        PrepareIntent(RightPane, LeftPane);
    }

    private void PrepareIntent(FilesPaneView source, FilesPaneView destination)
    {
        var sourcePath = source.CurrentPath;
        var destinationPath = destination.CurrentPath;
        var selectedRows = source.SelectedRows;
        if (!source.IsDirectoryReady ||
            !destination.IsDirectoryReady ||
            string.IsNullOrWhiteSpace(sourcePath) ||
            string.IsNullOrWhiteSpace(destinationPath) ||
            selectedRows.Count == 0)
        {
            ClearPreparedIntent();
            UpdateIntentAvailability();
            return;
        }

        var entries = selectedRows
            .Select(static row => new FileOperationEntry(
                row.Path,
                row.Name,
                row.IsDirectory))
            .ToArray();

        _preparedIntent = new FileOperationIntent(
            source.PaneTitle,
            source.ActiveTabId,
            sourcePath,
            Array.AsReadOnly(entries),
            destination.PaneTitle,
            destination.ActiveTabId,
            destinationPath);

        IntentText.Text =
            $"Prepared: {entries.Length:N0} selected entr{(entries.Length == 1 ? "y" : "ies")} " +
            $"from {source.PaneTitle} ({sourcePath}) → {destination.PaneTitle} ({destinationPath}). " +
            "Choose a collision policy and queue Copy or Move. Queueing does not touch the filesystem.";
        QueueStatusText.Text = string.Empty;
        UpdateIntentAvailability();
    }

    private void QueueCopyButton_Click(object sender, RoutedEventArgs e)
    {
        QueuePreparedIntent(FileOperationKind.Copy);
    }

    private void QueueMoveButton_Click(object sender, RoutedEventArgs e)
    {
        QueuePreparedIntent(FileOperationKind.Move);
    }

    private void QueuePreparedIntent(FileOperationKind kind)
    {
        var intent = _preparedIntent;
        if (intent is null || !TryGetCollisionPolicy(out var collisionPolicy))
        {
            UpdateIntentAvailability();
            return;
        }

        if (!TryValidateIntentForQueue(intent, out var validationMessage))
        {
            QueueStatusText.Text = validationMessage;
            return;
        }

        var queued = new FileOperationPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            kind,
            collisionPolicy,
            intent);
        _queuedOperations.Add(queued);

        QueueStatusText.Text =
            $"Queued {kind.ToString().ToLowerInvariant()} plan for {intent.Entries.Count:N0} " +
            $"entr{(intent.Entries.Count == 1 ? "y" : "ies")} with {FormatCollisionPolicy(collisionPolicy)}. " +
            "Execution remains disabled.";
        ClearPreparedIntent();
        RefreshQueuePresentation();
        UpdateIntentAvailability();
    }

    private void CollisionPolicyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateIntentAvailability();
    }

    private void OperationQueueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RemoveQueuedOperationButton.IsEnabled = OperationQueueList.SelectedItem is FileBrowserQueuedOperationRow;
    }

    private void RemoveQueuedOperationButton_Click(object sender, RoutedEventArgs e)
    {
        if (OperationQueueList.SelectedItem is not FileBrowserQueuedOperationRow row)
        {
            return;
        }

        _queuedOperations.RemoveAll(operation => operation.Id == row.Id);
        QueueStatusText.Text = "Removed the selected queued plan. No filesystem operation was executed.";
        RefreshQueuePresentation();
    }

    private void ClearQueueButton_Click(object sender, RoutedEventArgs e)
    {
        _queuedOperations.Clear();
        QueueStatusText.Text = "Cleared the planned operation queue. No filesystem operation was executed.";
        RefreshQueuePresentation();
    }

    private void RefreshQueuePresentation()
    {
        var rows = _queuedOperations
            .Select(FileBrowserQueuedOperationRow.FromOperation)
            .ToArray();
        OperationQueueList.ItemsSource = null;
        OperationQueueList.ItemsSource = rows;
        QueueCountText.Text = rows.Length == 0
            ? "No queued plans."
            : $"{rows.Length:N0} queued plan{(rows.Length == 1 ? string.Empty : "s")}.";
        ClearQueueButton.IsEnabled = rows.Length > 0;
        RemoveQueuedOperationButton.IsEnabled = false;
    }

    private void ClearPreparedIntent()
    {
        _preparedIntent = null;
        IntentText.Text =
            "Select one or more entries in a source pane, then prepare a direction. Queueing is planning only; no file operation is executed.";
    }

    private void UpdateIntentAvailability()
    {
        PrepareLeftToRightButton.IsEnabled = CanPrepareIntent(LeftPane, RightPane);
        PrepareRightToLeftButton.IsEnabled = CanPrepareIntent(RightPane, LeftPane);

        var canQueue = _preparedIntent is not null && TryGetCollisionPolicy(out _);
        QueueCopyButton.IsEnabled = canQueue;
        QueueMoveButton.IsEnabled = canQueue;
    }

    private bool TryGetCollisionPolicy(out FileOperationCollisionPolicy collisionPolicy)
    {
        if (CollisionPolicyBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
            Enum.TryParse(tag, ignoreCase: false, out collisionPolicy))
        {
            return true;
        }

        collisionPolicy = default;
        return false;
    }

    private static bool CanPrepareIntent(FilesPaneView source, FilesPaneView destination) =>
        source.IsDirectoryReady &&
        destination.IsDirectoryReady &&
        source.SelectedCount > 0 &&
        !string.IsNullOrWhiteSpace(source.CurrentPath) &&
        !string.IsNullOrWhiteSpace(destination.CurrentPath);

    private static bool TryValidateIntentForQueue(
        FileOperationIntent intent,
        out string validationMessage)
    {
        if (intent.Entries.Count == 0)
        {
            validationMessage = "Nothing is selected to queue.";
            return false;
        }

        try
        {
            if (PathsEqual(intent.SourceDirectoryPath, intent.DestinationDirectoryPath))
            {
                validationMessage =
                    "Source and destination are the same folder. Same-folder duplicate/rename semantics are not defined yet.";
                return false;
            }

            foreach (var entry in intent.Entries)
            {
                var parent = Path.GetDirectoryName(NormalizeOperationPath(entry.Path));
                if (string.IsNullOrWhiteSpace(parent) ||
                    !PathsEqual(parent, intent.SourceDirectoryPath))
                {
                    validationMessage =
                        $"The queued selection is no longer a direct child of the captured source folder: {entry.Name}.";
                    return false;
                }

                if (entry.IsDirectory &&
                    IsSameOrDescendantPath(intent.DestinationDirectoryPath, entry.Path))
                {
                    validationMessage =
                        $"Cannot queue {entry.Name} into itself or one of its descendants.";
                    return false;
                }
            }
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            validationMessage = "One of the captured operation paths is invalid.";
            return false;
        }

        validationMessage = string.Empty;
        return true;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            NormalizeOperationPath(left),
            NormalizeOperationPath(right),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrDescendantPath(string candidate, string root)
    {
        var normalizedCandidate = NormalizeOperationPath(candidate);
        var normalizedRoot = NormalizeOperationPath(root);
        if (string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar) ||
            normalizedRoot.EndsWith(Path.AltDirectorySeparatorChar)
                ? normalizedRoot
                : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeOperationPath(string path)
    {
        var normalized = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalized.Length == 2 && normalized[1] == Path.VolumeSeparatorChar)
        {
            normalized += Path.DirectorySeparatorChar;
        }

        return normalized;
    }

    private static string FormatCollisionPolicy(FileOperationCollisionPolicy collisionPolicy) =>
        collisionPolicy switch
        {
            FileOperationCollisionPolicy.Ask => "ask-later collision handling",
            FileOperationCollisionPolicy.Skip => "skip-existing collision handling",
            FileOperationCollisionPolicy.Stop => "stop-on-collision handling",
            _ => throw new ArgumentOutOfRangeException(nameof(collisionPolicy)),
        };
}

public sealed record FileBrowserQueuedOperationRow(
    Guid Id,
    string OperationText,
    string RouteText,
    string CollisionText,
    string QueuedText)
{
    public static FileBrowserQueuedOperationRow FromOperation(FileOperationPlan operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return new FileBrowserQueuedOperationRow(
            operation.Id,
            $"{operation.Kind} · {operation.Intent.Entries.Count:N0} entr{(operation.Intent.Entries.Count == 1 ? "y" : "ies")}",
            $"{operation.Intent.SourcePane}: {operation.Intent.SourceDirectoryPath} → {operation.Intent.DestinationPane}: {operation.Intent.DestinationDirectoryPath}",
            operation.CollisionPolicy switch
            {
                FileOperationCollisionPolicy.Ask => "Ask later",
                FileOperationCollisionPolicy.Skip => "Skip existing",
                FileOperationCollisionPolicy.Stop => "Stop on collision",
                _ => operation.CollisionPolicy.ToString(),
            },
            operation.QueuedAtUtc.ToLocalTime().ToString("g"));
    }
}
