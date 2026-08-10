using FileOp.Core.Performance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class PerformanceDiagnosticsView : UserControl
{
    private readonly PerformanceProbeHistory _probeHistory = new();

    public PerformanceDiagnosticsView()
    {
        InitializeComponent();
    }

    public event EventHandler? RefreshRequested;

    public void SetLoading()
    {
        StatusText.Text = "Running bounded Search, Storage and index-database probes through the current FileOp source…";
        IndexStatusText.Text = "Refreshing helper-owned index database metrics…";
        JournalStatusText.Text = "Refreshing durable checkpoint and optional live USN metadata…";
        RefreshButton.IsEnabled = false;
    }

    public void SetUnavailable(string message)
    {
        StatusText.Text = message;
        ProbeList.ItemsSource = null;
        LatencyDistributionList.ItemsSource = null;
        ResetIndexMetrics();
        IndexStatusText.Text = message;
        JournalStatusText.Text = message;
        RefreshButton.IsEnabled = false;
    }

    public void SetReadyForRefresh(bool ready)
    {
        RefreshButton.IsEnabled = ready;
    }

    public void Apply(PerformanceDiagnosticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        SourceText.Text = snapshot.SourceMode;
        IndexedItemsText.Text = snapshot.IndexedItemCount > 0
            ? $"{snapshot.IndexedItemCount:N0}"
            : "—";
        FreeSpaceText.Text = snapshot.VolumeFreeBytes is { } free
            ? snapshot.VolumeFreePercent is { } percent
                ? $"{ByteFormatter.Format(free)} · {percent:N1}%"
                : ByteFormatter.Format(free)
            : "Unknown";
        CapturedText.Text = snapshot.CapturedAt.ToLocalTime().ToString("g");
        ProbeList.ItemsSource = snapshot.Probes
            .Select(PerformanceProbeRow.FromMeasurement)
            .ToArray();
        LatencyDistributionList.ItemsSource = _probeHistory
            .AddAndSummarize(snapshot)
            .Select(PerformanceProbeDistributionRow.FromDistribution)
            .ToArray();
        StatusText.Text = snapshot.RootPath is { } root
            ? $"{snapshot.IndexState} · scope {root}"
            : snapshot.IndexState;
        ApplyIndexDatabase(snapshot.IndexDatabase, snapshot.IndexDatabaseStatus);
        RefreshButton.IsEnabled = true;
    }

    private void ApplyIndexDatabase(IndexDatabaseDiagnostics? diagnostics, string? status)
    {
        if (diagnostics is null)
        {
            ResetIndexMetrics();
            IndexStatusText.Text = string.IsNullOrWhiteSpace(status)
                ? "Native helper index database metrics are unavailable for this source."
                : status;
            JournalStatusText.Text = IndexStatusText.Text;
            return;
        }

        IndexFootprintText.Text = ByteFormatter.Format(diagnostics.FileFootprintBytes);
        IndexDatabaseText.Text = ByteFormatter.Format(diagnostics.DatabaseFileBytes);
        IndexWalText.Text = ByteFormatter.Format(diagnostics.WalFileBytes);
        IndexReusableText.Text = diagnostics.ReusableFreePagePercent is { } reusablePercent
            ? $"{ByteFormatter.Format(diagnostics.ReusableFreePageBytes)} · {reusablePercent:N1}%"
            : ByteFormatter.Format(diagnostics.ReusableFreePageBytes);
        IndexCacheText.Text = diagnostics.ReaderCacheDefaultTargetBytes is { } cacheDefault
            ? ByteFormatter.Format(cacheDefault)
            : "Unknown";

        var journalMode = string.IsNullOrWhiteSpace(diagnostics.JournalMode)
            ? "journal mode unknown"
            : diagnostics.JournalMode.ToUpperInvariant();
        IndexStatusText.Text =
            $"{journalMode} · {diagnostics.PageCount:N0} logical page(s) × {ByteFormatter.Format(diagnostics.PageSizeBytes)} · " +
            $"{diagnostics.FreePageCount:N0} reusable page(s) · {diagnostics.IndexedItemCount:N0} indexed row(s) · " +
            $"SHM {ByteFormatter.Format(diagnostics.SharedMemoryFileBytes)} · captured {diagnostics.CapturedAt.ToLocalTime():g}. " +
            "Reusable pages can be reused by SQLite and are not automatically reclaimable disk space; the reader cache default is not observed resident memory or live cache occupancy.";
        ApplyJournalFreshness(diagnostics);
    }

    private void ApplyJournalFreshness(IndexDatabaseDiagnostics diagnostics)
    {
        var checkpoint = diagnostics.DurableCheckpoint;
        var freshness = diagnostics.JournalFreshness;
        if (checkpoint is null)
        {
            CheckpointAgeText.Text = "Unknown";
            JournalIdentityText.Text = "Unknown";
            UsnBacklogText.Text = "Unknown";
            RetentionHeadroomText.Text = "Unknown";
            JournalStatusText.Text =
                "Durable checkpoint evidence was not included. Older protocol-v8 helpers may omit this optional evidence.";
            return;
        }

        var checkpointAge = diagnostics.CapturedAt.ToUniversalTime() - checkpoint.UpdatedAt.ToUniversalTime();
        if (checkpointAge < TimeSpan.Zero)
        {
            checkpointAge = TimeSpan.Zero;
        }
        CheckpointAgeText.Text = FormatAge(checkpointAge);

        if (freshness is null)
        {
            JournalIdentityText.Text = "Live unavailable";
            UsnBacklogText.Text = "Unknown";
            RetentionHeadroomText.Text = "Unknown";
            JournalStatusText.Text =
                $"Durable checkpoint journal {checkpoint.JournalId:X16} at USN {checkpoint.NextUsn:N0}; live journal metadata is unavailable. " +
                "Older protocol-v8 helpers may omit this optional evidence.";
            return;
        }

        CheckpointAgeText.Text = FormatAge(freshness.AgeAt(diagnostics.CapturedAt));
        if (!freshness.JournalIdentityMatches)
        {
            JournalIdentityText.Text = "Changed";
            UsnBacklogText.Text = "Not comparable";
            RetentionHeadroomText.Text = "Not comparable";
            JournalStatusText.Text =
                $"Durable journal ID {freshness.DurableJournalId:X16} differs from the live journal ID {freshness.LiveJournalId:X16}; " +
                "a continuous backlog cannot be inferred.";
            return;
        }

        JournalIdentityText.Text = "Matches";
        if (freshness.CheckpointBelowRetentionFloor)
        {
            UsnBacklogText.Text = "Unavailable";
            RetentionHeadroomText.Text = "Expired";
            JournalStatusText.Text =
                $"Durable checkpoint USN {freshness.DurableNextUsn:N0} is below the live retention floor {freshness.LowestValidUsn:N0}. " +
                "The missing journal range can no longer be replayed incrementally.";
            return;
        }

        if (freshness.CheckpointAheadOfJournal)
        {
            UsnBacklogText.Text = "Unavailable";
            RetentionHeadroomText.Text = "Unavailable";
            JournalStatusText.Text =
                $"Durable checkpoint USN {freshness.DurableNextUsn:N0} is ahead of the live journal head {freshness.LiveNextUsn:N0}; " +
                "no backlog distance is inferred from this inconsistent state.";
            return;
        }

        if (freshness.CheckpointWithinReadableWindow)
        {
            UsnBacklogText.Text = freshness.BacklogUsnDistance is { } backlog
                ? $"{backlog:N0} USN"
                : "Unknown";
            RetentionHeadroomText.Text = freshness.RetentionHeadroomUsnDistance is { } headroom
                ? $"{headroom:N0} USN"
                : "Unknown";
            JournalStatusText.Text =
                $"Durable checkpoint USN {freshness.DurableNextUsn:N0} is inside the live readable window " +
                $"{freshness.LowestValidUsn:N0}–{freshness.LiveNextUsn:N0}. " +
                "Backlog and retention headroom are USN sequence-position distances, not file/event counts or elapsed time.";
            return;
        }

        UsnBacklogText.Text = "Unknown";
        RetentionHeadroomText.Text = "Unknown";
        JournalStatusText.Text =
            "Live USN metadata did not map to a recognized continuity state; FileOp does not invent a backlog value.";
    }

    private void ResetIndexMetrics()
    {
        IndexFootprintText.Text = "—";
        IndexDatabaseText.Text = "—";
        IndexWalText.Text = "—";
        IndexReusableText.Text = "—";
        IndexCacheText.Text = "—";
        CheckpointAgeText.Text = "—";
        JournalIdentityText.Text = "—";
        UsnBacklogText.Text = "—";
        RetentionHeadroomText.Text = "—";
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalDays >= 1)
        {
            return $"{age.TotalDays:N1} d";
        }

        if (age.TotalHours >= 1)
        {
            return $"{age.TotalHours:N1} h";
        }

        if (age.TotalMinutes >= 1)
        {
            return $"{age.TotalMinutes:N1} min";
        }

        return $"{Math.Max(0, age.TotalSeconds):N0} s";
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        RefreshRequested?.Invoke(this, EventArgs.Empty);
}

