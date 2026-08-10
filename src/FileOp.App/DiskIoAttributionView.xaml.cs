using FileOp.Core.Performance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class DiskIoAttributionView : UserControl
{
    private bool _captureActive;

    public DiskIoAttributionView()
    {
        InitializeComponent();
    }

    public event EventHandler? CaptureRequested;

    public void SetLoading()
    {
        _captureActive = true;
        StatusText.Text =
            "Capturing a bounded 2-second Disk I/O sample. FileOp will stop its owned ETW session, drain the consumer, then report evidence quality…";
        CaptureButton.IsEnabled = false;
    }

    public void SetReady(bool ready) =>
        CaptureButton.IsEnabled = ready && !_captureActive;

    public void SetUnavailable(string message)
    {
        _captureActive = false;
        ResetRows();
        ResetSummary();
        StatusText.Text = message;
    }

    public void Reset()
    {
        _captureActive = false;
        ResetRows();
        ResetSummary();
        StatusText.Text = "No Disk I/O attribution capture has been run.";
    }

    public void Apply(DiskIoCaptureResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _captureActive = false;
        if (result.Status != DiskIoCaptureStatus.Completed || result.Report is null)
        {
            ResetRows();
            StopReasonText.Text = result.Status.ToString();
            AcceptedEventsText.Text = "—";
            LossText.Text = "Unknown";
            DurationText.Text = "—";
            StatusText.Text = result.Detail;
            return;
        }

        var report = result.Report;
        DiskList.ItemsSource = report.Disks
            .Select(DiskIoDiskRow.FromAttribution)
            .ToArray();
        ProcessList.ItemsSource = report.Disks
            .SelectMany(static disk => disk.Owners.Select(owner =>
                DiskIoProcessRow.FromAttribution(disk.PhysicalDiskNumber, owner)))
            .ToArray();

        StopReasonText.Text = result.StopReason switch
        {
            DiskIoCaptureStopReason.DurationElapsed => "Duration",
            DiskIoCaptureStopReason.ObservationLimitReached => "Observation cap",
            _ => "Unknown",
        };
        AcceptedEventsText.Text = $"{report.AcceptedEventCount:N0}";
        LossText.Text =
            $"{FormatCount(result.LostEventCount)} events · {FormatCount(result.LostBufferCount)} buffers";
        DurationText.Text = $"{report.ObservationDuration.TotalSeconds:N2} s";

        var partialDiskCount = report.Disks.Count(static disk =>
            disk.TotalBytes > 0 && disk.AttributionCoveragePercent is not >= 100d);
        var quality = result.EvidenceMayBeIncomplete
            ? "Evidence may be incomplete because the capture hit its observation cap or ETW reported loss."
            : "No ETW loss or observation-cap truncation was reported for this short sample.";
        if (partialDiskCount > 0)
        {
            quality +=
                $" Process attribution is partial on {partialDiskCount:N0} physical disk(s); unattributed traffic remains visible.";
        }

        StatusText.Text = $"{quality} {result.Detail}";
    }

    private void CaptureButton_Click(object sender, RoutedEventArgs e) =>
        CaptureRequested?.Invoke(this, EventArgs.Empty);

    private void ResetRows()
    {
        DiskList.ItemsSource = null;
        ProcessList.ItemsSource = null;
    }

    private void ResetSummary()
    {
        StopReasonText.Text = "—";
        AcceptedEventsText.Text = "—";
        LossText.Text = "—";
        DurationText.Text = "—";
    }

    private static string FormatCount(long? count) =>
        count is { } value ? $"{value:N0}" : "Unknown";
}

public sealed record DiskIoDiskRow(
    string DiskText,
    string ReadText,
    string WriteText,
    string OperationsText,
    string AttributionText,
    string UnattributedText)
{
    public static DiskIoDiskRow FromAttribution(DiskIoDiskAttribution disk)
    {
        var coverage = disk.AttributionCoveragePercent is { } percent
            ? $"{percent:N1}%"
            : "No byte traffic";
        var unattributed = $"{ByteFormatter.Format(disk.UnattributedBytes)} unattributed";
        if (disk.OtherIdentifiedOwnerCount > 0)
        {
            unattributed +=
                $" · {ByteFormatter.Format(disk.OtherIdentifiedBytes)} across {disk.OtherIdentifiedOwnerCount:N0} additional identified owner(s)";
        }

        return new DiskIoDiskRow(
            $"Disk {disk.PhysicalDiskNumber}",
            ByteFormatter.Format(disk.ReadBytes),
            ByteFormatter.Format(disk.WriteBytes),
            $"{disk.TotalOperations:N0}",
            coverage,
            unattributed);
    }
}

public sealed record DiskIoProcessRow(
    string DiskText,
    string ProcessInstanceText,
    string ReadText,
    string WriteText,
    string OperationsText,
    string ShareText)
{
    public static DiskIoProcessRow FromAttribution(
        uint physicalDiskNumber,
        DiskIoProcessAttribution attribution)
    {
        var owner = attribution.Owner;
        var name = string.IsNullOrWhiteSpace(owner.ImageName)
            ? $"PID {owner.ProcessId}"
            : $"{owner.ImageName} · PID {owner.ProcessId}";
        var identity = owner.StartedAt is { } startedAt
            ? $"{name} · started {startedAt.ToLocalTime():g}"
            : $"{name} · process start unavailable";

        return new DiskIoProcessRow(
            $"Disk {physicalDiskNumber}",
            identity,
            ByteFormatter.Format(attribution.ReadBytes),
            ByteFormatter.Format(attribution.WriteBytes),
            $"{attribution.TotalOperations:N0}",
            $"{attribution.ObservedByteSharePercent:N1}%");
    }
}
