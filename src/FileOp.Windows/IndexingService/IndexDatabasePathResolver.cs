namespace FileOp.Windows.IndexingService;

public static class IndexDatabasePathResolver
{
    public static string CreateKey(ulong volumeIdentity, string volumeRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeRootPath);
        var root = NormalizeRoot(volumeRootPath);
        var rootToken = new string(root.Where(static character => char.IsLetterOrDigit(character)).ToArray());
        if (string.IsNullOrEmpty(rootToken))
        {
            rootToken = "root";
        }

        return $"ntfs-{volumeIdentity:X16}-{rootToken.ToLowerInvariant()}";
    }

    public static string CreatePath(
        string databaseDirectory,
        ulong volumeIdentity,
        string volumeRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseDirectory);
        var directory = Path.GetFullPath(databaseDirectory);
        return Path.Combine(directory, $"{CreateKey(volumeIdentity, volumeRootPath)}.sqlite");
    }

    private static string NormalizeRoot(string rootPath) =>
        Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
}
