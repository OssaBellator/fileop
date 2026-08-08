using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class FilesView : UserControl
{
    private FileBrowserOperationIntent? _preparedIntent;

    public FilesView()
    {
        InitializeComponent();
        LeftPane.IntentStateChanged += Pane_IntentStateChanged;
        RightPane.IntentStateChanged += Pane_IntentStateChanged;
        UpdateIntentAvailability();
    }

    public FilesPaneView LeftPane => LeftPaneControl;

    public FilesPaneView RightPane => RightPaneControl;

    public FileBrowserOperationIntent? PreparedIntent => _preparedIntent;

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
            .Select(static row => new FileBrowserOperationEntry(
                row.Path,
                row.Name,
                row.IsDirectory))
            .ToArray();

        _preparedIntent = new FileBrowserOperationIntent(
            source.PaneTitle,
            source.ActiveTabId,
            sourcePath,
            entries,
            destination.PaneTitle,
            destination.ActiveTabId,
            destinationPath);

        IntentText.Text =
            $"Prepared only: {entries.Length:N0} selected entr{(entries.Length == 1 ? "y" : "ies")} " +
            $"from {source.PaneTitle} ({sourcePath}) → {destination.PaneTitle} ({destinationPath}). " +
            "Copy and Move remain disabled until collision, queue and undo semantics are implemented.";
    }

    private void ClearPreparedIntent()
    {
        _preparedIntent = null;
        IntentText.Text =
            "Select one or more entries in a source pane, then prepare a direction. No file operation is executed.";
    }

    private void UpdateIntentAvailability()
    {
        PrepareLeftToRightButton.IsEnabled = CanPrepareIntent(LeftPane, RightPane);
        PrepareRightToLeftButton.IsEnabled = CanPrepareIntent(RightPane, LeftPane);
    }

    private static bool CanPrepareIntent(FilesPaneView source, FilesPaneView destination) =>
        source.IsDirectoryReady &&
        destination.IsDirectoryReady &&
        source.SelectedCount > 0 &&
        !string.IsNullOrWhiteSpace(source.CurrentPath) &&
        !string.IsNullOrWhiteSpace(destination.CurrentPath);
}

public sealed record FileBrowserOperationEntry(
    string Path,
    string Name,
    bool IsDirectory);

public sealed record FileBrowserOperationIntent(
    string SourcePane,
    Guid SourceTabId,
    string SourceDirectoryPath,
    IReadOnlyList<FileBrowserOperationEntry> Entries,
    string DestinationPane,
    Guid DestinationTabId,
    string DestinationDirectoryPath);
