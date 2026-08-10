using FileOp.Core.Indexing.Service;
using FileOp.Core.Storage;

namespace FileOp.App;

internal sealed partial class DesktopSearchEngine
{
    public bool StorageOptimizationAvailable =>
        !_disposed &&
        _nativeSession is { Client.IsConnected: true } &&
        _primaryVolume is not null;

    public async ValueTask<StorageOptimizationAnalysis> AnalyzeStorageOptimizationAsync(
        string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ThrowIfDisposed();
        var fullPath = Path.GetFullPath(directoryPath);

        await _searchOperationGate.WaitAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_nativeSession is not { Client.IsConnected: true } || _primaryVolume is null)
            {
                throw new InvalidOperationException(
                    "Storage optimization currently requires the native NTFS index. " +
                    "Fallback snapshots are not used for reclaim recommendations yet.");
            }

            await _nativeOperationGate.WaitAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
            try
            {
                if (_nativeSession is not { Client.IsConnected: true } session ||
                    _primaryVolume is not { } volume)
                {
                    throw new InvalidOperationException("The native storage index is no longer available.");
                }

                EnsurePathWithinRoot(fullPath, volume.RootPath);
                var response = await session.Client.AnalyzeStorageOptimizationAsync(
                    new IndexingStorageOptimizationRequest(
                        volume.VolumeIdentity,
                        volume.RootPath,
                        fullPath),
                    _lifetimeCancellation.Token).ConfigureAwait(false);
                return response.Analysis;
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
