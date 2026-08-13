using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FileOp.App;

public sealed partial class MainWindow
{
    internal async void FilesView_DeleteSessionFinished(
        object? sender,
        FilesDeleteSessionFinishedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_closed || !_filesInitialized)
        {
            return;
        }

        var activeRefreshes = new List<Task>();
        foreach (var pane in EnumerateFilesPanes())
        {
            foreach (var tab in pane.Tabs.Where(tab =>
                tab.CurrentPath is not null &&
                PathsEqual(tab.CurrentPath, e.SourceDirectoryPath)))
            {
                ResetFilesTabPaging(tab, clearPath: false);
            }

            var activeTab = pane.ActiveTab;
            if (activeTab.CurrentPath is not null &&
                PathsEqual(activeTab.CurrentPath, e.SourceDirectoryPath))
            {
                activeRefreshes.Add(
                    LoadFilesDirectoryAsync(
                        pane,
                        e.SourceDirectoryPath,
                        forceRefresh: true));
            }
        }

        if (activeRefreshes.Count != 0)
        {
            try
            {
                await Task.WhenAll(activeRefreshes);
            }
            catch (Exception exception)
            {
                SetFilesStatus(
                    $"Delete history was persisted, but Files could not refresh the affected directory: {exception.Message}");
                return;
            }
        }

        SetFilesStatus(
            "Delete session durable state changed. Files invalidated cached pages for the affected directory; active matching panes were refreshed.");
    }
}
