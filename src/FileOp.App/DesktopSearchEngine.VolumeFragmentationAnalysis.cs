using FileOp.Core.Performance;
using FileOp.Windows.Performance;

namespace FileOp.App;

internal sealed partial class DesktopSearchEngine
{
    private readonly IVolumeFragmentationAnalysisProvider _volumeFragmentationAnalysisProvider =
        new WindowsVolumeFragmentationAnalysisProvider();

    public string? VolumeFragmentationAnalysisRoot
    {
        get
        {
            var storageRootPath = StorageRootPath;
            if (_disposed || string.IsNullOrWhiteSpace(storageRootPath))
            {
                return null;
            }

            try
            {
                var storageRoot = Path.GetFullPath(storageRootPath);
                var driveRoot = Path.GetPathRoot(storageRoot);
                return string.IsNullOrWhiteSpace(driveRoot)
                    ? null
                    : VolumeFragmentationDriveRoot.RequireCanonical(driveRoot);
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                IOException or
                NotSupportedException or
                PathTooLongException)
            {
                return null;
            }
        }
    }

    public ValueTask<VolumeFragmentationAnalysisResult> CaptureVolumeFragmentationAnalysisAsync(
        string expectedVolumeRoot)
    {
        var canonicalExpected = VolumeFragmentationDriveRoot.RequireCanonical(expectedVolumeRoot);
        ThrowIfDisposed();
        var currentRoot = VolumeFragmentationAnalysisRoot;
        if (currentRoot is null ||
            !string.Equals(
                currentRoot,
                canonicalExpected,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The Storage source moved to a different volume before fragmentation analysis started.");
        }

        return _volumeFragmentationAnalysisProvider.AnalyzeAsync(
            canonicalExpected,
            VolumeFragmentationAnalysisBudget.Default,
            _lifetimeCancellation.Token);
    }
}
