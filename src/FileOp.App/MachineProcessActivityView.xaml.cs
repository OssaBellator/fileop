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
        ResetProcessSummary();
        ResetSystemCpuSummary();
        StatusText.Text =
            $"Capturing exactly two readable process-counter frames around a {MachineProcessActivityBudget.Default.SamplingDelay.TotalMilliseconds:N0} ms bounded delay. CPU-time deltas are attributed only to stable PID + process-start identities.";
        SystemCpuStatusText.Text =
            $"Capturing a separate Windows GetSystemTimes interval in parallel around its own {SystemCpuActivityBudget.Default.SamplingDelay.TotalMilliseconds:N0} ms bounded delay. The two evidence streams have separate observation boundaries.";
    }

    public void SetReady(bool ready) =>
        CaptureButton.IsEnabled = ready;

    public void SetUnavailable(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        CaptureButton.IsEnabled = true;
        RowsList.ItemsSource = null;
        ResetProcessSummary();
        ResetSystemCpuSummary();
        StatusText.Text = message;
        SystemCpuStatusText.Text =
            "System CPU interval was not retained because the combined explicit machine-activity action failed.";
    }

    public void Apply(
        MachineProcessActivityResult result,
        SystemCpuActivityResult systemCpuActivity)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(systemCpuActivity);
        CaptureButton.IsEnabled = true;
        ApplySystemCpu(systemCpuActivity);

        if (result.Status != MachineProcessActivityStatus.Completed ||
            result.Report is not { } report)
        {
            RowsList.ItemsSource = null;
            ResetProcessSummary();
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
            $"{incomplete} Process CPU percentage is intentionally not inferred from these sequential process-counter reads or from the separate Windows system CPU interval.";
    }

    private void ApplySystemCpu(SystemCpuActivityResult result)
    {
        if (result.Status != SystemCpuActivityStatus.Completed ||
            result.Evidence is not { } evidence)
        {
            ResetSystemCpuSummary();
            SystemCpuStatusText.Text =
                $"Windows system CPU interval {result.Status}. {result.Detail}";
            return;
        }

        SystemCpuBusyText.Text = evidence.BusyPercent is { } busyPercent
            ? $"{busyPercent:N2}%"
            : "No percentage";
        SystemCpuTimeText.Text =
            $"{FormatCpu(evidence.BusyProcessorTimeDelta)} / {FormatCpu(evidence.TotalProcessorTimeDelta)} · idle {FormatCpu(evidence.IdleProcessorTimeDelta)}";
        SystemCpuWallText.Text =
            $"{evidence.ObservationWallDuration.TotalMilliseconds:N2} ms · {evidence.StartObservedAt.ToLocalTime():HH:mm:ss.fff}–{evidence.EndObservedAt.ToLocalTime():HH:mm:ss.fff}";
        SystemCpuOverheadText.Text = FormatCpu(result.ProviderOverheadDuration);
        SystemCpuStatusText.Text =
            $"{result.Detail} This interval is separate from the process rows below; FileOp does not treat visible process deltas as a decomposition of Windows busy time.";
    }

    private async void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App { MainWindow: { } window })
        {
            await window.CapturePerformanceMachineProcessActivityAsync();
        }
    }

    private void ResetProcessSummary()
    {
        MatchedText.Text = "—";
        TurnoverText.Text = "—";
        HiddenCpuText.Text = "—";
        ObserverText.Text = "—";
    }

    private void ResetSystemCpuSummary()
    {
        SystemCpuBusyText.Text = "—";
        SystemCpuTimeText.Text = "—";
        SystemCpuWallText.Text = "—";
        SystemCpuOverheadText.Text = "—";
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
