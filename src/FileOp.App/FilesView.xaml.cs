using FileOp.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class FilesView : UserControl
{
    private string? _lastAppliedPath;

    public FilesView()
    {
        InitializeComponent();
    }

    public event EventHandler? UpRequested;

    public event EventHandler? RefreshRequested;

    public event EventHandler? LoadMoreRequested;

    public event EventHandler<FileBrowserRow>? EntryInvoked;

    public void SetSourceDescription(string description)
    {
        ScopeText.Text = description;
    }

    public void SetLoading(string path, string message, bool preserveRows = false)
    {
        PathText.Text = path;
        StatusText.Text = message;
        LoadingRing.IsActive = true;
        UpButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        LoadMoreButton.IsEnabled = false;
        if (!preserveRows)
        {
            LoadMoreButton.Visibility = Visibility.Collapsed;
            FilesList.ItemsSource = null;
            EmptyText.Visibility = Visibility.Collapsed;
        }
    }

    public void SetStatus(string message)
    {
        StatusText.Text = message;
        if (LoadingRing.IsActive && _lastAppliedPath is not null)
        {
            PathText.Text = _lastAppliedPath;
        }
    }

    public void SetUnavailable(string message)
    {
        StatusText.Text = message;
        LoadingRing.IsActive = false;
        UpButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        LoadMoreButton.IsEnabled = false;
        LoadMoreButton.Visibility = Visibility.Collapsed;
        FilesList.ItemsSource = null;
        EmptyText.Visibility = Visibility.Visible;
    }

    public void Apply(
        IReadOnlyList<FileBrowserRow> rows,
        string path,
        int currentTotalCount,
        bool hasMore,
        bool canNavigateUp,
        bool canRefresh)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _lastAppliedPath = path;
        PathText.Text = path;
        LoadingRing.IsActive = false;
        UpButton.IsEnabled = canNavigateUp;
        RefreshButton.IsEnabled = canRefresh;
        LoadMoreButton.Visibility = hasMore ? Visibility.Visible : Visibility.Collapsed;
        LoadMoreButton.IsEnabled = hasMore && canRefresh;
        FilesList.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!hasMore && rows.Count == currentTotalCount)
        {
            StatusText.Text =
                $"{rows.Count:N0} indexed direct entr{(rows.Count == 1 ? "y" : "ies")} loaded in exact directory-first/name/path order.";
        }
        else
        {
            StatusText.Text = hasMore
                ? $"{rows.Count:N0} loaded · current indexed count {currentTotalCount:N0} · more exact pages are available."
                : $"{rows.Count:N0} loaded · current indexed count {currentTotalCount:N0}. The live directory changed while pages were being read.";
        }
    }

    public void SetReady(bool canNavigateUp, bool canRefresh, bool canLoadMore)
    {
        if (LoadingRing.IsActive && StatusText.Text.StartsWith("Loading ", StringComparison.Ordinal))
        {
            StatusText.Text = "The indexed directory load did not complete. Refresh to try again.";
            if (_lastAppliedPath is not null)
            {
                PathText.Text = _lastAppliedPath;
            }
        }

        LoadingRing.IsActive = false;
        UpButton.IsEnabled = canNavigateUp;
        RefreshButton.IsEnabled = canRefresh;
        LoadMoreButton.Visibility = canLoadMore ? Visibility.Visible : Visibility.Collapsed;
        LoadMoreButton.IsEnabled = canLoadMore && canRefresh;
    }

    private void UpButton_Click(object sender, RoutedEventArgs e)
    {
        UpRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void LoadMoreButton_Click(object sender, RoutedEventArgs e)
    {
        LoadMoreRequested?.Invoke(this, EventArgs.Empty);
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
    string SizeText,
    string AllocatedText,
    string ModifiedText)
{
    public static FileBrowserRow FromRecord(FileRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var extension = record.Extension.TrimStart('.');
        var typeText = record.IsDirectory
            ? "Folder"
            : string.IsNullOrWhiteSpace(extension)
                ? "File"
                : extension.ToUpperInvariant();

        return new FileBrowserRow(
            record.Path,
            record.Name,
            record.IsDirectory,
            typeText,
            record.IsDirectory ? "—" : ByteFormatter.Format(record.Length),
            record.IsDirectory
                ? "—"
                : record.AllocatedLength is { } allocated
                    ? ByteFormatter.Format(allocated)
                    : "Unknown",
            record.LastWriteTime.ToLocalTime().ToString("g"));
    }
}
