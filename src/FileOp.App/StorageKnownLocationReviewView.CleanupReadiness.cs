using FileOp.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class StorageKnownLocationReviewView
{
    public void SetCleanupReadinessLoading(string candidateName)
    {
        CleanupReadinessStatusText.Text =
            $"Checking current canonical path, identity snapshot, size and last-write evidence for {candidateName}. This is read-only and cannot authorize deletion.";
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
        CleanupReadinessStatusText.Text =
            $"{heading} · {preview.Path} · checked {preview.CheckedAtUtc.ToLocalTime():g}. {preview.Detail}";
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

        await window.CheckKnownLocationCleanupReadinessAsync(row.Path);
    }
}
