using FileOp.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class FilesView : UserControl
{
    public FilesView()
    {
        InitializeComponent();
    }

    public event EventHandler? UpRequested;

    public event EventHandler? RefreshRequested;

    public event EventHandler<FileBrowserRow>? EntryInvoked;

    public void SetSourceDescription(string description)
    {
        ScopeText.Text = description;
    }

    public void SetLoading(string path, string message)
    {
        PathText.Text = path;
        StatusText.Text = message;
        LoadingRing.IsActive = true;
        UpButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
    }

    public void SetUnavailable(string message)
    {
        StatusText.Text = message;
        LoadingRing.IsActive = false;
        UpButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        FilesList.ItemsSource = null;
        EmptyText.Visibility = Visibility.Visible;
    }

    public void Apply(
        IReadOnlyList<FileBrowserRow> rows,
        string path,
        int directEntryCount,
        bool canNavigateUp,
        bool canRefresh)
    {
        ArgumentNullException.ThrowIfNull(rows);
        PathText.Text = path;
        LoadingRing.IsActive = false;
        UpButton.IsEnabled = canNavigateUp;
        RefreshButton.IsEnabled = canRefresh;
        FilesList.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        StatusText.Text = rows.Count == directEntryCount
            ? $"{rows.Count:N0} indexed direct entr{(rows.Count == 1 ? "y" : "ies")}. Folders are shown first, then names are sorted case-insensitively."
            : $"Showing {rows.Count:N0} of {directEntryCount:N0} indexed direct entries. This first browser slice is bounded; omitted entries remain available through Search and future paging work.";
    }

    public void SetReady(bool canNavigateUp, bool canRefresh)
    {
        if (LoadingRing.IsActive && StatusText.Text.StartsWith("Loading ", StringComparison.Ordinal))
        {
            StatusText.Text = "The indexed directory load did not complete. Refresh to try again.";
        }

        LoadingRing.IsActive = false;
        UpButton.IsEnabled = canNavigateUp;
        RefreshButton.IsEnabled = canRefresh;
    }

    private void UpButton_Click(object sender, RoutedEventArgs e)
    {
        UpRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void FilesList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FileBrowserRow row)
        {
            EntryInvoked?.Invoke(this, row);
        }
    }
}

public sealed record FileBrowserRow(
    string Path,
    string Name,
    bool IsDirectory,
    string TypeText,
    string LogicalText,
    string AllocatedText,
    string ContentsText)
{
    public static FileBrowserRow FromEntry(StorageDirectoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var extension = entry.IsDirectory
            ? string.Empty
            : System.IO.Path.GetExtension(entry.Name).TrimStart('.');
        var typeText = entry.IsDirectory
            ? "Folder"
            : string.IsNullOrWhiteSpace(extension)
                ? "File"
                : extension.ToUpperInvariant();
        var contentsText = entry.IsDirectory
            ? $"{entry.UniqueFileCount:N0} file(s) · {entry.DirectoryCount:N0} folder record(s)"
            : entry.HardLinkAliasCount > 0
                ? "Hard-link alias"
                : "—";

        return new FileBrowserRow(
            entry.Path,
            entry.Name,
            entry.IsDirectory,
            typeText,
            ByteFormatter.Format(entry.LogicalBytes),
            entry.AllocatedBytes is { } allocated ? ByteFormatter.Format(allocated) : "Unknown",
            contentsText);
    }
}
