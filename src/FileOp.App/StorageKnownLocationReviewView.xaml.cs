using FileOp.Core.Storage;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class StorageKnownLocationReviewView : UserControl
{
    public StorageKnownLocationReviewView()
    {
        InitializeComponent();
    }

    public void SetLoading()
    {
        StatusText.Text = "Reviewing large, old files in current-user known locations from the native index…";
        CleanupReadinessStatusText.Text =
            "Cleanup readiness is waiting for the refreshed known-location review. No previous readiness result is retained as current evidence.";
    }

    public void SetUnavailable(string message)
    {
        LocationStatusList.ItemsSource = null;
        CandidateList.ItemsSource = null;
        StatusText.Text = message;
        CleanupReadinessStatusText.Text =
            "Cleanup readiness is unavailable because the known-location review is unavailable.";
    }

    public void Apply(StorageKnownLocationReviewSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var candidates = snapshot.Locations
            .SelectMany(static location => location.Candidates)
            .OrderByDescending(static candidate => candidate.MeasuredBytes)
            .ThenBy(static candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Select(StorageKnownLocationCandidateRow.FromCandidate)
            .ToArray();
        LocationStatusList.ItemsSource = snapshot.Locations
            .Select(StorageKnownLocationStatusRow.FromLocation)
            .ToArray();
        CandidateList.ItemsSource = candidates;
        CleanupReadinessStatusText.Text =
            "Cleanup readiness has not been checked for this review. Delete recovery/history and mutation authorization are not implemented.";

        var available = snapshot.Locations.Count(static location =>
            location.Status == StorageReviewLocationStatus.Available);
        var capped = snapshot.Locations.Count(static location => location.SourceMayBeTruncated);
        StatusText.Text =
            $"{available:N0} known location(s) reviewed · {candidates.Length:N0} candidate(s) · " +
            $"{ByteFormatter.Format(snapshot.CandidateMeasuredBytes)} candidate measured bytes. " +
            "This is review evidence, not guaranteed reclaimable space or deletion authorization." +
            (capped > 0
                ? $" {capped:N0} location(s) reached the upstream stale-candidate cap, so additional matching files may exist."
                : string.Empty);
    }
}

public sealed record StorageKnownLocationStatusRow(
    string Heading,
    string Detail)
{
    public static StorageKnownLocationStatusRow FromLocation(StorageKnownLocationReview location)
    {
        var provenance = location.Provenance switch
        {
            StorageReviewProvenance.Downloads => "Downloads",
            StorageReviewProvenance.UserTemp => "User Temp",
            _ => location.Provenance.ToString(),
        };
        var status = location.Status switch
        {
            StorageReviewLocationStatus.Available => "reviewed",
            StorageReviewLocationStatus.OutsideActiveVolume => "outside active volume",
            StorageReviewLocationStatus.Unavailable => "unavailable",
            _ => location.Status.ToString(),
        };
        var root = string.IsNullOrWhiteSpace(location.RootPath)
            ? string.Empty
            : $" · {location.RootPath}";
        return new StorageKnownLocationStatusRow(
            $"{provenance} · {status}{root}",
            location.Detail +
            (location.SourceMayBeTruncated
                ? " The native stale-large source reached its configured result cap; this review is not exhaustive."
                : string.Empty));
    }
}

public sealed record StorageKnownLocationCandidateRow(
    string Name,
    string Path,
    string EvidenceText,
    string SizeText,
    string LastWriteText)
{
    public static StorageKnownLocationCandidateRow FromCandidate(StorageReviewCandidate candidate)
    {
        var provenance = candidate.Provenance switch
        {
            StorageReviewProvenance.Downloads => "Downloads",
            StorageReviewProvenance.UserTemp => "User Temp",
            _ => candidate.Provenance.ToString(),
        };
        var reason = candidate.Reason switch
        {
            StorageReviewReason.OldInstallerPackage => "old installer/package extension",
            StorageReviewReason.OldArchive => "old archive extension",
            StorageReviewReason.OldDiskImage => "old disk-image extension",
            StorageReviewReason.OldArchiveOrDiskImage => "old archive/disk-image extension",
            StorageReviewReason.OldUserTempFile => "old file under current-user Temp",
            _ => candidate.Reason.ToString(),
        };
        return new StorageKnownLocationCandidateRow(
            candidate.Name,
            candidate.Path,
            $"{provenance} · {reason} · rule {candidate.RuleId}. Review only; this rule does not establish safe deletion.",
            candidate.AllocatedBytes is { } allocated
                ? $"{ByteFormatter.Format(allocated)} physical"
                : $"{ByteFormatter.Format(candidate.LogicalBytes)} logical",
            candidate.LastWriteTime.ToLocalTime().ToString("g"));
    }
}
