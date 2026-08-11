using FileOp.Core.Performance;
using FileOp.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class StorageOptimizationView : UserControl
{
    private const string FileOpResourceRefreshUnavailableMessage =
        "FileOp process counters have not been refreshed because the foreground Performance diagnostics path is currently unavailable; this is not evidence of a process-counter failure.";

    private readonly Dictionary<int, StorageSameSizeContentVerification> _sameSizeVerificationResults = [];
    private readonly Dictionary<int, string> _sameSizeVerificationMessages = [];
    private StorageOptimizationAnalysis? _analysis;
    private int? _sameSizeVerificationActiveIndex;
    private bool _sameSizeVerificationControlsBlocked;

    public StorageOptimizationView()
    {
        InitializeComponent();
        EnsureThresholdPanel();
        PerformanceDiagnostics.RefreshRequested += PerformanceDiagnostics_RefreshRequested;
        PerformanceDiagnostics.DiskIoCaptureRequested += PerformanceDiagnostics_DiskIoCaptureRequested;
    }

    public event EventHandler? RefreshRequested;
    public event EventHandler? PerformanceRefreshRequested;
    public event EventHandler? PerformanceDiskIoCaptureRequested;
    public event EventHandler<StorageSameSizeVerificationRequestedEventArgs>? SameSizeVerificationRequested;

    public void SetLoading(string message)
    {
        SetThresholdAnalysisLoading(true);
        StatusText.Text = message;
        RefreshButton.IsEnabled = false;
        PerformanceDiagnostics.SetLoading();
        FileOpResourceFootprint.SetLoading();
        _sameSizeVerificationControlsBlocked = true;
        RefreshSameSizeRows();
    }

    public void SetPerformanceLoading()
    {
        PerformanceDiagnostics.SetLoading();
        FileOpResourceFootprint.SetLoading();
        _sameSizeVerificationControlsBlocked = true;
        RefreshSameSizeRows();
    }

    public void SetDiskIoLoading()
    {
        PerformanceDiagnostics.SetDiskIoLoading();
        _sameSizeVerificationControlsBlocked = true;
        RefreshSameSizeRows();
    }

    public void SetKnownLocationReviewLoading() =>
        KnownLocationReview.SetLoading();

    public void SetKnownLocationReviewUnavailable(string message) =>
        KnownLocationReview.SetUnavailable(message);

    public void ApplyKnownLocationReview(StorageKnownLocationReviewSnapshot snapshot) =>
        KnownLocationReview.Apply(snapshot);

    public void SetUnavailable(string message)
    {
        SetThresholdAnalysisLoading(true);
        LargestFilesList.ItemsSource = null;
        StaleFilesList.ItemsSource = null;
        SameSizeGroupsList.ItemsSource = null;
        _analysis = null;
        _sameSizeVerificationResults.Clear();
        _sameSizeVerificationMessages.Clear();
        _sameSizeVerificationActiveIndex = null;
        _sameSizeVerificationControlsBlocked = false;
        SetThresholdAnalysisLoading(false);
        StatusText.Text = message;
        RefreshButton.IsEnabled = false;
        PerformanceDiagnostics.SetUnavailable(message);
        FileOpResourceFootprint.Apply(null, FileOpResourceRefreshUnavailableMessage);
        KnownLocationReview.SetUnavailable(
            "Known-location review is unavailable until a native indexed NTFS volume is active and ready.");
    }

    public void SetReadyForRefresh(bool ready)
    {
        RefreshButton.IsEnabled = ready;
        PerformanceDiagnostics.SetReadyForRefresh(ready);
    }

    public void SetPerformanceUnavailable(string message)
    {
        PerformanceDiagnostics.SetUnavailable(message);
        FileOpResourceFootprint.Apply(null, FileOpResourceRefreshUnavailableMessage);
        _sameSizeVerificationControlsBlocked = false;
        RefreshSameSizeRows();
    }

    public void SetDiskIoUnavailable(string message)
    {
        PerformanceDiagnostics.SetDiskIoUnavailable(message);
        _sameSizeVerificationControlsBlocked = false;
        RefreshSameSizeRows();
    }

    public void SetDiskIoReadyForCapture(bool ready) =>
        PerformanceDiagnostics.SetDiskIoReadyForCapture(ready);

    public void ResetDiskIoCapture() =>
        PerformanceDiagnostics.ResetDiskIoCapture();

    public void Apply(StorageOptimizationAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        if (!ReferenceEquals(_analysis, analysis))
        {
            _sameSizeVerificationResults.Clear();
            _sameSizeVerificationMessages.Clear();
            _sameSizeVerificationActiveIndex = null;
        }
        _analysis = analysis;
        SetThresholdAnalysisLoading(false);

        LargestFilesList.ItemsSource = analysis.LargestFiles
            .Select(StorageOptimizationFileRow.FromCandidate)
            .ToArray();
        StaleFilesList.ItemsSource = analysis.StaleLargeFiles
            .Select(StorageOptimizationFileRow.FromCandidate)
            .ToArray();
        RefreshSameSizeRows();

        var policy = analysis.Policy;
        StatusText.Text =
            $"{analysis.LargestFiles.Count:N0} large file(s) · " +
            $"{analysis.StaleLargeFiles.Count:N0} older than {policy.StaleAgeDays:N0} days · " +
            $"{analysis.SameSizeCandidateGroups.Count:N0} same-size candidate group(s) · " +
            $"{ByteFormatter.Format(analysis.SameSizePotentialLogicalSavingsUpperBound)} maximum logical candidate savings";
        ApplyThresholdOverlay();
        RefreshButton.IsEnabled = true;
    }

    public void ApplyPerformanceDiagnostics(PerformanceDiagnosticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        PerformanceDiagnostics.Apply(snapshot);
        FileOpResourceFootprint.Apply(snapshot.FileOpResources, snapshot.FileOpResourcesStatus);
        _sameSizeVerificationControlsBlocked = false;
        RefreshSameSizeRows();
    }

    public void ApplyDiskIoCapture(DiskIoCaptureResult result)
    {
        PerformanceDiagnostics.ApplyDiskIoCapture(result);
        _sameSizeVerificationControlsBlocked = false;
        RefreshSameSizeRows();
    }

    public void SetSameSizeVerificationLoading(int groupIndex)
    {
        if (!IsValidGroupIndex(groupIndex))
        {
            return;
        }

        _sameSizeVerificationActiveIndex = groupIndex;
        _sameSizeVerificationMessages.Remove(groupIndex);
        RefreshSameSizeRows();
    }

    public void ApplySameSizeVerification(
        int groupIndex,
        StorageSameSizeContentVerification verification)
    {
        ArgumentNullException.ThrowIfNull(verification);
        if (!IsValidGroupIndex(groupIndex))
        {
            return;
        }

        var group = _analysis!.SameSizeCandidateGroups[groupIndex];
        if (verification.LogicalBytesPerFile != group.LogicalBytesPerFile)
        {
            return;
        }

        _sameSizeVerificationResults[groupIndex] = verification;
        _sameSizeVerificationMessages.Remove(groupIndex);
        _sameSizeVerificationActiveIndex = null;
        RefreshSameSizeRows();
    }

    public void SetSameSizeVerificationFailure(int groupIndex, string message)
    {
        if (!IsValidGroupIndex(groupIndex))
        {
            return;
        }

        _sameSizeVerificationMessages[groupIndex] = message;
        _sameSizeVerificationActiveIndex = null;
        RefreshSameSizeRows();
    }

    private void RefreshSameSizeRows()
    {
        if (_analysis is null)
        {
            return;
        }
        if (TryApplyThresholdSameSizeRows())
        {
            return;
        }

        var busy = _sameSizeVerificationControlsBlocked || _sameSizeVerificationActiveIndex.HasValue;
        SameSizeGroupsList.ItemsSource = _analysis.SameSizeCandidateGroups
            .Select((group, index) =>
            {
                _sameSizeVerificationResults.TryGetValue(index, out var verification);
                _sameSizeVerificationMessages.TryGetValue(index, out var message);
                return StorageSameSizeGroupRow.FromGroup(
                    group,
                    index,
                    verification,
                    message,
                    _sameSizeVerificationActiveIndex == index,
                    busy);
            })
            .ToArray();
    }

    private bool IsValidGroupIndex(int groupIndex) =>
        _analysis is not null &&
        groupIndex >= 0 &&
        groupIndex < _analysis.SameSizeCandidateGroups.Count;

    private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void VerifySameSizeContentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: StorageSameSizeGroupRow row } && row.CanVerify)
        {
            SameSizeVerificationRequested?.Invoke(
                this,
                new StorageSameSizeVerificationRequestedEventArgs(row.GroupIndex));
        }
    }

    private void PerformanceDiagnostics_RefreshRequested(object? sender, EventArgs e) =>
        PerformanceRefreshRequested?.Invoke(this, EventArgs.Empty);

    private void PerformanceDiagnostics_DiskIoCaptureRequested(object? sender, EventArgs e) =>
        PerformanceDiskIoCaptureRequested?.Invoke(this, EventArgs.Empty);
}

