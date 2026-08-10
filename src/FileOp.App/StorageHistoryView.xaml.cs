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
        PressureStatusText.Text = "Refreshing current capacity and native aggregate history…";
    }

    public void SetUnavailable(string message)
    {
        HistoryProgressRing.IsActive = false;
        RefreshButton.IsEnabled = false;
        HistoryStatusText.Text = message;
        GrowthStatusText.Text = "Native indexed history is not currently available.";
        PressureFreeText.Text = "—";
        PressureLatestText.Text = "—";
        PressureDeltaText.Text = "—";
        PressureMultipleText.Text = "—";
        PressureStatusText.Text = message;
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

    public void Apply(
        IReadOnlyList<StorageHistorySnapshot> snapshots,
        long? volumeTotalBytes = null,
        long? volumeFreeBytes = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        HistoryProgressRing.IsActive = false;
        RefreshButton.IsEnabled = true;
        _timeline.Clear();
        _growth.Clear();
        ApplyPressureEvidence(StorageHistoryPressureEvidence.Analyze(
            snapshots,
            volumeTotalBytes,
            volumeFreeBytes));

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

    private void ApplyPressureEvidence(StorageHistoryPressureEvidence evidence)
    {
        PressureFreeText.Text = evidence.VolumeFreeBytes is { } free
            ? evidence.VolumeFreePercent is { } freePercent
                ? $"{ByteFormatter.Format(free)} · {freePercent:N1}%"
                : ByteFormatter.Format(free)
            : "Unknown";

        PressureLatestText.Text = evidence.LatestStorageBytes is { } latest
            ? $"{ByteFormatter.Format(latest)} · " +
              (evidence.LatestStorageUsesPhysicalAllocation ? "physical" : "logical")
            : "No observation";

        PressureDeltaText.Text = evidence.StorageDeltaBytes is { } delta
            ? $"{FormatSignedBytes(delta)} · " +
              (evidence.DeltaUsesPhysicalAllocation ? "physical" : "logical") +
              (evidence.ObservationInterval is { } interval
                  ? $" / {FormatInterval(interval)} interval"
                  : string.Empty)
            : "Need 2 observations";

        if (evidence.FreeSpaceToLastPositivePhysicalGrowthMultiple is { } multiple)
        {
            PressureMultipleText.Text = $"{multiple:N1}× last growth";
        }
        else if (evidence.StorageDeltaBytes is null)
        {
            PressureMultipleText.Text = "Need 2 observations";
        }
        else if (!evidence.DeltaUsesPhysicalAllocation)
        {
            PressureMultipleText.Text = "Not comparable";
        }
        else if (evidence.StorageDeltaBytes <= 0)
        {
            PressureMultipleText.Text = "No positive growth";
        }
        else
        {
            PressureMultipleText.Text = "Free space unknown";
        }

        if (evidence.NewerCapturedAt is null)
        {
            PressureStatusText.Text =
                "Current free space is measured from the active volume, but no native aggregate history observation exists yet.";
            return;
        }

        if (evidence.StorageDeltaBytes is null)
        {
            PressureStatusText.Text =
                "One trustworthy aggregate observation exists. A second observation is required before FileOp describes a change.";
            return;
        }

        if (!evidence.DeltaUsesPhysicalAllocation)
        {
            PressureStatusText.Text =
                "The last history change uses logical size because physical allocation was incomplete. Logical-size growth is not compared with physical free-space bytes.";
            return;
        }

        if (evidence.StorageDeltaBytes <= 0)
        {
            PressureStatusText.Text =
                "The latest physical-allocation observation did not increase. FileOp does not manufacture a growth-pressure ratio from a flat or shrinking interval.";
            return;
        }

        if (evidence.FreeSpaceToLastPositivePhysicalGrowthMultiple is { } ratio &&
            evidence.OlderCapturedAt is { } older &&
            evidence.NewerCapturedAt is { } newer)
        {
            PressureStatusText.Text =
                $"Current free space equals {ratio:N1}× the physical-allocation increase observed from " +
                $"{older.ToLocalTime():g} to {newer.ToLocalTime():g}. This is a descriptive comparison of one interval, not a forecast, trend guarantee, cause attribution, or disk-full date.";
            return;
        }

        PressureStatusText.Text =
            "Physical growth is available, but current volume free space could not be measured, so FileOp does not infer headroom.";
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

    private static string FormatInterval(TimeSpan interval)
    {
        if (interval.TotalDays >= 1)
        {
            return $"{interval.TotalDays:N1} d";
        }

        if (interval.TotalHours >= 1)
        {
            return $"{interval.TotalHours:N1} h";
        }

        if (interval.TotalMinutes >= 1)
        {
            return $"{interval.TotalMinutes:N0} min";
        }

        return $"{Math.Max(0, interval.TotalSeconds):N0} s";
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
