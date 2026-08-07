using System.Collections.ObjectModel;
using System.Diagnostics;
using FileOp.Core.Models;
using FileOp.Core.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class MainWindow : Window
{
    private readonly DesktopSearchEngine _searchEngine = new();
    private readonly StorageSnapshotService _storageSnapshotService = new();
    private readonly ObservableCollection<SearchResultRow> _results = [];
    private readonly ObservableCollection<VolumeCard> _volumes = [];
    private readonly DispatcherQueueTimer _searchTimer;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _searchGate = new(1, 1);
    private int _searchGeneration;
    private bool _closed;

    public MainWindow()
    {
        InitializeComponent();

        ResultsList.ItemsSource = _results;
        VolumeCards.ItemsSource = _volumes;

        _searchTimer = DispatcherQueue.CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(140);
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += SearchTimer_Tick;

        _searchEngine.StateChanged += SearchEngine_StateChanged;
        Closed += MainWindow_Closed;
        Activated += MainWindow_Activated;
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= MainWindow_Activated;
        LoadVolumes();

        var root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            ApplyEngineState(new DesktopSearchEngineState(
                DesktopSearchMode.Unavailable,
                "User profile is unavailable.",
                0,
                IsBusy: false,
                CanElevate: false,
                IsCurrent: false));
            return;
        }

        try
        {
            await _searchEngine.InitializeAsync(root, _lifetimeCancellation.Token);
            if (_closed)
            {
                return;
            }

            ApplyEngineState(_searchEngine.State);
            if (SearchBox.IsEnabled)
            {
                await RunSearchAsync();
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ApplyEngineState(new DesktopSearchEngineState(
                DesktopSearchMode.Unavailable,
                $"Search engine could not start: {exception.Message}",
                0,
                IsBusy: false,
                CanElevate: false,
                IsCurrent: false));
        }
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _searchTimer.Stop();
        _lifetimeCancellation.Cancel();
        _searchEngine.StateChanged -= SearchEngine_StateChanged;

        var disposeTask = _searchEngine.DisposeAsync().AsTask();
        try
        {
            await _searchGate.WaitAsync(CancellationToken.None);
            _searchGate.Release();
            await disposeTask;
        }
        catch (Exception)
        {
            // Window shutdown should not be blocked by search/helper teardown failures.
        }
        finally
        {
            _searchGate.Dispose();
            _lifetimeCancellation.Dispose();
        }
    }

    private void LoadVolumes()
    {
        _volumes.Clear();
        foreach (var volume in _storageSnapshotService.GetReadyVolumes().Take(4))
        {
            _volumes.Add(VolumeCard.FromSnapshot(volume));
        }
    }

    private void SearchEngine_StateChanged(DesktopSearchEngineState state)
    {
        if (_closed)
        {
            return;
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            ApplyEngineState(state);
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closed)
            {
                ApplyEngineState(state);
            }
        });
    }

    private void ApplyEngineState(DesktopSearchEngineState state)
    {
        EngineStatusText.Text = state.Status;
        IndexingProgressRing.IsActive = state.IsBusy;
        CountText.Text = state.IndexedItemCount > 0
            ? $"{state.IndexedItemCount:N0} indexed"
            : string.Empty;

        var searchAvailable = !_closed &&
            !state.IsBusy &&
            state.Mode is DesktopSearchMode.Native or DesktopSearchMode.Fallback;
        SearchBox.IsEnabled = searchAvailable;

        EnableFastIndexButton.Visibility = state.CanElevate
            ? Visibility.Visible
            : Visibility.Collapsed;
        EnableFastIndexButton.IsEnabled = state.CanElevate && !state.IsBusy;
        EnableFastIndexButton.Content = state.Mode == DesktopSearchMode.Native
            ? "Enable live updates"
            : "Enable fast indexing";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        Interlocked.Increment(ref _searchGeneration);
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
        if (!SearchBox.IsEnabled || _closed)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _searchGeneration);
        var rawQuery = SearchBox.Text;

        try
        {
            await _searchGate.WaitAsync(_lifetimeCancellation.Token);
            try
            {
                if (_closed || generation != Volatile.Read(ref _searchGeneration))
                {
                    return;
                }

                var matches = await _searchEngine.SearchAsync(rawQuery, limit: 250);
                if (_closed || generation != Volatile.Read(ref _searchGeneration))
                {
                    return;
                }

                _results.Clear();
                foreach (var match in matches)
                {
                    _results.Add(SearchResultRow.FromRecord(match));
                }

                var mode = _searchEngine.State.Mode == DesktopSearchMode.Native
                    ? "native index"
                    : "profile fallback";
                SearchStatusText.Text = string.IsNullOrWhiteSpace(rawQuery)
                    ? $"Showing {matches.Count:N0} indexed item(s) · {mode}"
                    : $"{matches.Count:N0} result(s) · {mode}";
            }
            finally
            {
                _searchGate.Release();
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_closed)
            {
                SearchStatusText.Text = $"Search failed: {exception.Message}";
            }
        }
    }

    private async void EnableFastIndexButton_Click(object sender, RoutedEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        SearchBox.IsEnabled = false;
        EnableFastIndexButton.IsEnabled = false;
        SearchStatusText.Text = string.Empty;
        Interlocked.Increment(ref _searchGeneration);

        try
        {
            var enabled = await _searchEngine.TryElevateAsync(_lifetimeCancellation.Token);
            if (enabled && !_closed)
            {
                ApplyEngineState(_searchEngine.State);
                if (SearchBox.IsEnabled)
                {
                    await RunSearchAsync();
                }
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (!_closed)
            {
                ApplyEngineState(_searchEngine.State);
            }
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
            SearchStatusText.Text = $"Could not open item: {exception.Message}";
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
