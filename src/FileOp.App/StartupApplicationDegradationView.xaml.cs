using FileOp.Core.Performance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class StartupApplicationDegradationView : UserControl
{
    public StartupApplicationDegradationView()
    {
        InitializeComponent();
    }

    public void SetLoading()
    {
        CaptureButton.IsEnabled = false;
        RowsList.ItemsSource = null;
        StatusText.Text =
            $"Reading at most {StartupApplicationDegradationBudget.Default.MaxEvents:N0} retained Diagnostics-Performance Event 101 compatibility record(s) within FileOp's {StartupApplicationDegradationBudget.Default.ReadBudget.TotalSeconds:N0}-second read budget. No startup registrations or process controls are being inspected.";
    }

    public void SetReady(bool ready) =>
        CaptureButton.IsEnabled = ready;

    public void SetUnavailable(string message, bool canRetry = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        CaptureButton.IsEnabled = canRetry;
        RowsList.ItemsSource = null;
        StatusText.Text = message;
    }

    public void Apply(StartupApplicationDegradationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        CaptureButton.IsEnabled = true;
        if (result.Status != StartupApplicationDegradationStatus.Completed ||
            result.Events is not { } events)
        {
            RowsList.ItemsSource = null;
            StatusText.Text =
                $"Startup degradation compatibility history {result.Status} · elapsed {result.Elapsed.TotalMilliseconds:N2} ms. {result.Detail}";
            return;
        }

        RowsList.ItemsSource = events
            .Select(StartupApplicationDegradationDisplayRow.FromEvidence)
            .ToArray();
        var older = result.MoreMatchingEventsAvailable
            ? " At least one older matching retained event exists beyond the visible event budget."
            : " No additional matching retained event was observed by the bounded query.";
        StatusText.Text =
            $"{result.Detail} Visible events remain newest-first in event-log order; FileOp does not duration-rank them. Query elapsed {result.Elapsed.TotalMilliseconds:N2} ms.{older}";
    }

    private async void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App { MainWindow: { } window })
        {
            await window.CapturePerformanceStartupApplicationDegradationAsync();
        }
    }
}

public sealed record StartupApplicationDegradationDisplayRow(
    string ComponentText,
    string RecordedText,
    string IncidentText,
    string TotalTimeText,
    string DegradationTimeText,
    string EventText)
{
    public static StartupApplicationDegradationDisplayRow FromEvidence(
        StartupApplicationDegradationEvent evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var friendly = string.IsNullOrWhiteSpace(evidence.FriendlyName)
            ? evidence.ComponentName
            : $"{evidence.FriendlyName} · {evidence.ComponentName}";
        var version = string.IsNullOrWhiteSpace(evidence.Version)
            ? string.Empty
            : $" · v{evidence.Version}";
        var eventVersion = evidence.EventVersion is { } value
            ? $" · schema v{value}"
            : string.Empty;

        return new StartupApplicationDegradationDisplayRow(
            friendly + version,
            evidence.RecordedAt.ToLocalTime().ToString("g"),
            evidence.IncidentAt.ToLocalTime().ToString("g"),
            $"{evidence.TotalTimeMilliseconds:N0} ms",
            $"{evidence.DegradationTimeMilliseconds:N0} ms",
            $"#{evidence.RecordId}{eventVersion}");
    }
}
