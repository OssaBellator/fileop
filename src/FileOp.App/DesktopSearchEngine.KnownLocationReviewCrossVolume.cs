using FileOp.Core.Indexing.Service;
using FileOp.Core.Storage;

namespace FileOp.App;

internal sealed partial class DesktopSearchEngine
{
    private async ValueTask<StorageKnownLocationReview> AnalyzeCrossVolumeKnownLocationAsync(
        StorageReviewProvenance provenance,
        string fullPath,
        string activeRoot)
    {
        var locationRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(locationRoot))
        {
            return StorageKnownLocationReviewClassifier.CreateUnavailable(
                provenance,
                StorageReviewLocationStatus.Unavailable,
                fullPath,
                $"{FormatProvenance(provenance)} has no resolvable filesystem root for indexed cross-volume review.");
        }

        await _searchOperationGate.WaitAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_nativeSession is not { Client.IsConnected: true } || _primaryVolume is null)
            {
                return StorageKnownLocationReviewClassifier.CreateUnavailable(
                    provenance,
                    StorageReviewLocationStatus.Unavailable,
                    fullPath,
                    $"{FormatProvenance(provenance)} cannot use cross-volume review because the native indexing session is no longer available.");
            }

            await _nativeOperationGate.WaitAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
            try
            {
                if (_nativeSession is not { Client.IsConnected: true } session ||
                    _primaryVolume is not { } primaryVolume ||
                    !ReviewRootsEqual(primaryVolume.RootPath, activeRoot))
                {
                    return StorageKnownLocationReviewClassifier.CreateUnavailable(
                        provenance,
                        StorageReviewLocationStatus.Unavailable,
                        fullPath,
                        $"{FormatProvenance(provenance)} review lost the active native source before secondary-volume evidence could be captured.");
                }

                var volumesBefore = await session.Client
                    .GetVolumesAsync(_lifetimeCancellation.Token)
                    .ConfigureAwait(false);
                var primaryBefore = FindUniqueIndexedVolumeByRoot(
                    volumesBefore.Volumes,
                    activeRoot,
                    out var primaryBeforeAmbiguous,
                    out var primaryBeforeInvalidCatalog);
                if (primaryBeforeInvalidCatalog)
                {
                    return StorageKnownLocationReviewClassifier.CreateUnavailable(
                        provenance,
                        StorageReviewLocationStatus.Unavailable,
                        fullPath,
                        $"{FormatProvenance(provenance)} cannot bind the active primary source because the native volume catalog contains a malformed root descriptor.");
                }

                if (primaryBeforeAmbiguous)
                {
                    return StorageKnownLocationReviewClassifier.CreateUnavailable(
                        provenance,
                        StorageReviewLocationStatus.Unavailable,
                        fullPath,
                        $"{FormatProvenance(provenance)} cannot bind the active primary source because the native volume catalog contains multiple descriptors for {activeRoot}.");
                }

                if (primaryBefore is null ||
                    primaryBefore.VolumeIdentity != primaryVolume.VolumeIdentity ||
                    !ReviewRootsEqual(primaryBefore.RootPath, primaryVolume.RootPath))
                {
                    return StorageKnownLocationReviewClassifier.CreateUnavailable(
                        provenance,
                        StorageReviewLocationStatus.Unavailable,
                        fullPath,
                        $"{FormatProvenance(provenance)} active primary indexed-volume identity or root no longer matches the selected native source, so no cross-volume evidence was captured.");
                }

                var target = FindUniqueIndexedVolumeByRoot(
                    volumesBefore.Volumes,
                    locationRoot,
                    out var targetAmbiguous,
                    out var targetInvalidCatalog);
                if (targetInvalidCatalog)
                {
                    return StorageKnownLocationReviewClassifier.CreateUnavailable(
                        provenance,
                        StorageReviewLocationStatus.Unavailable,
                        fullPath,
                        $"{FormatProvenance(provenance)} cannot bind {locationRoot} because the native volume catalog contains a malformed root descriptor.");
                }

                if (targetAmbiguous)
                {
                    return StorageKnownLocationReviewClassifier.CreateUnavailable(
                        provenance,
                        StorageReviewLocationStatus.Unavailable,
                        fullPath,
                        $"{FormatProvenance(provenance)} cannot bind {locationRoot} to one indexed source because the native volume catalog contains multiple matching descriptors.");
                }

                if (target is null)
                {
                    return StorageKnownLocationReviewClassifier.CreateUnavailable(
                        provenance,
                        StorageReviewLocationStatus.OutsideActiveVolume,
                        fullPath,
                        $"{FormatProvenance(provenance)} is outside the active indexed volume {activeRoot}, and no matching native indexed volume is available for {locationRoot}. FileOp did not scan the path directly.");
                }

                if (!target.HasCheckpoint)
                {
                    if (!IsSnapshotRequiredState(target.State))
                    {
                        return StorageKnownLocationReviewClassifier.CreateUnavailable(
                            provenance,
                            StorageReviewLocationStatus.Unavailable,
                            fullPath,
                            $"{FormatProvenance(provenance)} maps to indexed volume {target.RootPath}, but its checkpoint descriptor is temporarily unavailable in state {target.State}. No cross-volume evidence was presented; retry after indexing maintenance finishes.");
                    }

                    return StorageKnownLocationReviewClassifier.CreateUnavailable(
                        provenance,
                        StorageReviewLocationStatus.Unavailable,
                        fullPath,
                        $"{FormatProvenance(provenance)} maps to indexed volume {target.RootPath}, but that volume has no existing checkpoint. Known-location review will not rebuild a secondary volume implicitly.");
                }

                var request = new IndexingVolumeRequest(target.VolumeIdentity, target.RootPath);
                var catchUp = await CatchUpAsync(
                    session,
                    request,
                    startingUsn: null,
                    InitialCatchUpBatchLimit,
                    _lifetimeCancellation.Token).ConfigureAwait(false);
                if (!catchUp.IsCurrent)
                {
                    return StorageKnownLocationReviewClassifier.CreateUnavailable(
                        provenance,
                        StorageReviewLocationStatus.Unavailable,
                        fullPath,
                        $"{FormatProvenance(provenance)} maps to indexed volume {target.RootPath}, but bounded journal catch-up did not reach a current checkpoint. No stale cross-volume review evidence was presented.");
                }

                var response = await session.Client.AnalyzeStorageOptimizationAsync(
                    new IndexingStorageOptimizationRequest(
                        target.VolumeIdentity,
                        target.RootPath,
                        fullPath),
                    _lifetimeCancellation.Token).ConfigureAwait(false);
                if (!ReviewPathsEqual(response.Analysis.RootPath, fullPath))
                {
                    return StorageKnownLocationReviewClassifier.CreateUnavailable(
                        provenance,
                        StorageReviewLocationStatus.Unavailable,
                        fullPath,
                        $"{FormatProvenance(provenance)} returned analysis for an unexpected or malformed root, so the cross-volume evidence was discarded.");
                }

                var volumesAfter = await session.Client
                    .GetVolumesAsync(_lifetimeCancellation.Token)
                    .ConfigureAwait(false);
                var current = FindUniqueIndexedVolumeByRoot(
                    volumesAfter.Volumes,
                    locationRoot,
                    out var currentAmbiguous,
                    out var currentInvalidCatalog);
                if (currentInvalidCatalog)
                {
                    return StorageKnownLocationReviewClassifier.CreateUnavailable(
                        provenance,
                        StorageReviewLocationStatus.Unavailable,
                        fullPath,
                        $"{FormatProvenance(provenance)} native volume catalog became malformed while evidence was being captured, so the result was discarded.");
                }

                if (currentAmbiguous ||
                    current is null ||
                    current.VolumeIdentity != target.VolumeIdentity ||
                    !ReviewRootsEqual(current.RootPath, target.RootPath))
                {
                    return StorageKnownLocationReviewClassifier.CreateUnavailable(
                        provenance,
                        StorageReviewLocationStatus.Unavailable,
                        fullPath,
                        currentAmbiguous
                            ? $"{FormatProvenance(provenance)} indexed-volume source became ambiguous while evidence was being captured, so the result was discarded."
                            : $"{FormatProvenance(provenance)} indexed-volume identity or root changed while evidence was being captured, so the result was discarded.");
                }

                if (!current.HasCheckpoint)
                {
                    if (!IsSnapshotRequiredState(current.State))
                    {
                        return StorageKnownLocationReviewClassifier.CreateUnavailable(
                            provenance,
                            StorageReviewLocationStatus.Unavailable,
                            fullPath,
                            $"{FormatProvenance(provenance)} indexed volume {current.RootPath} became temporarily unavailable in state {current.State} while evidence was being captured, so the result was discarded.");
                    }

                    return StorageKnownLocationReviewClassifier.CreateUnavailable(
                        provenance,
                        StorageReviewLocationStatus.Unavailable,
                        fullPath,
                        $"{FormatProvenance(provenance)} indexed volume {current.RootPath} lost its durable checkpoint while evidence was being captured, so the result was discarded.");
                }

                var primaryAfter = FindUniqueIndexedVolumeByRoot(
                    volumesAfter.Volumes,
                    activeRoot,
                    out var primaryAfterAmbiguous,
                    out var primaryAfterInvalidCatalog);
                if (primaryAfterInvalidCatalog)
                {
                    return StorageKnownLocationReviewClassifier.CreateUnavailable(
                        provenance,
                        StorageReviewLocationStatus.Unavailable,
                        fullPath,
                        $"{FormatProvenance(provenance)} active primary native catalog became malformed while secondary-volume evidence was being captured, so the result was discarded.");
                }

                if (primaryAfterAmbiguous ||
                    primaryAfter is null ||
                    primaryAfter.VolumeIdentity != primaryBefore.VolumeIdentity ||
                    !ReviewRootsEqual(primaryAfter.RootPath, primaryBefore.RootPath))
                {
                    return StorageKnownLocationReviewClassifier.CreateUnavailable(
                        provenance,
                        StorageReviewLocationStatus.Unavailable,
                        fullPath,
                        primaryAfterAmbiguous
                            ? $"{FormatProvenance(provenance)} active primary indexed-volume source became ambiguous while secondary-volume evidence was being captured, so the result was discarded."
                            : $"{FormatProvenance(provenance)} active primary indexed-volume identity or root changed while secondary-volume evidence was being captured, so the result was discarded.");
                }

                if (_primaryVolume is not { } currentPrimary ||
                    currentPrimary.VolumeIdentity != primaryBefore.VolumeIdentity ||
                    !ReviewRootsEqual(currentPrimary.RootPath, activeRoot))
                {
                    return StorageKnownLocationReviewClassifier.CreateUnavailable(
                        provenance,
                        StorageReviewLocationStatus.Unavailable,
                        fullPath,
                        $"{FormatProvenance(provenance)} review lost the active primary source while secondary-volume evidence was being captured.");
                }

                var classified = StorageKnownLocationReviewClassifier.Classify(
                    response.Analysis,
                    provenance);
                return classified with
                {
                    SourceVolumeIdentity = target.VolumeIdentity,
                    Detail = classified.Detail +
                        $" Evidence came from current checkpointed indexed volume {target.RootPath}; the active Files/Storage volume remains {activeRoot}.",
                };
            }
            finally
            {
                _nativeOperationGate.Release();
            }
        }
        finally
        {
            _searchOperationGate.Release();
        }
    }

    private static IndexingVolumeDescriptor? FindUniqueIndexedVolumeByRoot(
        IReadOnlyList<IndexingVolumeDescriptor> volumes,
        string rootPath,
        out bool ambiguous,
        out bool invalidCatalog)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        ambiguous = false;
        invalidCatalog = false;
        if (!TryNormalizeReviewRoot(rootPath, out var normalizedRoot))
        {
            invalidCatalog = true;
            return null;
        }

        IndexingVolumeDescriptor? match = null;
        foreach (var volume in volumes)
        {
            if (!TryNormalizeReviewRoot(volume.RootPath, out var normalizedVolumeRoot))
            {
                invalidCatalog = true;
                return null;
            }

            if (!string.Equals(
                    normalizedVolumeRoot,
                    normalizedRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (match is not null)
            {
                ambiguous = true;
                return null;
            }

            match = volume;
        }

        return match;
    }

    private static bool ReviewRootsEqual(string? left, string? right) =>
        TryNormalizeReviewRoot(left, out var normalizedLeft) &&
        TryNormalizeReviewRoot(right, out var normalizedRight) &&
        string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);

    private static bool TryNormalizeReviewRoot(string? path, out string normalizedRoot)
    {
        normalizedRoot = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                return false;
            }

            var fullPath = Path.GetFullPath(path);
            var filesystemRoot = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(filesystemRoot))
            {
                return false;
            }

            normalizedRoot = NormalizeRoot(fullPath);
            var normalizedFilesystemRoot = NormalizeRoot(filesystemRoot);
            if (!string.Equals(
                    normalizedRoot,
                    normalizedFilesystemRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                normalizedRoot = string.Empty;
                return false;
            }

            return true;
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            normalizedRoot = string.Empty;
            return false;
        }
    }

    private static bool ReviewPathsEqual(string? left, string? right) =>
        TryNormalizeReviewPath(left, out var normalizedLeft) &&
        TryNormalizeReviewPath(right, out var normalizedRight) &&
        string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);

    private static bool TryNormalizeReviewPath(string? path, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                return false;
            }

            normalizedPath = Path.GetFullPath(path).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            return true;
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsSnapshotRequiredState(string state) =>
        string.Equals(state, "SnapshotRequired", StringComparison.OrdinalIgnoreCase);
}
