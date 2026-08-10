using FileOp.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class StorageOptimizationView : UserControl
{
    public StorageOptimizationView()
    {
        InitializeComponent();
    }

    public event EventHandler? RefreshRequested;

    public void SetLoading(string message)
    {
        StatusText.Text = message;
        RefreshButton.IsEnabled = false;
    }

    public void SetUnavailable(string message)
    {
        LargestFilesList.ItemsSource = null;
        StaleFilesList.ItemsSource = null;
        SameSizeGroupsList.ItemsSource = null;
        StatusText.Text = message;
        RefreshButton.IsEnabled = false;
    }

    public void SetReadyForRefresh(bool ready)
    {
        RefreshButton.IsEnabled = ready;
    }

    public void Apply(StorageOptimizationAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        LargestFilesList.ItemsSource = analysis.LargestFiles
            .Select(StorageOptimizationFileRow.FromCandidate)
            .ToArray();
        StaleFilesList.ItemsSource = analysis.StaleLargeFiles
            .Select(StorageOptimizationFileRow.FromCandidate)
            .ToArray();
        SameSizeGroupsList.ItemsSource = analysis.SameSizeCandidateGroups
            .Select(StorageSameSizeGroupRow.FromGroup)
            .ToArray();

        var policy = analysis.Policy;
        StatusText.Text =
            $"{analysis.LargestFiles.Count:N0} large file(s) · " +
            $"{analysis.StaleLargeFiles.Count:N0} older than {policy.StaleAgeDays:N0} days · " +
            $"{analysis.SameSizeCandidateGroups.Count:N0} same-size candidate group(s) · " +
            $"{ByteFormatter.Format(analysis.SameSizePotentialLogicalSavingsUpperBound)} maximum logical candidate savings";
        RefreshButton.IsEnabled = true;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        RefreshRequested?.Invoke(this, EventArgs.Empty);
}

public sealed record StorageOptimizationFileRow(
    string Name,
    string Path,
    string SizeText,
    string LastWriteText)
{
    public static StorageOptimizationFileRow FromCandidate(StorageOptimizationFileCandidate candidate) =>
        new(
            candidate.Name,
            candidate.Path,
            candidate.AllocatedBytes is { } allocated
                ? $"{ByteFormatter.Format(allocated)} physical"
                : $"{ByteFormatter.Format(candidate.LogicalBytes)} logical",
            candidate.LastWriteTime.ToLocalTime().ToString("g"));
}

public sealed record StorageSameSizeGroupRow(
    string Heading,
    string SampleText,
    string PotentialText)
{
    public static StorageSameSizeGroupRow FromGroup(StorageSameSizeCandidateGroup group)
    {
        var samples = string.Join(
            " · ",
            group.SampleFiles.Select(static file => file.Path));
        var suffix = group.SampleFiles.Count < group.CandidateFileCount
            ? $" · +{group.CandidateFileCount - group.SampleFiles.Count:N0} more"
            : string.Empty;
        return new StorageSameSizeGroupRow(
            $"{group.CandidateFileCount:N0} physical candidates · {ByteFormatter.Format(group.LogicalBytesPerFile)} each",
            samples + suffix,
            ByteFormatter.Format(group.PotentialLogicalSavingsUpperBound));
    }
}
