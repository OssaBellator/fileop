namespace FileOp.Windows.IndexingService;

/// <summary>
/// Coordinates access to one persistent volume index across FileOp.Indexer processes.
/// Read leases may coexist; a maintenance lease is exclusive for the full snapshot/sync operation.
/// </summary>
public sealed class IndexingVolumeFileGate
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    private readonly string _lockPath;

    public IndexingVolumeFileGate(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullDatabasePath = Path.GetFullPath(databasePath);
        _lockPath = fullDatabasePath + ".lock";
        EnsureLockFileExists();
    }

    public IDisposable? TryAcquireRead() => TryOpen(FileAccess.Read, FileShare.Read);

    public IDisposable? TryAcquireMaintenance() => TryOpen(FileAccess.ReadWrite, FileShare.None);

    private FileStream? TryOpen(FileAccess access, FileShare share)
    {
        try
        {
            return new FileStream(
                _lockPath,
                FileMode.Open,
                access,
                share,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException exception) when (IsSharingOrLockViolation(exception))
        {
            return null;
        }
        catch (FileNotFoundException)
        {
            EnsureLockFileExists();
            try
            {
                return new FileStream(
                    _lockPath,
                    FileMode.Open,
                    access,
                    share,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException exception) when (IsSharingOrLockViolation(exception))
            {
                return null;
            }
        }
    }

    private void EnsureLockFileExists()
    {
        try
        {
            using var stream = new FileStream(
                _lockPath,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException exception) when (IsSharingOrLockViolation(exception))
        {
            // Another helper can already hold the established lock file exclusively.
            // In that case the file necessarily exists and acquisition will report busy.
        }
    }

    private static bool IsSharingOrLockViolation(IOException exception)
    {
        var win32Code = exception.HResult & 0xFFFF;
        return win32Code is ErrorSharingViolation or ErrorLockViolation;
    }
}
