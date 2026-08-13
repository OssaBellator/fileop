using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace FileOp.App;

public sealed partial class FilesView
{
    private const string DeleteSessionLockFileName = "delete-session.lock";

    private async void ReviewDeleteLeftWithSessionGateButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await RunDeleteSessionWithInterprocessGateAsync(LeftPane, RightPane);

    private async void ReviewDeleteRightWithSessionGateButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await RunDeleteSessionWithInterprocessGateAsync(RightPane, LeftPane);

    private async Task RunDeleteSessionWithInterprocessGateAsync(
        FilesPaneView source,
        FilesPaneView other)
    {
        if (_deleteSessionRunning ||
            _pendingDeleteLeaseRelease is { FinalLeaseReleasePending: true })
        {
            await RunDeleteSessionAsync(source, other);
            return;
        }

        FileStream sessionGate;
        try
        {
            sessionGate = AcquireDeleteSessionGate(GetDeleteHistoryDatabasePath());
        }
        catch (IOException exception)
        {
            DeleteStatusText.Text =
                $"Delete review could not acquire the per-user destructive-session lock. Another FileOp process may already be reviewing or executing deletion, or the app-data lock is unavailable: {exception.Message} No delete authorization was issued.";
            return;
        }
        catch (UnauthorizedAccessException exception)
        {
            DeleteStatusText.Text =
                $"Delete review cannot create or open its per-user destructive-session lock: {exception.Message} No delete authorization was issued.";
            return;
        }

        using (sessionGate)
        {
            // The lock is app-control state under LocalApplicationData, not a target-file
            // mutation. Holding FileShare.None from before recovery inspection through the
            // complete session closes the cross-process confirmation/authorization race for
            // FileOp instances that implement this reviewed boundary.
            await RunDeleteSessionAsync(source, other);
        }
    }

    private static FileStream AcquireDeleteSessionGate(string historyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(historyPath);
        var directory = Path.GetDirectoryName(historyPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException(
                "Durable delete history has no parent directory for the per-user session lock.");
        }

        Directory.CreateDirectory(directory);
        return new FileStream(
            Path.Combine(directory, DeleteSessionLockFileName),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.None);
    }
}
