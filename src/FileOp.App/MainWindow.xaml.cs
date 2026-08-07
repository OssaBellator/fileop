using System.Collections.ObjectModel;
using System.Diagnostics;
using FileOp.Core.Indexing;
using FileOp.Core.Models;
using FileOp.Core.Search;
using FileOp.Core.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class MainWindow : Window
{
    private const int IndexBatchSize = 512;

    private readonly InMemoryFileIndex _index = new();
    private readonly FileSystemCrawler _crawler = new();
    private readonly StorageSnapshotService _storageSnapshotService = new();
    private readonly ObservableCollection<SearchResultRow> _results = [];
    private readonly ObservableCollection<VolumeCard> _volumes = [];
    private readonly DispatcherQueueTimer _searchTimer;
    private CancellationTokenSource? _searchCancellation;

    public MainWindow()
    {
        InitializeComponent();

        ResultsList.ItemsSource = _results;
        VolumeCards.ItemsSource = _volumes;

        _searchTimer = DispatcherQueue.CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(140);
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += SearchTimer_Tick;

        Closed += (_, _) =>
        {
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
            _index.Dispose();
        };

        Activated += MainWindow_Activated;
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= MainWindow_Activated;
        LoadVolumes();
        await IndexDefaultLocationAsync();
    }

    private void LoadVolumes()
    {
        _volumes.Clear();
        foreach (var volume in _storageSnapshotService.GetReadyVolumes().Take(4))
        {
            _volumes.Add(VolumeCard.FromSnapshot(volume));
        }
    }

    private async Task IndexDefaultLocationAsync()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            StatusText.Text = "User profile is unavailable.";
            return;
        }

        StatusText.Text = $"Indexing {root}";
        await _index.ClearAsync();

        var batch = new List<FileRecord>(IndexBatchSize);
        var indexed = 0;

        try
        {
            await foreach (var record in _crawler.CrawlAsync(root))
            {
                batch.Add(record);
                if (batch.Count < IndexBatchSize)
                {
                    continue;
                }

                await _index.AddBatchAsync(batch);
                indexed += batch.Count;
                batch.Clear();
                CountText.Text = $"{indexed:N0} indexed";
            }

            if (batch.Count > 0)
            {
                await _index.AddBatchAsync(batch);
                indexed += batch.Count;
            }

            CountText.Text = $"{indexed:N0} indexed";
            StatusText.Text = "Index ready";
            await RunSearchAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Indexing stopped: {exception.Message}";
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private async void SearchTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        await RunSearchAsync();
    }

    private async Task RunSearchAsync()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var cancellationToken = _searchCancellation.Token;

        try
        {
            var query = FileSearchQuery.Parse(SearchBox.Text, limit: 250);
            var matches = await _index.SearchAsync(query, cancellationToken);

            _results.Clear();
            foreach (var match in matches)
            {
                _results.Add(SearchResultRow.FromRecord(match));
            }

            StatusText.Text = string.IsNullOrWhiteSpace(query.Raw)
                ? "Showing indexed items"
                : $"{matches.Count:N0} result(s)";
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ResultsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SearchResultRow row)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(row.Path)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = $"Could not open item: {exception.Message}";
        }
    }
}

public sealed record SearchResultRow(string Path, string Name, string ParentPath, string SizeText)
{
    public static SearchResultRow FromRecord(FileRecord record) => new(
        record.Path,
        record.Name,
        record.ParentPath,
        record.IsDirectory ? "Folder" : ByteFormatter.Format(record.Length));
}

public sealed record VolumeCard(string Name, string Usage, double UsedPercent)
{
    public static VolumeCard FromSnapshot(StorageVolumeSnapshot snapshot)
    {
        var displayName = string.IsNullOrWhiteSpace(snapshot.Label)
            ? snapshot.Name
            : $"{snapshot.Name} {snapshot.Label}";

        return new VolumeCard(
            displayName,
            $"{ByteFormatter.Format(snapshot.UsedBytes)} / {ByteFormatter.Format(snapshot.TotalBytes)}",
            snapshot.UsedFraction * 100);
    }
}

internal static class ByteFormatter
{
    private static readonly string[] Suffixes = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes)
    {
        var value = Math.Max(0, (double)bytes);
        var suffix = 0;
        while (value >= 1024 && suffix < Suffixes.Length - 1)
        {
            value /= 1024;
            suffix++;
        }

        return suffix == 0 ? $"{value:N0} {Suffixes[suffix]}" : $"{value:N1} {Suffixes[suffix]}";
    }
}
