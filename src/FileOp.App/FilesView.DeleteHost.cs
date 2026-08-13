using Microsoft.UI.Xaml;

namespace FileOp.App;

public sealed partial class FilesView
{
    private bool _deleteHostWiringNormalized;

    private void FilesView_DeleteHostLayoutUpdated(object sender, object e)
    {
        if (_deleteHostWiringNormalized ||
            Application.Current is not App app ||
            app.MainWindow is not { } window)
        {
            return;
        }

        // InitializeFilesFeature creates the Files view before App.MainWindow is published.
        // Loaded normally occurs only after activation, but normalize the host wiring here once
        // the view is definitely participating in layout rather than relying on that lifecycle
        // ordering. Remove-before-add also collapses any subscription made by Loaded to one copy.
        DeleteSessionFinished -= window.FilesView_DeleteSessionFinished;
        DeleteSessionFinished += window.FilesView_DeleteSessionFinished;
        window.Closed -= FilesDeleteHostWindow_Closed;
        window.Closed += FilesDeleteHostWindow_Closed;
        _deleteHostWiringNormalized = true;
    }
}
