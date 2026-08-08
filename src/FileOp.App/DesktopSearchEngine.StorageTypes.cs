using FileOp.Core.Indexing.Service;
using FileOp.Core.Storage;
using FileOp.Windows.IndexingService;

namespace FileOp.App;

internal sealed partial class DesktopSearchEngine
{
    public async ValueTask<StorageFileTypeAnalysis> AnalyzeStorageFileTypesAsync(
        string directoryPath,
        int maxTypes = 256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ThrowIfDisposed();
        if (maxTypes <= 0 || maxTypes > MaximumStorageEntryLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTypes),
                maxTypes,
                $"Storage file-type limits must be between 1 and {MaximumStorageEntryLimit:N0}.");
        }

        var fullPath = Path.GetFullPath(directoryPath);
        await _searchOperationGate.WaitAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_nativeSession is { Client.IsConnected: true } && _primaryVolume is not null)
            {
                await _nativeOperationGate.WaitAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
                try
                {
                    if (_nativeSession is { Client.IsConnected: true } session &&
                        _primaryVolume is { } volume)
                    {
                        EnsurePathWithinRoot(fullPath, volume.RootPath);
                        return await AnalyzeNativeStorageFileTypesAsync(
                            session,
                            volume,
                            fullPath,
                            maxTypes).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _nativeOperationGate.Release();
                }
            }

            if (!_fallbackReady || string.IsNullOrWhiteSpace(_fallbackRoot))
            {
                throw new InvalidOperationException("Storage file-type analytics is not currently available.");
            }

            EnsurePathWithinRoot(fullPath, _fallbackRoot);
            return await _fallbackIndex.AnalyzeFileTypesAsync(
                fullPath,
                maxTypes,
                _lifetimeCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            _searchOperationGate.Release();
        }
    }

    private async ValueTask<StorageFileTypeAnalysis> AnalyzeNativeStorageFileTypesAsync(
        IndexingServiceProcessSession session,
        IndexingVolumeDescriptor volume,
        string directoryPath,
        int maxTypes)
    {
        var limit = maxTypes;
        while (true)
        {
            try
            {
                var response = await session.Client.AnalyzeStorageTypesAsync(
                    new IndexingStorageFileTypeRequest(
                        volume.VolumeIdentity,
                        volume.RootPath,
                        directoryPath,
                        limit),
                    _lifetimeCancellation.Token).ConfigureAwait(false);
                return response.Analysis;
            }
            catch (IndexingServiceRemoteException exception)
                when (exception.Error.Code == IndexingServiceErrorCode.ResponseTooLarge && limit > 16)
            {
                limit = Math.Max(16, limit / 2);
            }
        }
    }
}
