namespace FileOp.Windows.IndexingService;

public static class IndexingServiceHelperLocator
{
    public const string HelperFileName = "FileOp.Indexer.exe";

    public static string ResolveAdjacentHelper(string applicationBaseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationBaseDirectory);

        var baseDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationBaseDirectory));
        var helperPath = Path.GetFullPath(Path.Combine(baseDirectory, HelperFileName));
        var helperDirectory = Path.GetDirectoryName(helperPath);

        if (!string.Equals(helperDirectory, baseDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The FileOp indexing helper resolved outside the application directory.");
        }

        if (!File.Exists(helperPath))
        {
            throw new FileNotFoundException(
                "The FileOp indexing helper is not installed beside the desktop application.",
                helperPath);
        }

        if ((File.GetAttributes(helperPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "The FileOp indexing helper cannot be launched through a filesystem reparse point.");
        }

        return helperPath;
    }
}
