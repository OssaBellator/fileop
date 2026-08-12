using FileOp.Core.Performance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class PerformanceDiagnosticsView
{
    private bool _systemPhysicalMemoryPanelAttached;
    private bool _systemPhysicalMemoryRefreshSubscribed;
    private bool _systemPhysicalMemoryRefreshActive;
    private TextBlock? _systemPhysicalMemoryStatusText;
    private TextBlock? _systemPhysicalMemoryTotalText;
    private TextBlock? _systemPhysicalMemoryAvailableText;
    private TextBlock? _systemPhysicalMemoryUsedText;
    private TextBlock? _systemPhysicalMemoryLoadText;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        EnsureSystemPhysicalMemoryPanel();
        if (!_systemPhysicalMemoryRefreshSubscribed)
        {
            RefreshRequested += PerformanceDiagnostics_SystemPhysicalMemoryRefreshRequested;
            _systemPhysicalMemoryRefreshSubscribed = true;
        }
    }

    private void EnsureSystemPhysicalMemoryPanel()
    {
        if (_systemPhysicalMemoryPanelAttached)
        {
            return;
        }
        if (Content is not Grid rootGrid)
        {
            throw new InvalidOperationException(
                "System physical-memory evidence requires the existing Performance root Grid.");
        }

        _systemPhysicalMemoryStatusText = new TextBlock
        {
            Text = "Physical-memory evidence has not been refreshed yet.",
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap,
        };
        _systemPhysicalMemoryTotalText = MetricValue();
        _systemPhysicalMemoryAvailableText = MetricValue();
        _systemPhysicalMemoryUsedText = MetricValue();
        _systemPhysicalMemoryLoadText = MetricValue();

        var metrics = new Grid { ColumnSpacing = 12 };
        for (var index = 0; index < 4; index++)
        {
            metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        metrics.Children.Add(MetricCell("Total physical", _systemPhysicalMemoryTotalText, 0));
        metrics.Children.Add(MetricCell("Available physical", _systemPhysicalMemoryAvailableText, 1));
        metrics.Children.Add(MetricCell("Used physical", _systemPhysicalMemoryUsedText, 2));
        metrics.Children.Add(MetricCell("Windows load (approx.)", _systemPhysicalMemoryLoadText, 3));

        var content = new StackPanel
        {
            Spacing = 10,
        };
        content.Children.Add(new TextBlock
        {
            Text = "System physical memory",
            FontSize = 16,
        });
        content.Children.Add(new TextBlock
        {
            Text = "One GlobalMemoryStatusEx snapshot refreshed only by Refresh diagnostics. Available memory is not labeled wasted/recoverable RAM, and Windows' approximate load percentage is not a FileOp memory-health or cleanup verdict.",
            Opacity = 0.68,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(metrics);
        content.Children.Add(_systemPhysicalMemoryStatusText);

        var card = new Border
        {
            Padding = new Thickness(14),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = content,
        };

        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(card, rootGrid.RowDefinitions.Count - 1);
        rootGrid.Children.Add(card);
        _systemPhysicalMemoryPanelAttached = true;
    }

    private async void PerformanceDiagnostics_SystemPhysicalMemoryRefreshRequested(
        object? sender,
        EventArgs e)
    {
        if (_systemPhysicalMemoryRefreshActive)
        {
            return;
        }

        _systemPhysicalMemoryRefreshActive = true;
        SetSystemPhysicalMemoryLoading();
        try
        {
            if (Application.Current is not App { MainWindow: { } window })
            {
                SetSystemPhysicalMemoryUnavailable(
                    "Physical-memory evidence could not be queried because the active FileOp window is unavailable.");
                return;
            }

            var result = await window.CapturePerformanceSystemPhysicalMemoryStatusAsync();
            ApplySystemPhysicalMemoryStatus(result);
        }
        catch (OperationCanceledException)
        {
            // Window lifetime cancellation is expected during shutdown.
        }
        catch (Exception exception)
        {
            SetSystemPhysicalMemoryUnavailable(
                $"Physical-memory evidence query failed: {exception.Message}");
        }
        finally
        {
            _systemPhysicalMemoryRefreshActive = false;
        }
    }

    private void SetSystemPhysicalMemoryLoading()
    {
        EnsureSystemPhysicalMemoryPanel();
        SetMetricValues("—", "—", "—", "—");
        _systemPhysicalMemoryStatusText!.Text =
            "Reading one bounded Windows physical-memory snapshot…";
    }

    private void SetSystemPhysicalMemoryUnavailable(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        EnsureSystemPhysicalMemoryPanel();
        SetMetricValues("—", "—", "—", "—");
        _systemPhysicalMemoryStatusText!.Text = message;
    }

    private void ApplySystemPhysicalMemoryStatus(
        SystemPhysicalMemoryStatusResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        EnsureSystemPhysicalMemoryPanel();
        if (result.Availability != SystemPhysicalMemoryStatusAvailability.Available ||
            result.Status is not { } status)
        {
            SetMetricValues("—", "—", "—", "—");
            _systemPhysicalMemoryStatusText!.Text =
                $"Physical-memory evidence {result.Availability} · query elapsed {result.Elapsed.TotalMilliseconds:N2} ms. {result.Detail}";
            return;
        }

        SetMetricValues(
            FormatBytes(status.TotalPhysicalBytes),
            FormatBytes(status.AvailablePhysicalBytes),
            FormatBytes(status.UsedPhysicalBytes),
            $"{status.WindowsMemoryLoadPercent:N0}%");
        _systemPhysicalMemoryStatusText!.Text =
            $"{result.Detail} Query elapsed {result.Elapsed.TotalMilliseconds:N2} ms. The Windows load percentage is preserved as reported and is not recomputed from the byte counters.";
    }

    private void SetMetricValues(
        string total,
        string available,
        string used,
        string load)
    {
        _systemPhysicalMemoryTotalText!.Text = total;
        _systemPhysicalMemoryAvailableText!.Text = available;
        _systemPhysicalMemoryUsedText!.Text = used;
        _systemPhysicalMemoryLoadText!.Text = load;
    }

    private static TextBlock MetricValue() =>
        new()
        {
            Text = "—",
            FontSize = 17,
        };

    private static StackPanel MetricCell(
        string label,
        TextBlock value,
        int column)
    {
        var cell = new StackPanel { Spacing = 2 };
        cell.Children.Add(new TextBlock
        {
            Text = label,
            Opacity = 0.62,
        });
        cell.Children.Add(value);
        Grid.SetColumn(cell, column);
        return cell;
    }

    private static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value:N0} {units[unit]}"
            : $"{value:N2} {units[unit]}";
    }
}
