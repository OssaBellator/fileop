using FileOp.Core.Indexing.Service;

namespace FileOp.App;

internal sealed partial class DesktopSearchEngine
{
    internal async ValueTask<bool> IsNativeReviewSourceCurrentAsync(
        ulong expectedVolumeIdentity,
        string expectedRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRoot);
        ThrowIfDisposed();

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        var token = linkedCancellation.Token;
        var normalizedExpectedRoot = NormalizeRoot(expectedRoot);

        await _searchOperationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_nativeSession is not { Client.IsConnected: true } ||
                _primaryVolume is not { } selectedPrimary ||
                selectedPrimary.VolumeIdentity != expectedVolumeIdentity ||
                !string.Equals(
                    NormalizeRoot(selectedPrimary.RootPath),
                    normalizedExpectedRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            await _nativeOperationGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_nativeSession is not { Client.IsConnected: true } session ||
                    _primaryVolume is not { } currentPrimary ||
                    currentPrimary.VolumeIdentity != expectedVolumeIdentity ||
                    !string.Equals(
                        NormalizeRoot(currentPrimary.RootPath),
                        normalizedExpectedRoot,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var volumes = await session.Client.GetVolumesAsync(token).ConfigureAwait(false);
                var currentDescriptor = FindUniqueIndexedVolumeByRoot(
                    volumes.Volumes,
                    expectedRoot,
                    out var ambiguous);
                return !ambiguous &&
                    currentDescriptor is not null &&
                    currentDescriptor.VolumeIdentity == expectedVolumeIdentity &&
                    string.Equals(
                        NormalizeRoot(currentDescriptor.RootPath),
                        normalizedExpectedRoot,
                        StringComparison.OrdinalIgnoreCase);
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
}
