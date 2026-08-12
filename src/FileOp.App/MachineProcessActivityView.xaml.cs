using FileOp.Core.Performance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class MachineProcessActivityView : UserControl
{
    public MachineProcessActivityView()
    {
        InitializeComponent();
    }

    public void SetLoading()
    {
        CaptureButton.IsEnabled = false;
        RowsList.ItemsSource = null;
        ResetSummary();
        StatusText.Text =
            $"Capturing exactly two readable process-counter frames around a {MachineProcessActivityBudget.Default.SamplingDelay.TotalMilliseconds:N0} ms bounded delay. CPU-time deltas are attributed only to stable PID + process-start identities.";
    }

    public void SetReady(bool ready) =>
        CaptureButton.IsEnabled = ready;

    public void SetUnavailable(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        CaptureButton.IsEnabled = true;
        RowsList.ItemsSource = null;
        ResetSummary();
        StatusText.Text = message;
    }

    public void Apply(MachineProcessActivityResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        CaptureButton.IsEnabled = true;
        if (result.Status != MachineProcessActivityStatus.Completed ||
            result.Report is not { } report)
        {
            RowsList.ItemsSource = null;
            ResetSummary();
            StatusText.Text = result.Detail;
            return;
        }

        RowsList.ItemsSource = report.Rows
            .Select(MachineProcessActivityDisplayRow.FromEvidence)
            .ToArray();
        MatchedText.Text =
            $"{report.StableMatchedProcessCount:N0} · visible {report.Rows.Count:N0}";
        TurnoverText.Text =
            $"{report.StartedDuringSampleCount:N0} / {report.ExitedDuringSampleCount:N0}";
        HiddenCpuText.Text = report.OtherMatchedProcessCount > 0
            ? $"{FormatCpu(report.OtherMatchedProcessorTime)} · {report.OtherMatchedProcessCount:N0} row(s)"
            : "0 ms · none hidden";
        ObserverText.Text = report.CaptureProcessProcessorTime is { } observerCpu &&
            report.CaptureProcessWorkingSetBytes is { } observerWorkingSet
            ? $"CPU Δ {FormatCpu(observerCpu)} · working set {ByteFormatter.Format(observerWorkingSet)}"
            : "Observer counters unavailable";

        var incomplete = report.EvidenceMayBeIncomplete
            ? "Evidence may be incomplete because a snapshot cap was reached or one or more required process counters were inaccessible."
            : "No snapshot cap or inaccessible required-counter evidence was reported.";
        StatusText.Text =
            $"{result.Detail} Visible rows retain CPU-time-delta order; {report.OtherMatchedProcessCount:N0} additional stable matched process(es) remain represented by aggregate hidden CPU time. " +
            $"{incomplete} CPU percentage is intentionally not inferred from these sequential process-counter reads.";
    }

    private async void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App { MainWindow: { } window })
        {
            await window.CapturePerformanceMachineProcessActivityAsync();
        }
    }

    private void ResetSummary()
    {
        MatchedText.Text = "—";
        TurnoverText.Text = "—";
        HiddenCpuText.Text = "—";
        ObserverText.Text = "—";
    }

    private static string FormatCpu(TimeSpan duration) =>
        duration.TotalSeconds >= 1
            ? $"{duration.TotalSeconds:N3} s"
            : $"{duration.TotalMilliseconds:N2} ms";
}

public sealed record MachineProcessActivityDisplayRow(
    string IdentityText,
    string CpuDeltaText,
    string WorkingSetText,
    string PrivateMemoryText,
    string ThreadCountText,
    string StartedText)
{
    public static MachineProcessActivityDisplayRow FromEvidence(
        MachineProcessActivityRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var image = string.IsNullOrWhiteSpace(row.Identity.ImageName)
            ? "image unavailable"
            : row.Identity.ImageName;
        var capture = row.IsCaptureProcess ? " · FileOp observer" : string.Empty;
        return new MachineProcessActivityDisplayRow(
            $"{image} · PID {row.Identity.ProcessId}{capture}",
            row.ProcessorTimeDelta.TotalSeconds >= 1
                ? $"{row.ProcessorTimeDelta.TotalSeconds:N3} s"
                : $"{row.ProcessorTimeDelta.TotalMilliseconds:N2} ms",
            ByteFormatter.Format(row.WorkingSetBytes),
            ByteFormatter.Format(row.PrivateMemoryBytes),
            $"{row.ThreadCount:N0}",
            row.Identity.StartedAt.ToLocalTime().ToString("g"));
    }
}
