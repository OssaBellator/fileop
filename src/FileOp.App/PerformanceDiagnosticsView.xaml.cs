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
        StatusText.Text = "Running bounded Search and Storage probes through the current FileOp source…";
        RefreshButton.IsEnabled = false;
    }

    public void SetUnavailable(string message)
    {
        StatusText.Text = message;
        ProbeList.ItemsSource = null;
        RefreshButton.IsEnabled = true;
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
        RefreshButton.IsEnabled = true;
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
