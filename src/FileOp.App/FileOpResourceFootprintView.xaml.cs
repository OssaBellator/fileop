using FileOp.Core.Performance;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class FileOpResourceFootprintView : UserControl
{
    public FileOpResourceFootprintView()
    {
        InitializeComponent();
    }

    public void SetLoading() =>
        StatusText.Text = "Refreshing current FileOp process resource counters…";

    public void Apply(FileOpProcessResourceSnapshot? snapshot, string? status)
    {
        if (snapshot is null || !snapshot.HasValidNonNegativeEvidence)
        {
            ResetMetrics();
            StatusText.Text = string.IsNullOrWhiteSpace(status)
                ? "FileOp process resource counters are unavailable for this refresh."
                : status;
            return;
        }

        WorkingSetText.Text = ByteFormatter.Format(snapshot.WorkingSetBytes);
        PrivateMemoryText.Text = ByteFormatter.Format(snapshot.PrivateMemoryBytes);
        ManagedMemoryText.Text = ByteFormatter.Format(snapshot.ManagedMemoryBytes);
        ThreadCountText.Text = $"{snapshot.ThreadCount:N0}";
        PeakWorkingSetText.Text = ByteFormatter.Format(snapshot.PeakWorkingSetBytes);
        CpuTimeText.Text = FormatDuration(snapshot.TotalProcessorTime);
        UptimeText.Text = FormatDuration(snapshot.Uptime);
        StatusText.Text =
            $"Process started {snapshot.ProcessStartedAt.ToLocalTime():g}. Working set/private memory are current OS process counters; " +
            "managed memory uses GC.GetTotalMemory(false) without forcing a collection. CPU time is cumulative across FileOp threads since process start, not current CPU utilisation.";
    }

    private void ResetMetrics()
    {
        WorkingSetText.Text = "—";
        PrivateMemoryText.Text = "—";
        ManagedMemoryText.Text = "—";
        ThreadCountText.Text = "—";
        PeakWorkingSetText.Text = "—";
        CpuTimeText.Text = "—";
        UptimeText.Text = "—";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
        {
            return $"{duration.TotalDays:N1} d";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{duration.TotalHours:N1} h";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{duration.TotalMinutes:N1} min";
        }

        return $"{Math.Max(0, duration.TotalSeconds):N1} s";
    }
}
