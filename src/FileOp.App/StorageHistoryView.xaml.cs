using System.Collections.ObjectModel;
using FileOp.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class StorageHistoryView : UserControl
{
    private readonly ObservableCollection<StorageHistoryTimelineRow> _timeline = [];
    private readonly ObservableCollection<StorageHistoryGrowthRow> _growth = [];

    public StorageHistoryView()
    {
        InitializeComponent();
        TimelineList.ItemsSource = _timeline;
        GrowthList.ItemsSource = _growth;
    }

    public event EventHandler? RefreshRequested;

    public void SetLoading(string message)
    {
        HistoryProgressRing.IsActive = true;
        RefreshButton.IsEnabled = false;
        HistoryStatusText.Text = message;
    }

    public void SetUnavailable(string message)
    {
        HistoryProgressRing.IsActive = false;
        RefreshButton.IsEnabled = false;
        HistoryStatusText.Text = message;
        GrowthStatusText.Text = "Native indexed history is not currently available.";
        _timeline.Clear();
        _growth.Clear();
        TimelineEmptyText.Visibility = Visibility.Visible;
        GrowthEmptyText.Visibility = Visibility.Visible;
    }

    public void SetReadyForRefresh(bool enabled)
    {
        HistoryProgressRing.IsActive = false;
        RefreshButton.IsEnabled = enabled;
    }

    public void Apply(IReadOnlyList<StorageHistorySnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        HistoryProgressRing.IsActive = false;
        RefreshButton.IsEnabled = true;
        _timeline.Clear();
        _growth.Clear();

        if (snapshots.Count == 0)
        {
            HistoryStatusText.Text = "No native history observations yet. FileOp records at most one successful volume observation per UTC hour while the native index is current.";
            GrowthStatusText.Text = "Two observations are needed to calculate a change.";
            TimelineEmptyText.Visibility = Visibility.Visible;
            GrowthEmptyText.Visibility = Visibility.Visible;
            return;
        }

        var chronological = snapshots
            .OrderBy(static snapshot => snapshot.CapturedAt)
            .ToArray();
        var usePhysical = chronological.All(static snapshot => snapshot.AllocatedBytes.HasValue);
        var peak = chronological.Max(snapshot => HistoryWeight(snapshot, usePhysical));

        long? previousWeight = null;
        foreach (var snapshot in chronological)
        {
            var weight = HistoryWeight(snapshot, usePhysical);
            var deltaText = previousWeight is { } previous
                ? FormatSignedBytes(weight - previous)
                : "Baseline";
            _timeline.Add(new StorageHistoryTimelineRow(
                snapshot.CapturedAt.ToLocalTime().ToString("g"),
                ByteFormatter.Format(weight),
                deltaText,
                peak > 0 ? Math.Clamp(weight * 100d / peak, 0d, 100d) : 0d));
            previousWeight = weight;
        }

        TimelineEmptyText.Visibility = Visibility.Collapsed;
        var oldest = chronological[0].CapturedAt.ToLocalTime();
        var newest = chronological[^1].CapturedAt.ToLocalTime();
        var unit = usePhysical ? "physical allocation" : "logical size";
        HistoryStatusText.Text = chronological.Length == 1
            ? $"1 hourly observation · {unit} · {newest:g}"
            : $"{chronological.Length:N0} hourly observations · {unit} · {oldest:g} to {newest:g}";

        if (chronological.Length < 2)
        {
            GrowthStatusText.Text = "Capture another hourly observation to see what changed.";
            GrowthEmptyText.Visibility = Visibility.Visible;
            return;
        }

        var olderSnapshot = chronological[^2];
        var newerSnapshot = chronological[^1];
        var delta = StorageHistoryDelta.Between(olderSnapshot, newerSnapshot);
        var usePhysicalDelta = delta.AllocatedBytesDelta.HasValue;
        foreach (var category in delta.Categories)
        {
            var bytes = usePhysicalDelta
                ? category.AllocatedBytesDelta ?? 0
                : category.LogicalBytesDelta;
            _growth.Add(new StorageHistoryGrowthRow(
                FormatCategory(category.Category),
                FormatSignedBytes(bytes),
                $"files {FormatSignedCount(category.FileCountDelta)} · " +
                $"aliases {FormatSignedCount(category.HardLinkAliasCountDelta)} · " +
                $"types {FormatSignedCount(category.TypeCountDelta)}"));
        }

        var totalDelta = usePhysicalDelta
            ? delta.AllocatedBytesDelta!.Value
            : delta.LogicalBytesDelta;
        GrowthStatusText.Text =
            $"{olderSnapshot.CapturedAt.ToLocalTime():g} → {newerSnapshot.CapturedAt.ToLocalTime():g} · " +
            $"{(usePhysicalDelta ? "physical allocation" : "logical size")} {FormatSignedBytes(totalDelta)}";
        GrowthEmptyText.Visibility = _growth.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private static long HistoryWeight(StorageHistorySnapshot snapshot, bool usePhysical) =>
        usePhysical ? snapshot.AllocatedBytes ?? 0 : snapshot.LogicalBytes;

    private static string FormatCategory(StorageFileCategory category) => category switch
    {
        StorageFileCategory.NoExtension => "No extension",
        StorageFileCategory.DiskImages => "Disk images",
        _ => category.ToString(),
    };

    private static string FormatSignedCount(long value) => value switch
    {
        > 0 => $"+{value:N0}",
        < 0 => $"−{Magnitude(value):N0}",
        _ => "0",
    };

    private static string FormatSignedBytes(long value)
    {
        if (value == 0)
        {
            return "0 B";
        }

        var sign = value > 0 ? "+" : "−";
        var magnitude = (double)Magnitude(value);
        string[] suffixes = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];
        var suffix = 0;
        while (magnitude >= 1024 && suffix < suffixes.Length - 1)
        {
            magnitude /= 1024;
            suffix++;
        }

        var formatted = suffix == 0 ? $"{magnitude:N0}" : $"{magnitude:N1}";
        return $"{sign}{formatted} {suffixes[suffix]}";
    }

    private static ulong Magnitude(long value) =>
        value >= 0 ? (ulong)value : (ulong)(-(value + 1)) + 1;
}

public sealed record StorageHistoryTimelineRow(
    string BucketText,
    string SizeText,
    string DeltaText,
    double PercentOfPeak);

public sealed record StorageHistoryGrowthRow(
    string Name,
    string DeltaText,
    string DetailText);
