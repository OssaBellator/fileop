using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileOp.App;

public sealed partial class StorageKnownLocationReviewView
{
    private async void ReviewInFilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: StorageKnownLocationCandidateRow row } ||
            Application.Current is not App app ||
            app.MainWindow is not { } window)
        {
            return;
        }

        await window.ReviewPathInFilesAsync(row.Path);
    }
}
