using FileOp.Core.Performance;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class DiskIoAttributionView
{
    public void ApplyFailurePredictionEvidence(
        IReadOnlyList<DiskIoPhysicalDiskDeviceEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        EnsureDeviceEvidencePanel();
        if (_deviceEvidenceRowsPanel!.Children.Count != evidence.Count)
        {
            throw new InvalidOperationException(
                "Failure-prediction annotations require one existing device-evidence row per physical disk.");
        }

        for (var index = 0; index < evidence.Count; index++)
        {
            var item = evidence[index];
            if (!item.QueryAttempted)
            {
                continue;
            }
            if (_deviceEvidenceRowsPanel.Children[index] is not Border border ||
                border.Child is not StackPanel content)
            {
                throw new InvalidOperationException(
                    "Failure-prediction annotations require the existing device-evidence row shape.");
            }

            content.Children.Add(EvidenceText(
                $"Windows failure prediction: {FormatFailurePrediction(item.FailurePrediction)}"));
        }

        _deviceEvidenceStatusText!.Text +=
            " Windows storage-stack failure prediction is shown as separate compatibility evidence; a raw zero means no current prediction from that interface, not a comprehensive device-health verdict.";
    }

    private static string FormatFailurePrediction(
        PhysicalDiskFailurePredictionResult? result)
    {
        if (result is null)
        {
            return "not attached in this compatibility result.";
        }
        if (result.Status != PhysicalDiskFailurePredictionStatus.Available ||
            result.Evidence is not { } prediction)
        {
            return
                $"{result.Status}. {result.Detail} Query elapsed {result.Elapsed.TotalMilliseconds:N2} ms.";
        }

        var reported = prediction.FailurePredicted
            ? $"failure prediction reported (raw {prediction.WindowsPredictFailureValue})"
            : "no current prediction reported (raw 0)";
        return
            $"{reported}. {result.Detail} Query elapsed {result.Elapsed.TotalMilliseconds:N2} ms.";
    }
}
