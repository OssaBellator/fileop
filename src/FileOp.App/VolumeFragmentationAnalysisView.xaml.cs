using FileOp.Core.Performance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class VolumeFragmentationAnalysisView : UserControl
{
    public VolumeFragmentationAnalysisView()
    {
        InitializeComponent();
    }

    public void SetLoading(string volumeRoot)
    {
        VolumeText.Text = VolumeFragmentationDriveRoot.RequireCanonical(volumeRoot);
        CaptureButton.IsEnabled = false;
        ResetEvidence(keepVolume: true);
        StatusText.Text =
            $"Running one bounded analysis-only compatibility probe for {VolumeText.Text}. FileOp will preserve Windows' raw result and structured fragmentation fields when available; no storage optimization action is being run.";
    }

    public void SetReady(bool ready) =>
        CaptureButton.IsEnabled = ready;

    public void SetUnavailable(string message, bool canRetry = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        CaptureButton.IsEnabled = canRetry;
        ResetEvidence(keepVolume: false);
        StatusText.Text = message;
    }

    public void Apply(VolumeFragmentationAnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        CaptureButton.IsEnabled = true;
        VolumeText.Text = result.VolumeRoot;
        if (result.Status != VolumeFragmentationAnalysisStatus.Completed ||
            result.Evidence is not { } evidence)
        {
            ResetEvidence(keepVolume: true);
            var providerCode = result.ProviderReturnCode is { } code
                ? $"provider return {code}"
                : "no provider return code";
            StatusText.Text =
                $"Fragmentation analysis {result.Status} · {providerCode} · elapsed {result.Elapsed.TotalMilliseconds:N2} ms. {result.Detail}";
            return;
        }

        RecommendationText.Text = evidence.WindowsDefragRecommended
            ? "reported yes · evidence only"
            : "reported no · evidence only";
        FragmentationText.Text = $"{evidence.FilePercentFragmentation:N0}%";
        AverageFragmentsText.Text = $"{evidence.AverageFragmentsPerFile:N2}";
        FilesText.Text = $"{evidence.TotalFragmentedFiles:N0} / {evidence.TotalFiles:N0}";
        FreeExtentsText.Text = $"{evidence.TotalFreeSpaceExtents:N0} extent(s)";
        FreeExtentSizeText.Text =
            $"{FormatBytes(evidence.LargestFreeSpaceExtentBytes)} / {FormatBytes(evidence.AverageFreeSpacePerExtentBytes)}";
        SpaceText.Text =
            $"{FormatBytes(evidence.UsedSpaceBytes)} / {FormatBytes(evidence.FreeSpaceBytes)} / {FormatBytes(evidence.VolumeSizeBytes)}";
        StatusText.Text =
            $"{result.Detail} Provider return {result.ProviderReturnCode ?? 0}; elapsed {result.Elapsed.TotalMilliseconds:N2} ms. " +
            "The Windows legacy recommendation is displayed as compatibility evidence only; FileOp does not turn it into a Defrag/ReTrim action or a health/urgency verdict.";
    }

    private async void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App { MainWindow: { } window })
        {
            await window.CapturePerformanceVolumeFragmentationAnalysisAsync();
        }
    }

    private void ResetEvidence(bool keepVolume)
    {
        if (!keepVolume)
        {
            VolumeText.Text = "—";
        }
        RecommendationText.Text = "—";
        FragmentationText.Text = "—";
        AverageFragmentsText.Text = "—";
        FilesText.Text = "—";
        FreeExtentsText.Text = "—";
        FreeExtentSizeText.Text = "—";
        SpaceText.Text = "—";
    }

    private static string FormatBytes(ulong bytes) =>
        FormatBytes((double)bytes);

    private static string FormatBytes(double bytes)
    {
        if (!double.IsFinite(bytes) || bytes < 0)
        {
            return "unavailable";
        }

        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        var value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value:N0} {units[unit]}"
            : $"{value:N2} {units[unit]}";
    }
}
