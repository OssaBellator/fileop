using System.IO;
using System.Threading;
using FileOp.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class StorageOptimizationView
{
    private sealed record SizeThresholdOption(long Value, string Label);
    private sealed record AgeThresholdOption(int Value, string Label);

    private ComboBox? _largeThresholdComboBox;
    private ComboBox? _sameSizeThresholdComboBox;
    private ComboBox? _staleAgeComboBox;
    private TextBlock? _thresholdStatusText;
    private StorageOptimizationDisplayThresholds? _displayThresholds;
    private StorageOptimizationThresholdPreference _thresholdPreference =
        StorageOptimizationThresholdPreference.Baseline;
    private readonly IStorageOptimizationThresholdPreferenceStore? _thresholdPreferenceStore =
        CreateThresholdPreferenceStore();
    private readonly SemaphoreSlim _thresholdPreferenceSaveGate = new(1, 1);
    private int _thresholdPreferenceGeneration;
    private bool _thresholdPanelInitialized;
    private bool _thresholdControlsUpdating;
    private bool _thresholdAnalysisLoading;

    public void ApplyThresholdPreference(StorageOptimizationThresholdPreference? preference)
    {
        _thresholdPreference = preference is not null &&
            StorageOptimizationThresholdPreferencePolicy.IsSupported(preference)
            ? preference
            : StorageOptimizationThresholdPreference.Baseline;

        if (_analysis is { } analysis)
        {
            _displayThresholds = StorageOptimizationThresholdPreferencePolicy.Resolve(
                analysis,
                _thresholdPreference);
            ApplyThresholdOverlay();
        }
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

        var optionGrid = new Grid { ColumnSpacing = 12 };
        optionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        optionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        optionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddThresholdColumn(optionGrid, 0, "Large file minimum", _largeThresholdComboBox);
        AddThresholdColumn(optionGrid, 1, "Same-size minimum", _sameSizeThresholdComboBox);
        AddThresholdColumn(optionGrid, 2, "Stale age minimum", _staleAgeComboBox);

        _thresholdStatusText = new TextBlock
        {
            Text = "Threshold controls become available after Optimize analysis loads.",
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "View thresholds",
            FontSize = 16,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Stricter view filters are remembered as policy-relative multipliers. FileOp re-derives them from each fresh native analysis and never lets them request evidence below the helper baseline.",
            Opacity = 0.68,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(optionGrid);
        panel.Children.Add(_thresholdStatusText);

        var insertionIndex = Math.Min(2, contentStack.Children.Count);
        contentStack.Children.Insert(insertionIndex, panel);
        _thresholdPanelInitialized = true;
        UpdateThresholdControlEnabledState();
        _ = LoadThresholdPreferenceAsync();
    }

    private static ComboBox CreateThresholdComboBox() =>
        new()
        {
            MinWidth = 180,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            DisplayMemberPath = "Label",
        };

    private static void AddThresholdColumn(
        Grid grid,
        int column,
        string label,
        ComboBox comboBox)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Opacity = 0.7,
        });
        stack.Children.Add(comboBox);
        Grid.SetColumn(stack, column);
        grid.Children.Add(stack);
    }

    private void SetThresholdAnalysisLoading(bool loading)
    {
        _thresholdAnalysisLoading = loading;
        if (!loading && _analysis is { } analysis)
        {
            _displayThresholds = StorageOptimizationThresholdPreferencePolicy.Resolve(
                analysis,
                _thresholdPreference);
        }

        UpdateThresholdControlEnabledState();
    }

    private void ThresholdComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_thresholdControlsUpdating ||
            _thresholdAnalysisLoading ||
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
        _thresholdPreference = StorageOptimizationThresholdPreferencePolicy.FromThresholds(
            _analysis,
            thresholds);
        var generation = Interlocked.Increment(ref _thresholdPreferenceGeneration);
        ApplyThresholdOverlay();
        _ = SaveThresholdPreferenceAsync(_thresholdPreference, generation);
    }

    private void ApplyThresholdOverlay()
    {
        if (_analysis is not { } analysis ||
            !_thresholdPanelInitialized ||
            _thresholdAnalysisLoading)
        {
            return;
        }

        EnsureThresholdControls(analysis);
        var thresholds = _displayThresholds ??
            StorageOptimizationThresholdPreferencePolicy.Resolve(analysis, _thresholdPreference);
        var filtered = StorageOptimizationThresholdFilter.Apply(analysis, thresholds);

        LargestFilesList.ItemsSource = filtered.LargestFiles
            .Select(StorageOptimizationFileRow.FromCandidate)
            .ToArray();
        StaleFilesList.ItemsSource = filtered.StaleLargeFiles
            .Select(StorageOptimizationFileRow.FromCandidate)
            .ToArray();
        ApplyThresholdSameSizeRows(analysis, thresholds);

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
                "The remembered preference stores only supported multipliers. These controls only narrow the already bounded result; they do not rerun the helper or imply evidence below its baseline.";
        }
    }

    private bool TryApplyThresholdSameSizeRows()
    {
        if (_analysis is not { } analysis || !_thresholdPanelInitialized)
        {
            return false;
        }

        var thresholds = _displayThresholds ??
            StorageOptimizationThresholdPreferencePolicy.Resolve(analysis, _thresholdPreference);
        StorageOptimizationThresholdFilter.ValidateAgainstAnalysis(analysis, thresholds);
        ApplyThresholdSameSizeRows(analysis, thresholds);
        UpdateThresholdControlEnabledState();
        return true;
    }

    private void ApplyThresholdSameSizeRows(
        StorageOptimizationAnalysis analysis,
        StorageOptimizationDisplayThresholds thresholds)
    {
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

    private void EnsureThresholdControls(StorageOptimizationAnalysis analysis)
    {
        if (_largeThresholdComboBox is null ||
            _sameSizeThresholdComboBox is null ||
            _staleAgeComboBox is null)
        {
            return;
        }

        var baseline = StorageOptimizationDisplayThresholds.FromAnalysis(analysis);
        var current = _displayThresholds ??
            StorageOptimizationThresholdPreferencePolicy.Resolve(analysis, _thresholdPreference);
        StorageOptimizationThresholdFilter.ValidateAgainstAnalysis(analysis, current);
        _displayThresholds = current;

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
        }
        finally
        {
            _thresholdControlsUpdating = false;
        }

        UpdateThresholdControlEnabledState();
    }

    private void UpdateThresholdControlEnabledState()
    {
        var enabled =
            !_thresholdAnalysisLoading &&
            _analysis is not null &&
            !_sameSizeVerificationControlsBlocked &&
            !_sameSizeVerificationActiveIndex.HasValue;
        if (_largeThresholdComboBox is not null)
        {
            _largeThresholdComboBox.IsEnabled = enabled;
        }
        if (_sameSizeThresholdComboBox is not null)
        {
            _sameSizeThresholdComboBox.IsEnabled = enabled;
        }
        if (_staleAgeComboBox is not null)
        {
            _staleAgeComboBox.IsEnabled = enabled;
        }
    }

    private async Task LoadThresholdPreferenceAsync()
    {
        var store = _thresholdPreferenceStore;
        if (store is null)
        {
            return;
        }

        var generation = Volatile.Read(ref _thresholdPreferenceGeneration);
        var preference = await store.LoadAsync();
        if (generation != Volatile.Read(ref _thresholdPreferenceGeneration))
        {
            return;
        }

        ApplyThresholdPreference(preference);
    }

    private async Task SaveThresholdPreferenceAsync(
        StorageOptimizationThresholdPreference preference,
        int generation)
    {
        var store = _thresholdPreferenceStore;
        if (store is null)
        {
            return;
        }

        await _thresholdPreferenceSaveGate.WaitAsync();
        try
        {
            if (generation != Volatile.Read(ref _thresholdPreferenceGeneration))
            {
                return;
            }

            try
            {
                await store.SaveAsync(preference);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        finally
        {
            _thresholdPreferenceSaveGate.Release();
        }
    }

    private static IStorageOptimizationThresholdPreferenceStore? CreateThresholdPreferenceStore()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return null;
        }

        return new JsonFileStorageOptimizationThresholdPreferenceStore(
            Path.Combine(
                localAppData,
                "FileOp",
                "preferences",
                "storage-optimization-thresholds.v1.json"));
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
