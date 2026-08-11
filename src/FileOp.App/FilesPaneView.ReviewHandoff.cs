namespace FileOp.App;

public sealed partial class FilesPaneView
{
    public bool TrySelectVisiblePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!_directoryReady)
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        var row = FilesList.Items
            .OfType<FileBrowserRow>()
            .FirstOrDefault(candidate =>
                string.Equals(
                    Path.GetFullPath(candidate.Path),
                    fullPath,
                    StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return false;
        }

        _restoringSelection = true;
        try
        {
            _selectedPaths.Clear();
            _selectedPaths.Add(row.Path);
            FilesList.SelectedItems.Clear();
            FilesList.SelectedItems.Add(row);
        }
        finally
        {
            _restoringSelection = false;
            UpdateSelectionActions();
        }

        RaiseIntentStateChanged();
        return true;
    }
}