public sealed record PerformanceProbeRow(
    string Name,
    string Scope,
    string ElapsedText,
    string Detail)
{
    public static PerformanceProbeRow FromMeasurement(PerformanceProbeMeasurement measurement) =>
        new(
            measurement.Name,
            measurement.Scope,
            FormatElapsed(measurement.ElapsedMicroseconds),
            measurement.Detail);

    private static string FormatElapsed(long microseconds) =>
        microseconds >= 1_000
            ? $"{microseconds / 1_000d:N2} ms"
            : $"{microseconds:N0} µs";
}

public sealed record PerformanceProbeDistributionRow(
    string Name,
    string Scope,
    string SamplesText,
    string MinimumText,
    string MedianText,
    string P95Text,
    string MaximumText)
{
    public static PerformanceProbeDistributionRow FromDistribution(
        PerformanceProbeDistribution distribution) =>
        new(
            distribution.Name,
            distribution.Scope,
            $"{distribution.SampleCount:N0}/{distribution.SampleCapacity:N0}",
            FormatElapsed(distribution.MinimumMicroseconds),
            FormatElapsed(distribution.MedianMicroseconds),
            distribution.P95Microseconds is { } p95
                ? FormatElapsed(p95)
                : $"Collect {PerformanceProbeHistory.MinimumSamplesForP95}+",
            FormatElapsed(distribution.MaximumMicroseconds));

    private static string FormatElapsed(long microseconds) =>
        microseconds >= 1_000
            ? $"{microseconds / 1_000d:N2} ms"
            : $"{microseconds:N0} µs";
}
