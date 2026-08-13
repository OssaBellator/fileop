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
        StatusText.Text = "Reviewing large, old files in current-user known locations from current checkpointed native indexes…";
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
            .SelectMany(location => location.Candidates.Select(candidate => (Location: location, Candidate: candidate)))
            .OrderByDescending(static item => item.Candidate.MeasuredBytes)
            .ThenBy(static item => item.Candidate.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.Candidate.RuleId, StringComparer.Ordinal)
            .Select(item => StorageKnownLocationCandidateRow.FromCandidate(
                item.Candidate,
                item.Location.RootPath,
                snapshot.ActiveVolumeRootPath))
            .ToArray();
        LocationStatusList.ItemsSource = snapshot.Locations
            .Select(StorageKnownLocationStatusRow.FromLocation)
            .ToArray();
        CandidateList.ItemsSource = candidates;
        CleanupReadinessStatusText.Text =
            "Cleanup readiness has not been checked for this review. It is read-only current-path evidence; permanent deletion, if later chosen in Files, requires the separate Files preflight, recovery and explicit-authorization flow.";

        var available = snapshot.Locations.Count(static location =>
            location.Status == StorageReviewLocationStatus.Available);
        var capped = snapshot.Locations.Count(static location => location.SourceMayBeTruncated);
        var crossVolumeCandidates = candidates.Count(static candidate => !candidate.CanReviewInFiles);
        StatusText.Text =
            $"{available:N0} known location(s) reviewed · {candidates.Length:N0} candidate(s) · " +
            $"{ByteFormatter.Format(snapshot.CandidateMeasuredBytes)} candidate measured bytes. " +
            "This is review evidence, not guaranteed reclaimable space or deletion authorization." +
            (crossVolumeCandidates > 0
                ? $" {crossVolumeCandidates:N0} candidate(s) are on another indexed volume; readiness remains available, but Files handoff is disabled until Files supports that volume."
                : string.Empty) +
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
            StorageReviewLocationStatus.OutsideActiveVolume => "outside active volume / no indexed source",
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
    string ReviewRootPath,
    StorageReviewProvenance Provenance,
    string RuleId,
    string EvidenceText,
    string SizeText,
    string LastWriteText,
    bool CanReviewInFiles)
{
    public static StorageKnownLocationCandidateRow FromCandidate(
        StorageReviewCandidate candidate,
        string reviewRootPath,
        string activeVolumeRootPath)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeVolumeRootPath);

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
        var canReviewInFiles = IsPathWithinRoot(candidate.Path, activeVolumeRootPath);
        return new StorageKnownLocationCandidateRow(
            candidate.Name,
            candidate.Path,
            reviewRootPath,
            candidate.Provenance,
            candidate.RuleId,
            $"{provenance} · {reason} · rule {candidate.RuleId}. Review only; this rule does not establish safe deletion." +
            (canReviewInFiles
                ? string.Empty
                : " This candidate is outside the active Files indexed volume, so Review in Files is unavailable for this evidence."),
            candidate.AllocatedBytes is { } allocated
                ? $"{ByteFormatter.Format(allocated)} physical"
                : $"{ByteFormatter.Format(candidate.LogicalBytes)} logical",
            candidate.LastWriteTime.ToLocalTime().ToString("g"),
            canReviewInFiles);
    }

    private static bool IsPathWithinRoot(string path, string rootPath)
    {
        try
        {
            var fullPath = System.IO.Path.GetFullPath(path);
            var fullRoot = System.IO.Path.GetFullPath(rootPath);
            var comparablePath = fullPath.TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar);
            var comparableRoot = fullRoot.TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar);
            if (string.Equals(
                    comparablePath,
                    comparableRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return fullPath.StartsWith(
                comparableRoot + System.IO.Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
