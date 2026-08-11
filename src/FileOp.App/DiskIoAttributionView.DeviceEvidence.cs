using FileOp.Core.Performance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class DiskIoAttributionView
{
    private StackPanel? _deviceEvidencePanel;
    private TextBlock? _deviceEvidenceStatusText;
    private StackPanel? _deviceEvidenceRowsPanel;

    public void SetDeviceEvidenceLoading()
    {
        EnsureDeviceEvidencePanel();
        _deviceEvidenceRowsPanel!.Children.Clear();
        _deviceEvidenceStatusText!.Text =
            "The ETW capture has completed. Querying bounded read-only device context for the physical disks observed in this capture…";
    }

    public void SetDeviceEvidenceUnavailable(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        EnsureDeviceEvidencePanel();
        _deviceEvidenceRowsPanel!.Children.Clear();
        _deviceEvidenceStatusText!.Text = message;
    }

    public void ResetDeviceEvidence()
    {
        EnsureDeviceEvidencePanel();
        _deviceEvidenceRowsPanel!.Children.Clear();
        _deviceEvidenceStatusText!.Text =
            "No post-capture physical-disk device evidence has been queried.";
    }

    public void ApplyDeviceEvidence(
        IReadOnlyList<DiskIoPhysicalDiskDeviceEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        EnsureDeviceEvidencePanel();
        _deviceEvidenceRowsPanel!.Children.Clear();

        foreach (var item in evidence)
        {
            _deviceEvidenceRowsPanel.Children.Add(CreateDeviceEvidenceRow(item));
        }

        if (evidence.Count == 0)
        {
            _deviceEvidenceStatusText!.Text =
                "The completed Disk I/O capture contained no physical-disk rows, so no device metadata query was needed.";
            return;
        }

        var queried = evidence.Count(static item => item.QueryAttempted);
        var skipped = evidence.Count - queried;
        _deviceEvidenceStatusText!.Text = skipped == 0
            ? $"Read-only device properties were queried for all {queried:N0} observed physical disk(s). Standardized NVMe SMART/Health is shown only when that Windows query is supported."
            : $"Read-only device properties were queried for {queried:N0} of {evidence.Count:N0} observed physical disk(s); {skipped:N0} row(s) were left explicit without a device query because of the signed disk-number or 32-disk query bound.";
    }

    private void EnsureDeviceEvidencePanel()
    {
        if (_deviceEvidencePanel is not null)
        {
            return;
        }
        if (Content is not Border { Child: Grid rootGrid })
        {
            throw new InvalidOperationException(
                "Disk I/O attribution device evidence requires the existing Border/Grid host.");
        }

        _deviceEvidenceStatusText = new TextBlock
        {
            Text = "No post-capture physical-disk device evidence has been queried.",
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap,
        };
        _deviceEvidenceRowsPanel = new StackPanel
        {
            Spacing = 8,
        };
        _deviceEvidencePanel = new StackPanel
        {
            Padding = new Thickness(14, 12, 14, 14),
            Spacing = 8,
        };
        _deviceEvidencePanel.Children.Add(new TextBlock
        {
            Text = "Reported device context for observed disks",
            FontSize = 16,
        });
        _deviceEvidencePanel.Children.Add(new TextBlock
        {
            Text = "Queried only after this explicit ETW capture and only for physical disks already present above. Bus, seek-penalty, TRIM and standardized NVMe SMART/Health fields are reported evidence, not an SSD/HDD classification, health score, bottleneck verdict or recommendation. Unsupported and unavailable evidence remains explicit.",
            Opacity = 0.62,
            TextWrapping = TextWrapping.Wrap,
        });
        _deviceEvidencePanel.Children.Add(_deviceEvidenceStatusText);
        _deviceEvidencePanel.Children.Add(_deviceEvidenceRowsPanel);

        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_deviceEvidencePanel, rootGrid.RowDefinitions.Count - 1);
        rootGrid.Children.Add(_deviceEvidencePanel);
    }

    private static Border CreateDeviceEvidenceRow(
        DiskIoPhysicalDiskDeviceEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(new TextBlock
        {
            Text = $"Disk {evidence.PhysicalDiskNumber}",
            FontSize = 15,
        });

        if (!evidence.QueryAttempted)
        {
            content.Children.Add(EvidenceText(evidence.QueryStatusDetail));
            return EvidenceBorder(content);
        }

        var device = evidence.DeviceContext!;
        var nvme = evidence.NvmeHealth!;
        content.Children.Add(EvidenceText(FormatDeviceDescriptor(device)));
        content.Children.Add(EvidenceText($"Seek penalty: {FormatCapability(device.Context?.SeekPenalty, device)}"));
        content.Children.Add(EvidenceText($"TRIM: {FormatCapability(device.Context?.Trim, device)}"));
        content.Children.Add(EvidenceText($"Standardized NVMe SMART/Health: {FormatNvmeHealth(nvme)}"));
        return EvidenceBorder(content);
    }

    private static Border EvidenceBorder(StackPanel content) =>
        new()
        {
            Padding = new Thickness(10),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = content,
        };

    private static TextBlock EvidenceText(string text) =>
        new()
        {
            Text = text,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
        };

    private static string FormatDeviceDescriptor(
        PhysicalDiskDeviceContextResult result)
    {
        if (result.Status != PhysicalDiskDeviceContextStatus.Available ||
            result.Context is not { } context)
        {
            return $"Device context {result.Status}: {result.Detail}";
        }

        var descriptor = context.Descriptor;
        var revision = descriptor.ProductRevision ?? "revision unavailable";
        var serial = descriptor.SerialNumber ?? "serial unavailable";
        return
            $"Device: {descriptor.DisplayName} · bus {descriptor.BusTypeLabel} (raw {descriptor.RawBusType}) · {revision} · {serial} · " +
            $"removable {(descriptor.RemovableMedia ? "reported yes" : "reported no")} · command queueing {(descriptor.CommandQueueing ? "reported yes" : "reported no")}.";
    }

    private static string FormatCapability(
        PhysicalDiskBooleanCapability? capability,
        PhysicalDiskDeviceContextResult result)
    {
        if (result.Status != PhysicalDiskDeviceContextStatus.Available || capability is null)
        {
            return $"not available because device context is {result.Status}. {result.Detail}";
        }

        return capability.Status switch
        {
            PhysicalDiskCapabilityStatus.Available =>
                $"reported {(capability.Value == true ? "yes" : "no")}. {capability.Detail}",
            PhysicalDiskCapabilityStatus.Unsupported =>
                $"unsupported. {capability.Detail}",
            PhysicalDiskCapabilityStatus.Unavailable =>
                $"unavailable. {capability.Detail}",
            _ => throw new InvalidOperationException("Unknown physical-disk capability status."),
        };
    }

    private static string FormatNvmeHealth(NvmeHealthEvidenceResult result)
    {
        if (result.Status != NvmeHealthEvidenceStatus.Available ||
            result.Evidence is not { } evidence)
        {
            return $"{result.Status}. {result.Detail}";
        }

        var warnings = FormatCriticalWarnings(evidence.CriticalWarnings);
        var used = evidence.PercentageUsedEstimate == byte.MaxValue
            ? "life-used estimate >254% (raw 255)"
            : $"life-used estimate {evidence.PercentageUsedEstimate}%";
        return
            $"critical warning 0x{evidence.CriticalWarnings.RawValue:X2} ({warnings}) · " +
            $"temperature {evidence.CompositeTemperatureKelvin:N0} K · spare {evidence.AvailableSparePercent:N0}% / threshold {evidence.AvailableSpareThresholdPercent:N0}% · {used} · " +
            $"power cycles {evidence.PowerCycles} · power-on hours {evidence.PowerOnHours} · unsafe shutdowns {evidence.UnsafeShutdowns} · media errors {evidence.MediaErrors} · error-log entries {evidence.ErrorInfoLogEntryCount} · " +
            $"warning/critical temperature time {evidence.WarningCompositeTemperatureMinutes:N0}/{evidence.CriticalCompositeTemperatureMinutes:N0} min. These are reported standardized fields, not a FileOp health verdict.";
    }

    private static string FormatCriticalWarnings(
        NvmeCriticalWarningEvidence warnings)
    {
        var parts = new List<string>(6);
        if (warnings.AvailableSpareBelowThreshold)
        {
            parts.Add("available spare below threshold");
        }
        if (warnings.TemperatureThreshold)
        {
            parts.Add("temperature threshold");
        }
        if (warnings.ReliabilityDegraded)
        {
            parts.Add("reliability degraded");
        }
        if (warnings.MediaReadOnly)
        {
            parts.Add("media read-only");
        }
        if (warnings.VolatileMemoryBackupFailed)
        {
            parts.Add("volatile-memory backup failed");
        }
        if (warnings.ReservedOrFutureBits != 0)
        {
            parts.Add($"reserved/future bits 0x{warnings.ReservedOrFutureBits:X2}");
        }

        return parts.Count == 0
            ? "no currently defined warning bits set"
            : string.Join(", ", parts);
    }
}
