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
            ResetSummary();
            StopReasonText.Text = result.Status.ToString();
            LossText.Text = "Unknown";
            TimingEvidenceStatusText.Text =
                "Response-duration evidence is unavailable because this capture did not complete with an attribution report.";
            InvestigationSummaryText.Text =
                "Storage I/O investigation cues are unavailable because this capture did not complete with an attribution report.";
            ProcessTimingEvidenceStatusText.Text =
                "Process-bound response timing is unavailable because this capture did not complete with an attribution report.";
            StatusText.Text = result.Detail;
            return;
        }

        var report = result.Report;
        DiskList.ItemsSource = report.Disks
            .Select(DiskIoDiskRow.FromAttribution)
            .ToArray();
        TimingList.ItemsSource = result.ResponseTimings
            .Select(DiskIoTimingRow.FromEvidence)
            .ToArray();

        var processRows = new List<DiskIoProcessRow>();
        var processTimingAvailable =
            result.ProcessResponseTimings.Count > 0 &&
            result.ProcessResponseTimings.Count == report.Disks.Count;
        for (var diskIndex = 0; diskIndex < report.Disks.Count; diskIndex++)
        {
            var disk = report.Disks[diskIndex];
            var diskTiming = processTimingAvailable
                ? result.ProcessResponseTimings[diskIndex]
                : null;
            for (var ownerIndex = 0; ownerIndex < disk.Owners.Count; ownerIndex++)
            {
                var ownerTiming = diskTiming is not null && ownerIndex < diskTiming.Owners.Count
                    ? diskTiming.Owners[ownerIndex]
                    : null;
                processRows.Add(DiskIoProcessRow.FromAttribution(
                    disk.PhysicalDiskNumber,
                    disk.Owners[ownerIndex],
                    ownerTiming));
            }
        }
        ProcessList.ItemsSource = processRows.ToArray();

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

        var timingSampleCount = result.ResponseTimings.Sum(static timing => timing.SampleCount);
        TimingEvidenceStatusText.Text = result.ResponseTimings.Count == 0
            ? report.AcceptedEventCount == 0
                ? "No accepted Disk I/O completions were observed, so there are no response-duration samples."
                : "This completed result does not carry typed response-duration evidence. No latency value is inferred."
            : $"{timingSampleCount:N0} response-duration sample(s) across {result.ResponseTimings.Count:N0} physical disk(s), aligned with the accepted completion evidence.";

        var investigation = DiskIoInvestigationSummaryAnalyzer.Analyze(result);
        InvestigationSummaryText.Text = FormatInvestigationSummary(investigation);

        if (result.ProcessResponseTimings.Count == 0)
        {
            ProcessTimingEvidenceStatusText.Text = report.AcceptedEventCount == 0
                ? "No accepted Disk I/O completions were observed, so there is no process-bound response timing."
                : "This completed result does not carry typed process-bound response timing. No per-process latency is inferred.";
        }
        else
        {
            var visibleSamples = result.ProcessResponseTimings.Sum(static timing => timing.VisibleOwnerSamples);
            var hiddenSamples = result.ProcessResponseTimings.Sum(static timing => timing.OtherIdentifiedSamples);
            var unattributedSamples = result.ProcessResponseTimings.Sum(static timing => timing.UnattributedSamples);
            ProcessTimingEvidenceStatusText.Text =
                $"{visibleSamples:N0} response-duration sample(s) are attached to {processRows.Count:N0} byte-ranked visible process row(s); " +
                $"{hiddenSamples:N0} identified-hidden and {unattributedSamples:N0} unresolved-owner sample(s) remain outside the visible process rows.";
        }

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
        TimingList.ItemsSource = null;
        ProcessList.ItemsSource = null;
    }

    private void ResetSummary()
    {
        StopReasonText.Text = "—";
        AcceptedEventsText.Text = "—";
        LossText.Text = "—";
        DurationText.Text = "—";
        TimingEvidenceStatusText.Text = "No response-duration evidence has been captured.";
        InvestigationSummaryText.Text = "No storage I/O investigation summary has been captured.";
        ProcessTimingEvidenceStatusText.Text = "No process-bound response timing has been captured.";
    }

    private static string FormatInvestigationSummary(DiskIoInvestigationSummary summary)
    {
        if (summary.AcceptedEventCount == 0)
        {
            return "No accepted Disk I/O completions were observed, so this capture has no comparative storage I/O cues.";
        }

        var parts = new List<string>();
        parts.Add(summary.EvidenceMayBeIncomplete
            ? "Capture evidence may be incomplete because the observation cap or ETW loss can omit completions; comparative cues below are partial."
            : "No ETW loss or observation-cap truncation was reported for these comparative cues.");

        if (summary.HighestObservedP95 is { } response)
        {
            var operation = response.Operation switch
            {
                DiskIoOperationKind.Read => "Read",
                DiskIoOperationKind.Write => "Write",
                DiskIoOperationKind.Flush => "Flush",
                _ => response.Operation.ToString(),
            };
            parts.Add(
                $"Highest observed eligible p95: Disk {response.PhysicalDiskNumber} {operation} · " +
                $"{DiskIoTimingRow.FormatSummary(response.Timing)}. " +
                "This is the largest eligible p95 within this capture, not a device-performance threshold.");
        }
        else
        {
            parts.Add(
                $"No operation has an eligible p95; each operation either has fewer than {DiskIoResponseTimingAnalyzer.MinimumSamplesForP95} samples or no typed response timing.");
        }

        if (summary.LargestObservedByteDisk is { } disk)
        {
            var coverage = disk.AttributionCoveragePercent is { } percent
                ? $"{percent:N1}% attributed"
                : "attribution coverage unavailable";
            parts.Add(
                $"Largest observed byte volume: Disk {disk.PhysicalDiskNumber} · {ByteFormatter.Format(disk.TotalBytes)} " +
                $"across {disk.TotalOperations:N0} operation(s) · {coverage} · " +
                $"{ByteFormatter.Format(disk.UnattributedBytes)} unattributed.");
        }
        else
        {
            parts.Add("No transferred read/write bytes were observed in this capture.");
        }

        if (summary.LargestIdentifiedOwner is { } owner)
        {
            var identity = string.IsNullOrWhiteSpace(owner.Owner.ImageName)
                ? $"PID {owner.Owner.ProcessId}"
                : $"{owner.Owner.ImageName} · PID {owner.Owner.ProcessId}";
            parts.Add(
                $"Largest identified owner by observed bytes: {identity} on Disk {owner.PhysicalDiskNumber} · " +
                $"{ByteFormatter.Format(owner.TotalBytes)} across {owner.TotalOperations:N0} operation(s) · " +
                $"{owner.ObservedByteSharePercent:N1}% of that disk's observed bytes. " +
                "Issuing ownership is an association, not proof that this process caused device response delay.");
        }
        else
        {
            parts.Add("No identified process owner carried transferred read/write bytes in this capture.");
        }

        return string.Join(" ", parts);
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

public sealed record DiskIoTimingRow(
    string DiskText,
    string ReadTimingText,
    string WriteTimingText,
    string FlushTimingText)
{
    public static DiskIoTimingRow FromEvidence(DiskIoDiskResponseTiming timing) =>
        new(
            $"Disk {timing.PhysicalDiskNumber}",
            FormatSummary(timing.Reads),
            FormatSummary(timing.Writes),
            FormatSummary(timing.Flushes));

    internal static string FormatSummary(DiskIoResponseTimingSummary? summary)
    {
        if (summary is null)
        {
            return "No samples";
        }

        var sampleLabel = summary.SampleCount == 1 ? "sample" : "samples";
        var p95 = summary.P95 is { } percentile
            ? FormatDuration(percentile)
            : $"— (<{DiskIoResponseTimingAnalyzer.MinimumSamplesForP95} samples)";
        return
            $"{summary.SampleCount:N0} {sampleLabel} · " +
            $"min {FormatDuration(summary.Minimum)} · " +
            $"median {FormatDuration(summary.Median)} · " +
            $"p95 {p95} · max {FormatDuration(summary.Maximum)}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMilliseconds(1))
        {
            return $"{duration.TotalMicroseconds:N0} µs";
        }
        if (duration < TimeSpan.FromSeconds(1))
        {
            return $"{duration.TotalMilliseconds:N2} ms";
        }
        return $"{duration.TotalSeconds:N3} s";
    }
}

public sealed record DiskIoProcessRow(
    string DiskText,
    string ProcessInstanceText,
    string ReadText,
    string WriteText,
    string OperationsText,
    string ShareText,
    string ResponseTimingText)
{
    public static DiskIoProcessRow FromAttribution(
        uint physicalDiskNumber,
        DiskIoProcessAttribution attribution,
        DiskIoProcessResponseTiming? timing)
    {
        var owner = attribution.Owner;
        var name = string.IsNullOrWhiteSpace(owner.ImageName)
            ? $"PID {owner.ProcessId}"
            : $"{owner.ImageName} · PID {owner.ProcessId}";
        var identity = owner.StartedAt is { } startedAt
            ? $"{name} · started {startedAt.ToLocalTime():g}"
            : $"{name} · process start unavailable";
        var responseTiming = timing is null
            ? "No typed timing"
            : $"Read: {DiskIoTimingRow.FormatSummary(timing.Reads)}\n" +
              $"Write: {DiskIoTimingRow.FormatSummary(timing.Writes)}\n" +
              $"Flush: {DiskIoTimingRow.FormatSummary(timing.Flushes)}";

        return new DiskIoProcessRow(
            $"Disk {physicalDiskNumber}",
            identity,
            ByteFormatter.Format(attribution.ReadBytes),
            ByteFormatter.Format(attribution.WriteBytes),
            $"{attribution.TotalOperations:N0}",
            $"{attribution.ObservedByteSharePercent:N1}%",
            responseTiming);
    }
}
