using FileOp.Core.Performance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class PerformanceDiagnosticsView : UserControl
{
    public PerformanceDiagnosticsView()
    {
        InitializeComponent();
    }

    public event EventHandler? RefreshRequested;

    public void SetLoading()
    {
        StatusText.Text = "Running bounded Search, Storage and index-database probes through the current FileOp source…";
        IndexStatusText.Text = "Refreshing helper-owned index database metrics…";
        RefreshButton.IsEnabled = false;
    }

    public void SetUnavailable(string message)
    {
        StatusText.Text = message;
        ProbeList.ItemsSource = null;
        ResetIndexMetrics();
        IndexStatusText.Text = message;
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
    }

    private void ResetIndexMetrics()
    {
        IndexFootprintText.Text = "—";
        IndexDatabaseText.Text = "—";
        IndexWalText.Text = "—";
        IndexReusableText.Text = "—";
        IndexCacheText.Text = "—";
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
