using System.Runtime.InteropServices;
using FileOp.Core.Indexing.Service;
using FileOp.Core.Storage;
using FileOp.Windows.IndexingService;
using FileOp.Windows.Storage;

namespace FileOp.App;

internal sealed partial class DesktopSearchEngine
{
    private readonly WindowsKnownFolderPathResolver _knownFolderPathResolver = new();

    public ulong? StorageVolumeIdentity =>
        State.Mode == DesktopSearchMode.Native
            ? _primaryVolume?.VolumeIdentity
            : null;

    public async ValueTask<StorageKnownLocationReviewSnapshot> AnalyzeKnownLocationReviewAsync()
    {
        ThrowIfDisposed();
        if (!StorageOptimizationAvailable || _primaryVolume is not { } capturedPrimary)
        {
            throw new InvalidOperationException(
                "Known-location review currently requires the native indexed NTFS volume.");
        }

        if (!TryNormalizeReviewRoot(capturedPrimary.RootPath, out var capturedRoot))
        {
            throw new InvalidOperationException(
                "The active native indexed volume reported an invalid filesystem root.");
        }

        var capturedVolumeIdentity = capturedPrimary.VolumeIdentity;
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
                capturedRoot,
                capturedVolumeIdentity).ConfigureAwait(false));
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
            PathTooLongException or
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
                capturedRoot,
                capturedVolumeIdentity).ConfigureAwait(false));
        }

        ThrowIfDisposed();
        if (!StorageOptimizationAvailable ||
            StorageRootPath is not { } currentRoot ||
            _primaryVolume is not { } currentPrimary ||
            currentPrimary.VolumeIdentity != capturedVolumeIdentity ||
            !ReviewRootsEqual(currentRoot, capturedRoot) ||
            !ReviewRootsEqual(currentPrimary.RootPath, capturedRoot))
        {
            throw new InvalidOperationException(
                "The native indexing source changed while known-location review evidence was being captured.");
        }

        if (!await IsNativeReviewSourceCurrentAsync(
                capturedVolumeIdentity,
                capturedRoot,
                locations).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "A native indexed-volume source changed before known-location review evidence could be published.");
        }

        return new StorageKnownLocationReviewSnapshot(
            DateTimeOffset.UtcNow,
            capturedRoot,
            locations.ToArray())
        {
            ActiveVolumeIdentity = capturedVolumeIdentity,
        };
    }

    private async ValueTask<StorageKnownLocationReview> AnalyzeKnownLocationAsync(
        StorageReviewProvenance provenance,
        string locationPath,
        string activeRoot,
        ulong activeVolumeIdentity)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(locationPath);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return StorageKnownLocationReviewClassifier.CreateUnavailable(
                provenance,
                StorageReviewLocationStatus.Unavailable,
                locationPath,
                $"{FormatProvenance(provenance)} resolved to an invalid filesystem path: {exception.Message}");
        }

        try
        {
            if (!IsReviewPathWithinRoot(fullPath, activeRoot))
            {
                return await AnalyzeCrossVolumeKnownLocationAsync(
                    provenance,
                    fullPath,
                    activeRoot).ConfigureAwait(false);
            }

            var analysis = await AnalyzeStorageOptimizationAsync(fullPath).ConfigureAwait(false);
            return StorageKnownLocationReviewClassifier.Classify(analysis, provenance) with
            {
                SourceVolumeIdentity = activeVolumeIdentity,
            };
        }
        catch (IndexingServiceRemoteException exception)
        {
            return StorageKnownLocationReviewClassifier.CreateUnavailable(
                provenance,
                exception.Error.Code == IndexingServiceErrorCode.VolumeNotFound
                    ? StorageReviewLocationStatus.OutsideActiveVolume
                    : StorageReviewLocationStatus.Unavailable,
                fullPath,
                $"{FormatProvenance(provenance)} could not be reviewed from the native index: {exception.Error.Message}");
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
