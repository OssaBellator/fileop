using FileOp.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class StorageKnownLocationReviewView
{
    public void SetCleanupReadinessLoading(string candidateName)
    {
        CleanupReadinessStatusText.Text =
            $"Checking current canonical path, identity, allocation, hard-link count, size and last-write evidence for {candidateName}. This is read-only and cannot authorize deletion.";
    }

    public void ApplyCleanupReadiness(StorageCleanupReadinessPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var heading = preview.Status switch
        {
            StorageCleanupReadinessStatus.CurrentEvidenceConsistent => "Current evidence consistent",
            StorageCleanupReadinessStatus.CandidateChanged => "Candidate changed",
            StorageCleanupReadinessStatus.Blocked => "Cleanup path blocked",
            StorageCleanupReadinessStatus.Unavailable => "Readiness unavailable",
            _ => preview.Status.ToString(),
        };
        var physical = preview.Status == StorageCleanupReadinessStatus.CurrentEvidenceConsistent &&
            preview.CurrentAllocatedBytes is { } allocatedBytes &&
            preview.CurrentHardLinkCount is { } hardLinkCount
                ? hardLinkCount == 1
                    ? $" Current allocation: {ByteFormatter.Format(allocatedBytes)} · 1 hard link · per-path physical-release upper bound: {ByteFormatter.Format(preview.CurrentPhysicalReleaseUpperBoundBytes)}."
                    : $" Current allocation: {ByteFormatter.Format(allocatedBytes)} · {hardLinkCount:N0} hard links · per-path physical-release upper bound from deleting this one name: 0 B."
                : string.Empty;
        CleanupReadinessStatusText.Text =
            $"{heading} · {preview.Path} · checked {preview.CheckedAtUtc.ToLocalTime():g}.{physical} {preview.Detail}";
    }

    public void SetCleanupReadinessUnavailable(string message)
    {
        CleanupReadinessStatusText.Text = message;
    }

    private async void CheckCleanupReadinessButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: StorageKnownLocationCandidateRow row } ||
            Application.Current is not App app ||
            app.MainWindow is not { } window)
        {
            return;
        }

        await window.CheckKnownLocationCleanupReadinessAsync(
            row.Path,
            row.ReviewRootPath,
            row.Provenance,
            row.RuleId);
    }
}
