using FileOp.Core.Indexing.Service;
using FileOp.Core.Storage;

namespace FileOp.App;

internal sealed partial class DesktopSearchEngine
{
    internal ValueTask<bool> IsNativeReviewSourceCurrentAsync(
        ulong expectedVolumeIdentity,
        string expectedRoot,
        CancellationToken cancellationToken = default) =>
        IsNativeReviewSourceCurrentAsync(
            expectedVolumeIdentity,
            expectedRoot,
            Array.Empty<StorageKnownLocationReview>(),
            cancellationToken);

    internal async ValueTask<bool> IsNativeReviewSourceCurrentAsync(
        ulong expectedVolumeIdentity,
        string expectedRoot,
        IReadOnlyList<StorageKnownLocationReview> expectedLocations,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRoot);
        ArgumentNullException.ThrowIfNull(expectedLocations);
        ThrowIfDisposed();

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        var token = linkedCancellation.Token;
        if (!TryNormalizeReviewRoot(expectedRoot, out _))
        {
            return false;
        }

        await _searchOperationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_nativeSession is not { Client.IsConnected: true } ||
                _primaryVolume is not { } selectedPrimary ||
                selectedPrimary.VolumeIdentity != expectedVolumeIdentity ||
                !ReviewRootsEqual(selectedPrimary.RootPath, expectedRoot))
            {
                return false;
            }

            await _nativeOperationGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_nativeSession is not { Client.IsConnected: true } session ||
                    _primaryVolume is not { } currentPrimary ||
                    currentPrimary.VolumeIdentity != expectedVolumeIdentity ||
                    !ReviewRootsEqual(currentPrimary.RootPath, expectedRoot))
                {
                    return false;
                }

                var volumes = await session.Client.GetVolumesAsync(token).ConfigureAwait(false);
                if (!MatchesExpectedSource(
                        volumes.Volumes,
                        expectedRoot,
                        expectedVolumeIdentity))
                {
                    return false;
                }

                foreach (var location in expectedLocations)
                {
                    if (location.Status != StorageReviewLocationStatus.Available)
                    {
                        continue;
                    }

                    if (location.SourceVolumeIdentity is not { } sourceVolumeIdentity ||
                        string.IsNullOrWhiteSpace(location.RootPath))
                    {
                        return false;
                    }

                    string? sourceRoot;
                    try
                    {
                        sourceRoot = Path.GetPathRoot(location.RootPath);
                    }
                    catch (Exception exception)
                        when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                    {
                        return false;
                    }

                    if (string.IsNullOrWhiteSpace(sourceRoot) ||
                        !MatchesExpectedSource(
                            volumes.Volumes,
                            sourceRoot,
                            sourceVolumeIdentity))
                    {
                        return false;
                    }
                }

                return true;
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

    private static bool MatchesExpectedSource(
        IReadOnlyList<IndexingVolumeDescriptor> volumes,
        string expectedRoot,
        ulong expectedVolumeIdentity)
    {
        var currentDescriptor = FindUniqueIndexedVolumeByRoot(
            volumes,
            expectedRoot,
            out var ambiguous,
            out var invalidCatalog);
        return !invalidCatalog &&
            !ambiguous &&
            currentDescriptor is not null &&
            currentDescriptor.HasCheckpoint &&
            currentDescriptor.VolumeIdentity == expectedVolumeIdentity &&
            ReviewRootsEqual(currentDescriptor.RootPath, expectedRoot);
    }
}
