using System.Runtime.InteropServices;
using FileOp.Core.Indexing.Service;
using FileOp.Core.Storage;
using FileOp.Windows.IndexingService;
using FileOp.Windows.Storage;

namespace FileOp.App;

internal sealed partial class DesktopSearchEngine
{
    private readonly WindowsKnownFolderPathResolver _knownFolderPathResolver = new();

    public async ValueTask<StorageKnownLocationReviewSnapshot> AnalyzeKnownLocationReviewAsync()
    {
        ThrowIfDisposed();
        if (!StorageOptimizationAvailable || StorageRootPath is not { } activeRoot)
        {
            throw new InvalidOperationException(
                "Known-location review currently requires the native indexed NTFS volume.");
        }

        var locations = new List<StorageKnownLocationReview>(2);
        string downloadsPath;
        try
        {
            downloadsPath = _knownFolderPathResolver.GetDownloadsPath();
        }
        catch (Exception exception) when (
            exception is COMException or
            InvalidDataException or
            IOException or
            UnauthorizedAccessException or
            PlatformNotSupportedException)
        {
            downloadsPath = string.Empty;
            locations.Add(StorageKnownLocationReviewClassifier.CreateUnavailable(
                StorageReviewProvenance.Downloads,
                StorageReviewLocationStatus.Unavailable,
                string.Empty,
                $"The current-user Downloads known folder could not be resolved: {exception.Message}"));
        }

        if (!string.IsNullOrWhiteSpace(downloadsPath))
        {
            locations.Add(await AnalyzeKnownLocationAsync(
                StorageReviewProvenance.Downloads,
                downloadsPath,
                activeRoot).ConfigureAwait(false));
        }

        string tempPath;
        try
        {
            tempPath = Path.GetFullPath(Path.GetTempPath());
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            IOException or
            NotSupportedException or
            UnauthorizedAccessException)
        {
            tempPath = string.Empty;
            locations.Add(StorageKnownLocationReviewClassifier.CreateUnavailable(
                StorageReviewProvenance.UserTemp,
                StorageReviewLocationStatus.Unavailable,
                string.Empty,
                $"The current-user Temp path could not be resolved: {exception.Message}"));
        }

        if (!string.IsNullOrWhiteSpace(tempPath))
        {
            locations.Add(await AnalyzeKnownLocationAsync(
                StorageReviewProvenance.UserTemp,
                tempPath,
                activeRoot).ConfigureAwait(false));
        }

        return new StorageKnownLocationReviewSnapshot(
            DateTimeOffset.UtcNow,
            Path.GetFullPath(activeRoot),
            locations.ToArray());
    }

    private async ValueTask<StorageKnownLocationReview> AnalyzeKnownLocationAsync(
        StorageReviewProvenance provenance,
        string locationPath,
        string activeRoot)
    {
        var fullPath = Path.GetFullPath(locationPath);
        if (!IsReviewPathWithinRoot(fullPath, activeRoot))
        {
            return StorageKnownLocationReviewClassifier.CreateUnavailable(
                provenance,
                StorageReviewLocationStatus.OutsideActiveVolume,
                fullPath,
                $"{FormatProvenance(provenance)} is outside the currently active indexed volume {Path.GetFullPath(activeRoot)}. " +
                "This first review slice does not aggregate candidates across volumes.");
        }

        try
        {
            var analysis = await AnalyzeStorageOptimizationAsync(fullPath).ConfigureAwait(false);
            return StorageKnownLocationReviewClassifier.Classify(analysis, provenance);
        }
        catch (IndexingServiceRemoteException exception)
            when (exception.Error.Code is
                IndexingServiceErrorCode.InvalidRequest or
                IndexingServiceErrorCode.SnapshotRequired or
                IndexingServiceErrorCode.Busy)
        {
            return StorageKnownLocationReviewClassifier.CreateUnavailable(
                provenance,
                StorageReviewLocationStatus.Unavailable,
                fullPath,
                $"{FormatProvenance(provenance)} could not be reviewed from the current native index: {exception.Error.Message}");
        }
        catch (InvalidOperationException exception)
        {
            return StorageKnownLocationReviewClassifier.CreateUnavailable(
                provenance,
                StorageReviewLocationStatus.Unavailable,
                fullPath,
                $"{FormatProvenance(provenance)} review lost the active native source: {exception.Message}");
        }
    }

    private static bool IsReviewPathWithinRoot(string path, string rootPath)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(rootPath);
        var comparablePath = fullPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var comparableRoot = fullRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (string.Equals(comparablePath, comparableRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootedPrefix = comparableRoot + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootedPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatProvenance(StorageReviewProvenance provenance) =>
        provenance switch
        {
            StorageReviewProvenance.Downloads => "Downloads",
            StorageReviewProvenance.UserTemp => "User Temp",
            _ => provenance.ToString(),
        };
}
