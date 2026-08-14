using System;

namespace FileOp.App;

public sealed partial class FilesPaneView
{
    /// <summary>
    /// Requests the same indexed refresh path used by the pane's Refresh button.
    /// File-operation UI uses this after a terminal namespace mutation so it does
    /// not bypass MainWindow's source/generation/browse coordination.
    /// </summary>
    public void RequestRefresh() => RefreshRequested?.Invoke(this, EventArgs.Empty);
}