public sealed class StorageSameSizeVerificationRequestedEventArgs : EventArgs
{
    public StorageSameSizeVerificationRequestedEventArgs(int groupIndex)
    {
        GroupIndex = groupIndex;
    }

    public int GroupIndex { get; }
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
    int GroupIndex,
    string Heading,
    string SampleText,
    string PotentialText,
    string VerificationText,
    bool CanVerify)
{
    public static StorageSameSizeGroupRow FromGroup(
        StorageSameSizeCandidateGroup group,
        int groupIndex,
        StorageSameSizeContentVerification? verification,
        string? message,
        bool isActive,
        bool busy)
    {
        var samples = string.Join(
            " · ",
            group.SampleFiles.Select(static file => file.Path));
        var suffix = group.SampleFiles.Count < group.CandidateFileCount
            ? $" · +{group.CandidateFileCount - group.SampleFiles.Count:N0} more candidate(s) not listed"
            : string.Empty;

        var verificationText = isActive
            ? $"Verifying with read-only handles that share read access only. FileOp will fully SHA-256 hash only whole sampled files that fit within the {ByteFormatter.Format(StorageSameSizeContentVerificationPolicy.Default.MaxTotalBytesRead)} content-byte budget, then revalidate current physical identity, hard-link count, and allocated disk bytes for any hash matches."
            : !string.IsNullOrWhiteSpace(message)
                ? message
                : verification is null
                    ? $"Not content-verified. Explicit verification processes at most {ByteFormatter.Format(StorageSameSizeContentVerificationPolicy.Default.MaxTotalBytesRead)} of file content and never uses the elevated indexer to read file contents. Physical reclaim evidence is collected only for fully hashed matches."
                    : FormatVerification(verification);

        return new StorageSameSizeGroupRow(
            groupIndex,
            $"{group.CandidateFileCount:N0} physical candidates · {ByteFormatter.Format(group.LogicalBytesPerFile)} each",
            samples + suffix,
            ByteFormatter.Format(group.PotentialLogicalSavingsUpperBound),
            verificationText,
            CanVerify: !busy && group.SampleFiles.Count >= 2);
    }

    private static string FormatVerification(StorageSameSizeContentVerification verification)
    {
        if (verification.Status != StorageSameSizeContentVerificationStatus.Completed)
        {
            return verification.Detail;
        }

        var scope =
            $"Fully hashed {verification.FullyHashedFileCount:N0} of {verification.SampleFileCount:N0} listed sample(s) " +
            $"({verification.SelectedFileCount:N0} selected) and processed {ByteFormatter.Format(verification.BytesRead)} of file content.";
        if (!verification.HasVerifiedDuplicateEvidence)
        {
            return scope +
                " No SHA-256 content matches were found within the fully hashed sample. The larger same-size candidate group remains unverified.";
        }

        var matches = string.Join(
            " | ",
            verification.MatchingSets.Select(static set => string.Join(" = ", set.Paths)));
        var contentEvidence =
            $" SHA-256 matched {ByteFormatter.Format(verification.VerifiedLogicalDuplicateBytes)} of logical duplicate content within sampled paths: {matches}.";
        var physicalEvidence = verification.PhysicalReclaim switch
        {
            { Status: StoragePhysicalReclaimEvidenceStatus.Verified } physical =>
                $" Current-handle physical evidence verifies a maximum {ByteFormatter.Format(verification.VerifiedPhysicalReclaimableBytesUpperBound)} reclaim upper bound across those sampled matches " +
                $"({physical.MatchingSets.Sum(static set => set.UniquePhysicalFileCount):N0} unique physical file(s), " +
                $"{physical.MatchingSets.Sum(static set => set.SingletonLinkPhysicalFileCount):N0} singleton-link physical file(s)). " +
                "The bound assumes one content-equivalent physical file remains per match set and only singleton-link files are later authorized for deletion; it is not deletion authorization and becomes stale if the files change.",
            { Status: StoragePhysicalReclaimEvidenceStatus.Unavailable } physical =>
                $" Physical reclaim remains unverified: {physical.Detail}",
            _ =>
                " Physical reclaim evidence was not applicable for this result.",
        };

        return scope + contentEvidence + physicalEvidence;
    }
}
