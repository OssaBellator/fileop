using FileOp.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class FilesPaneView : UserControl
{
    private bool _tabActionsEnabled;

    public FilesPaneView()
    {
        InitializeComponent();
    }

    public string PaneTitle
    {
        get => PaneTitleText.Text;
        set => PaneTitleText.Text = value;
    }

    public event EventHandler? UpRequested;

    public event EventHandler? RefreshRequested;

    public event EventHandler? LoadMoreRequested;

    public event EventHandler? NewTabRequested;

    public event EventHandler? CloseTabRequested;

    public event EventHandler<FileBrowserTabRequestedEventArgs>? TabRequested;

    public event EventHandler<FileBrowserRow>? EntryInvoked;

    public void ApplyTabs(IReadOnlyList<FileBrowserTabHeader> tabs, Guid activeTabId)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        TabsPanel.Children.Clear();
        foreach (var tab in tabs)
        {
            var button = new RadioButton
            {
                Content = tab.Title,
                Tag = tab.Id,
                GroupName = $"FileOpFilesTabs-{PaneTitle}",
                IsChecked = tab.Id == activeTabId,
                IsEnabled = _tabActionsEnabled,
                MinWidth = 72,
                MaxWidth = 180,
                Padding = new Thickness(10, 5, 10, 5),
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            ToolTipService.SetToolTip(button, tab.Path);
            button.Click += TabButton_Click;
            TabsPanel.Children.Add(button);
        }

        NewTabButton.IsEnabled = _tabActionsEnabled;
        CloseTabButton.IsEnabled = _tabActionsEnabled && tabs.Count > 1;
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

    public void SetStatus(string message, string? displayPath = null)
    {
        StatusText.Text = message;
        if (!string.IsNullOrWhiteSpace(displayPath))
        {
            PathText.Text = displayPath;
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
        SetTabActionsEnabled(false);
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
        PathText.Text = path;
        LoadingRing.IsActive = false;
        UpButton.IsEnabled = canNavigateUp;
        RefreshButton.IsEnabled = canRefresh;
        LoadMoreButton.Visibility = hasMore ? Visibility.Visible : Visibility.Collapsed;
        LoadMoreButton.IsEnabled = hasMore && canRefresh;
        FilesList.ItemsSource = null;
        FilesList.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        SetTabActionsEnabled(canRefresh);

        if (!hasMore && rows.Count == currentTotalCount)
        {
            StatusText.Text =
                $"{rows.Count:N0} indexed direct entr{(rows.Count == 1 ? "y" : "ies")} loaded.";
        }
        else
        {
            StatusText.Text = hasMore
                ? $"{rows.Count:N0} loaded · current count {currentTotalCount:N0} · more exact pages available."
                : $"{rows.Count:N0} loaded · current count {currentTotalCount:N0} · directory changed while paging.";
        }
    }

    public void SetReady(bool canNavigateUp, bool canRefresh, bool canLoadMore)
    {
        if (LoadingRing.IsActive && StatusText.Text.StartsWith("Loading ", StringComparison.Ordinal))
        {
            StatusText.Text = "The indexed directory load did not complete. Refresh to try again.";
        }

        LoadingRing.IsActive = false;
        UpButton.IsEnabled = canNavigateUp;
        RefreshButton.IsEnabled = canRefresh;
        LoadMoreButton.Visibility = canLoadMore ? Visibility.Visible : Visibility.Collapsed;
        LoadMoreButton.IsEnabled = canLoadMore && canRefresh;
        SetTabActionsEnabled(canRefresh);
    }

    private void SetTabActionsEnabled(bool enabled)
    {
        _tabActionsEnabled = enabled;
        NewTabButton.IsEnabled = enabled;
        CloseTabButton.IsEnabled = enabled && TabsPanel.Children.Count > 1;
        foreach (var child in TabsPanel.Children.OfType<Control>())
        {
            child.IsEnabled = enabled;
        }
    }

    private void TabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: Guid tabId })
        {
            TabRequested?.Invoke(this, new FileBrowserTabRequestedEventArgs(tabId));
        }
    }

    private void NewTabButton_Click(object sender, RoutedEventArgs e)
    {
        NewTabRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CloseTabButton_Click(object sender, RoutedEventArgs e)
    {
        CloseTabRequested?.Invoke(this, EventArgs.Empty);
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

public sealed class FileBrowserTabRequestedEventArgs : EventArgs
{
    public FileBrowserTabRequestedEventArgs(Guid tabId)
    {
        TabId = tabId;
    }

    public Guid TabId { get; }
}

public sealed record FileBrowserTabHeader(
    Guid Id,
    string Title,
    string Path);

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
