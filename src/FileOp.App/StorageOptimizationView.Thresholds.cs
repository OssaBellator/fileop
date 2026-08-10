using FileOp.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class StorageOptimizationView
{
    private readonly record struct SizeThresholdOption(long Value, string Label);
    private readonly record struct AgeThresholdOption(int Value, string Label);

    private ComboBox? _largeThresholdComboBox;
    private ComboBox? _sameSizeThresholdComboBox;
    private ComboBox? _staleAgeComboBox;
    private TextBlock? _thresholdStatusText;
    private StorageOptimizationDisplayThresholds? _displayThresholds;
    private bool _thresholdPanelInitialized;
    private bool _thresholdControlsUpdating;
    private bool _thresholdOverlayApplying;
    private bool _thresholdRefreshQueued;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        EnsureThresholdPanel();
    }

    private void EnsureThresholdPanel()
    {
        if (_thresholdPanelInitialized ||
            Content is not Grid rootGrid ||
            rootGrid.Children
                .OfType<ScrollViewer>()
                .FirstOrDefault(static child => Grid.GetRow(child) == 2) is not { Content: StackPanel contentStack })
        {
            return;
        }

        _largeThresholdComboBox = CreateThresholdComboBox();
        _sameSizeThresholdComboBox = CreateThresholdComboBox();
        _staleAgeComboBox = CreateThresholdComboBox();
        _largeThresholdComboBox.SelectionChanged += ThresholdComboBox_SelectionChanged;
        _sameSizeThresholdComboBox.SelectionChanged += ThresholdComboBox_SelectionChanged;
        _staleAgeComboBox.SelectionChanged += ThresholdComboBox_SelectionChanged;

        var optionGrid = new Grid
        {
            ColumnSpacing = 12,
        };
        optionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        optionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        optionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddThresholdColumn(optionGrid, 0, "Large file minimum", _largeThresholdComboBox);
        AddThresholdColumn(optionGrid, 1, "Same-size minimum", _sameSizeThresholdComboBox);
        AddThresholdColumn(optionGrid, 2, "Stale age minimum", _staleAgeComboBox);

        _thresholdStatusText = new TextBlock
        {
            Text = "Threshold presets become available after Optimize analysis loads.",
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        };
        var panel = new StackPanel
        {
            Spacing = 10,
        };
        panel.Children.Add(new TextBlock
        {
            Text = "View thresholds",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Session-only stricter filters for the already bounded native analysis. FileOp will not let these controls request evidence below the helper's analysis baseline.",
            Opacity = 0.68,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(optionGrid);
        panel.Children.Add(_thresholdStatusText);

        var insertionIndex = Math.Min(2, contentStack.Children.Count);
        contentStack.Children.Insert(insertionIndex, panel);
        SameSizeGroupsList.RegisterPropertyChangedCallback(
            ItemsControl.ItemsSourceProperty,
            SameSizeGroupsItemsSource_Changed);
        _thresholdPanelInitialized = true;
        ScheduleThresholdOverlayRefresh();
    }

    private static ComboBox CreateThresholdComboBox() =>
        new()
        {
            MinWidth = 180,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

    private static void AddThresholdColumn(
        Grid grid,
        int column,
        string label,
        ComboBox comboBox)
    {
        var stack = new StackPanel
        {
            Spacing = 4,
        };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Opacity = 0.7,
        });
        stack.Children.Add(comboBox);
        Grid.SetColumn(stack, column);
        grid.Children.Add(stack);
    }

    private void SameSizeGroupsItemsSource_Changed(
        DependencyObject sender,
        DependencyProperty property)
    {
        if (!_thresholdOverlayApplying)
        {
            ScheduleThresholdOverlayRefresh();
        }
    }

    private void ScheduleThresholdOverlayRefresh()
    {
        if (_thresholdRefreshQueued || _analysis is null || !_thresholdPanelInitialized)
        {
            return;
        }

        _thresholdRefreshQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _thresholdRefreshQueued = false;
            if (_analysis is not null)
            {
                ApplyThresholdOverlay();
            }
        });
    }

    private void ThresholdComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_thresholdControlsUpdating ||
            _analysis is null ||
            _largeThresholdComboBox?.SelectedItem is not SizeThresholdOption large ||
            _sameSizeThresholdComboBox?.SelectedItem is not SizeThresholdOption sameSize ||
            _staleAgeComboBox?.SelectedItem is not AgeThresholdOption stale)
        {
            return;
        }

        var thresholds = new StorageOptimizationDisplayThresholds(
            large.Value,
            sameSize.Value,
            stale.Value);
        StorageOptimizationThresholdFilter.ValidateAgainstAnalysis(_analysis, thresholds);
        _displayThresholds = thresholds;
        ApplyThresholdOverlay();
    }

    private void ApplyThresholdOverlay()
    {
        if (_analysis is not { } analysis || !_thresholdPanelInitialized)
        {
            return;
        }

        EnsureThresholdControls(analysis);
        var thresholds = _displayThresholds ?? StorageOptimizationDisplayThresholds.FromAnalysis(analysis);
        var filtered = StorageOptimizationThresholdFilter.Apply(analysis, thresholds);

        _thresholdOverlayApplying = true;
        try
        {
            LargestFilesList.ItemsSource = filtered.LargestFiles
                .Select(StorageOptimizationFileRow.FromCandidate)
                .ToArray();
            StaleFilesList.ItemsSource = filtered.StaleLargeFiles
                .Select(StorageOptimizationFileRow.FromCandidate)
                .ToArray();

            var busy = _sameSizeVerificationControlsBlocked || _sameSizeVerificationActiveIndex.HasValue;
            SameSizeGroupsList.ItemsSource = analysis.SameSizeCandidateGroups
                .Select((group, index) => (Group: group, Index: index))
                .Where(item => item.Group.LogicalBytesPerFile >= thresholds.SameSizeMinimumBytes)
                .Select(item =>
                {
                    _sameSizeVerificationResults.TryGetValue(item.Index, out var verification);
                    _sameSizeVerificationMessages.TryGetValue(item.Index, out var message);
                    return StorageSameSizeGroupRow.FromGroup(
                        item.Group,
                        item.Index,
                        verification,
                        message,
                        _sameSizeVerificationActiveIndex == item.Index,
                        busy);
                })
                .ToArray();
        }
        finally
        {
            _thresholdOverlayApplying = false;
        }

        StatusText.Text =
            $"Showing {filtered.LargestFiles.Count:N0} large file(s) · " +
            $"{filtered.StaleLargeFiles.Count:N0} at least {thresholds.StaleAgeDays:N0} days old · " +
            $"{filtered.SameSizeCandidateGroups.Count:N0} same-size candidate group(s) · " +
            $"{ByteFormatter.Format(filtered.SameSizePotentialLogicalSavingsUpperBound)} displayed logical candidate upper bound";
        if (_thresholdStatusText is not null)
        {
            _thresholdStatusText.Text =
                $"Native analysis baseline: large ≥ {ByteFormatter.Format(analysis.Policy.LargeFileMinimumBytes)}, " +
                $"same-size ≥ {ByteFormatter.Format(analysis.Policy.SameSizeMinimumBytes)}, stale ≥ {analysis.Policy.StaleAgeDays:N0} days. " +
                $"Current view: large ≥ {ByteFormatter.Format(thresholds.LargeFileMinimumBytes)}, " +
                $"same-size ≥ {ByteFormatter.Format(thresholds.SameSizeMinimumBytes)}, stale ≥ {thresholds.StaleAgeDays:N0} days. " +
                "These controls only narrow the already bounded result; they do not rerun the helper or imply evidence below its baseline.";
        }
    }

    private void EnsureThresholdControls(StorageOptimizationAnalysis analysis)
    {
        if (_largeThresholdComboBox is null ||
            _sameSizeThresholdComboBox is null ||
            _staleAgeComboBox is null)
        {
            return;
        }

        var baseline = StorageOptimizationDisplayThresholds.FromAnalysis(analysis);
        var current = _displayThresholds;
        if (current is null ||
            current.LargeFileMinimumBytes < baseline.LargeFileMinimumBytes ||
            current.SameSizeMinimumBytes < baseline.SameSizeMinimumBytes ||
            current.StaleAgeDays < baseline.StaleAgeDays)
        {
            current = baseline;
            _displayThresholds = current;
        }

        var largeOptions = BuildSizeOptions(
            baseline.LargeFileMinimumBytes,
            current.LargeFileMinimumBytes);
        var sameSizeOptions = BuildSizeOptions(
            baseline.SameSizeMinimumBytes,
            current.SameSizeMinimumBytes);
        var staleOptions = BuildAgeOptions(
            baseline.StaleAgeDays,
            current.StaleAgeDays);

        _thresholdControlsUpdating = true;
        try
        {
            _largeThresholdComboBox.ItemsSource = largeOptions;
            _largeThresholdComboBox.SelectedItem = largeOptions.First(option =>
                option.Value == current.LargeFileMinimumBytes);
            _sameSizeThresholdComboBox.ItemsSource = sameSizeOptions;
            _sameSizeThresholdComboBox.SelectedItem = sameSizeOptions.First(option =>
                option.Value == current.SameSizeMinimumBytes);
            _staleAgeComboBox.ItemsSource = staleOptions;
            _staleAgeComboBox.SelectedItem = staleOptions.First(option =>
                option.Value == current.StaleAgeDays);

            var enabled = !_sameSizeVerificationControlsBlocked &&
                !_sameSizeVerificationActiveIndex.HasValue;
            _largeThresholdComboBox.IsEnabled = enabled;
            _sameSizeThresholdComboBox.IsEnabled = enabled;
            _staleAgeComboBox.IsEnabled = enabled;
        }
        finally
        {
            _thresholdControlsUpdating = false;
        }
    }

    private static IReadOnlyList<SizeThresholdOption> BuildSizeOptions(
        long baseline,
        long current)
    {
        var values = new SortedSet<long>
        {
            baseline,
            Math.Max(baseline, current),
            SaturatingMultiply(baseline, 2),
            SaturatingMultiply(baseline, 4),
            SaturatingMultiply(baseline, 8),
        };
        return values
            .Select(static value => new SizeThresholdOption(value, ByteFormatter.Format(value)))
            .ToArray();
    }

    private static IReadOnlyList<AgeThresholdOption> BuildAgeOptions(
        int baseline,
        int current)
    {
        var values = new SortedSet<int>
        {
            baseline,
            Math.Max(baseline, current),
            SaturatingMultiply(baseline, 2),
            SaturatingMultiply(baseline, 4),
            SaturatingMultiply(baseline, 6),
        };
        return values
            .Select(static value => new AgeThresholdOption(value, $"{value:N0} days"))
            .ToArray();
    }

    private static long SaturatingMultiply(long value, int multiplier) =>
        value > long.MaxValue / multiplier
            ? long.MaxValue
            : value * multiplier;

    private static int SaturatingMultiply(int value, int multiplier) =>
        value > int.MaxValue / multiplier
            ? int.MaxValue
            : value * multiplier;
}
