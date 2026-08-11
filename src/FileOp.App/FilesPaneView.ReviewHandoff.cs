namespace FileOp.App;

public sealed partial class FilesPaneView
{
    public bool SetReviewSelectionHint(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _selectedPaths.Clear();
        _selectedPaths.Add(path);
        RestoreSelection();
        RaiseIntentStateChanged();

        return FilesList.SelectedItems
            .OfType<FileBrowserRow>()
            .Any(row => string.Equals(row.Path, path, StringComparison.OrdinalIgnoreCase));
    }
}
