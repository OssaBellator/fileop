using FileOp.Core.Indexing.Service;
using FileOp.Core.Storage;
using FileOp.Windows.Storage;

namespace FileOp.App;

internal sealed partial class DesktopSearchEngine
{
    private readonly WindowsSameSizeContentVerifier _sameSizeContentVerifier = new();

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
                if (!ReviewPathsEqual(response.Analysis.RootPath, fullPath))
                {
                    throw new InvalidOperationException(
                        "The native storage index returned optimization analysis for an unexpected directory root.");
                }

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

    public ValueTask<StorageSameSizeContentVerification> VerifySameSizeContentAsync(
        string analysisRootPath,
        StorageSameSizeCandidateGroup group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(analysisRootPath);
        ArgumentNullException.ThrowIfNull(group);
        ThrowIfDisposed();

        var fullRoot = Path.GetFullPath(analysisRootPath);
        var currentRoot = StorageRootPath;
        if (string.IsNullOrWhiteSpace(currentRoot) ||
            !string.Equals(
                NormalizeRoot(currentRoot),
                NormalizeRoot(fullRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The Storage source changed after these same-size candidates were collected. Refresh Optimize before verifying content.");
        }

        foreach (var file in group.SampleFiles)
        {
            EnsurePathWithinRoot(Path.GetFullPath(file.Path), fullRoot);
        }

        return _sameSizeContentVerifier.VerifyAsync(
            group,
            StorageSameSizeContentVerificationPolicy.Default,
            _lifetimeCancellation.Token);
    }
}
