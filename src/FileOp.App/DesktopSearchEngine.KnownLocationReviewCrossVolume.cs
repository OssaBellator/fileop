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
                    !string.Equals(
                        NormalizeRoot(primaryVolume.RootPath),
                        NormalizeRoot(activeRoot),
                        StringComparison.OrdinalIgnoreCase))
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
                var target = FindIndexedVolumeByRoot(volumesBefore.Volumes, locationRoot);
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
                if (!string.Equals(
                        Path.GetFullPath(response.Analysis.RootPath),
                        fullPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return StorageKnownLocationReviewClassifier.CreateUnavailable(
                        provenance,
                        StorageReviewLocationStatus.Unavailable,
                        fullPath,
                        $"{FormatProvenance(provenance)} returned analysis for an unexpected root, so the cross-volume evidence was discarded.");
                }

                var volumesAfter = await session.Client
                    .GetVolumesAsync(_lifetimeCancellation.Token)
                    .ConfigureAwait(false);
                var current = FindIndexedVolumeByRoot(volumesAfter.Volumes, locationRoot);
                if (current is null ||
                    current.VolumeIdentity != target.VolumeIdentity ||
                    !string.Equals(
                        NormalizeRoot(current.RootPath),
                        NormalizeRoot(target.RootPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return StorageKnownLocationReviewClassifier.CreateUnavailable(
                        provenance,
                        StorageReviewLocationStatus.Unavailable,
                        fullPath,
                        $"{FormatProvenance(provenance)} indexed-volume identity or root changed while evidence was being captured, so the result was discarded.");
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

                if (_primaryVolume is not { } currentPrimary ||
                    !string.Equals(
                        NormalizeRoot(currentPrimary.RootPath),
                        NormalizeRoot(activeRoot),
                        StringComparison.OrdinalIgnoreCase))
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

    private static IndexingVolumeDescriptor? FindIndexedVolumeByRoot(
        IReadOnlyList<IndexingVolumeDescriptor> volumes,
        string rootPath)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var normalizedRoot = NormalizeRoot(rootPath);
        return volumes.FirstOrDefault(volume =>
            string.Equals(
                NormalizeRoot(volume.RootPath),
                normalizedRoot,
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSnapshotRequiredState(string state) =>
        string.Equals(state, "SnapshotRequired", StringComparison.OrdinalIgnoreCase);
}
