using FileOp.Core.Indexing;
using Microsoft.Data.Sqlite;

namespace FileOp.Windows.IndexingService;

/// <summary>
/// Holds both publication gates for the exact lifetime of SQLite quiescence evidence.
/// The lease is intentionally non-authorizing: it can only drive the existing Core
/// transaction across its SwapStarted barrier while the gates that produced the
/// evidence are still held.
/// </summary>
public sealed class WindowsIndexRebuildPublicationQuiescenceLease : IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim _operationGate;
    private IDisposable? _maintenanceLease;
    private int _disposed;

    internal WindowsIndexRebuildPublicationQuiescenceLease(
        SemaphoreSlim operationGate,
        IDisposable maintenanceLease,
        IndexRebuildPublicationQuiescenceEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(operationGate);
        ArgumentNullException.ThrowIfNull(maintenanceLease);
        ArgumentNullException.ThrowIfNull(evidence);
        if (!evidence.CanStartFilesystemSwap)
        {
            throw new ArgumentException(
                "A Windows publication quiescence lease requires complete SQLite quiescence evidence.",
                nameof(evidence));
        }

        _operationGate = operationGate;
        _maintenanceLease = maintenanceLease;
        Evidence = evidence;
    }

    public IndexRebuildPublicationQuiescenceEvidence Evidence { get; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public bool GrantsFilesystemMutationAuthority => false;

    public IndexRebuildPublicationAttempt MarkSwapStarted(
        IndexRebuildPublicationAttempt attempt,
        IndexRebuildPublicationState currentPublicationState)
    {
        ThrowIfDisposed();
        return IndexRebuildPublicationTransactionPolicy.MarkSwapStarted(
            attempt,
            currentPublicationState,
            Evidence);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var maintenanceLease = Interlocked.Exchange(ref _maintenanceLease, null);
        try
        {
            maintenanceLease?.Dispose();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
    }
}

/// <summary>
/// Produces a Windows-held quiescence lease for a verified live/shadow SQLite pair.
/// The local operation semaphore and cross-process lock are obtained from the same
/// database-bound gate object so callers cannot substitute unrelated local-gate evidence.
/// This class never moves, replaces, deletes, rolls back, or cleans up database files.
/// </summary>
public sealed class WindowsIndexRebuildPublicationQuiescenceProvider
{
    public async ValueTask<WindowsIndexRebuildPublicationQuiescenceLease?> TryAcquireAsync(
        IndexingVolumeFileGate publicationGate,
        string liveDatabasePath,
        string shadowDatabasePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publicationGate);
        ArgumentException.ThrowIfNullOrWhiteSpace(liveDatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(shadowDatabasePath);

        var livePath = Path.GetFullPath(liveDatabasePath);
        var shadowPath = Path.GetFullPath(shadowDatabasePath);
        ValidatePathPair(livePath, shadowPath);
        ValidateDatabaseFile(livePath, nameof(liveDatabasePath));
        ValidateDatabaseFile(shadowPath, nameof(shadowDatabasePath));

        if (!string.Equals(publicationGate.DatabasePath, livePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The publication gate is not bound to the exact live database path.",
                nameof(publicationGate));
        }

        var operationGate = publicationGate.OperationGate;
        if (!await operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var localGateHeld = true;
        IDisposable? maintenanceLease = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            maintenanceLease = publicationGate.TryAcquireMaintenance();
            if (maintenanceLease is null)
            {
                return null;
            }

            await RequireTruncatedWalAsync(livePath, "live", cancellationToken).ConfigureAwait(false);
            await RequireTruncatedWalAsync(shadowPath, "shadow", cancellationToken).ConfigureAwait(false);

            // Existing index objects use pooled connections. The checkpoint connections above
            // are non-pooled and already closed here; clearing every pool is required before
            // sidecar absence can be treated as meaningful quiescence evidence.
            SqliteConnection.ClearAllPools();

            RequireSidecarsAbsent(livePath, "live");
            RequireSidecarsAbsent(shadowPath, "shadow");

            var evidence = new IndexRebuildPublicationQuiescenceEvidence(
                livePath,
                shadowPath,
                LocalVolumeOperationGateHeld: true,
                CrossProcessMaintenanceLeaseHeld: true,
                LiveWalCheckpointComplete: true,
                ShadowWalCheckpointComplete: true,
                SqliteConnectionPoolsCleared: true,
                LiveWalAndShmSidecarsQuiesced: true,
                ShadowWalAndShmSidecarsQuiesced: true);

            var lease = new WindowsIndexRebuildPublicationQuiescenceLease(
                operationGate,
                maintenanceLease,
                evidence);
            maintenanceLease = null;
            localGateHeld = false;
            return lease;
        }
        finally
        {
            if (maintenanceLease is not null || localGateHeld)
            {
                try
                {
                    maintenanceLease?.Dispose();
                }
                finally
                {
                    if (localGateHeld)
                    {
                        operationGate.Release();
                    }
                }
            }
        }
    }

    private static void ValidatePathPair(string livePath, string shadowPath)
    {
        if (string.Equals(livePath, shadowPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Live and shadow publication databases must be distinct files.");
        }

        var liveDirectory = Path.GetDirectoryName(livePath);
        var shadowDirectory = Path.GetDirectoryName(shadowPath);
        if (string.IsNullOrWhiteSpace(liveDirectory) ||
            string.IsNullOrWhiteSpace(shadowDirectory) ||
            !string.Equals(liveDirectory, shadowDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Live and shadow publication databases must be sibling files in the same directory.");
        }

        var directoryAttributes = File.GetAttributes(liveDirectory);
        if ((directoryAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "Index publication refuses a database directory whose final path component is a reparse point.");
        }
    }

    private static void ValidateDatabaseFile(string path, string parameterName)
    {
        if (!File.Exists(path))
        {
            throw new ArgumentException("Index publication requires an existing database file.", parameterName);
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new ArgumentException("Index publication database paths must identify regular files.", parameterName);
        }
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Index publication refuses the reparse-point database path '{path}'.");
        }
    }

    private static async ValueTask RequireTruncatedWalAsync(
        string databasePath,
        string role,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"The {role} index WAL checkpoint returned no evidence.");
        }

        var busy = reader.GetInt64(0);
        var remainingWalFrames = reader.GetInt64(1);
        var checkpointedFrames = reader.GetInt64(2);
        if (busy != 0 || remainingWalFrames != 0 || checkpointedFrames != 0)
        {
            throw new InvalidOperationException(
                $"The {role} index WAL could not be fully checkpointed and truncated " +
                $"(busy={busy}, remaining={remainingWalFrames}, checkpointed={checkpointedFrames}).");
        }

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"The {role} index WAL checkpoint returned an unexpected extra result row.");
        }
    }

    private static void RequireSidecarsAbsent(string databasePath, string role)
    {
        var walPath = databasePath + "-wal";
        var shmPath = databasePath + "-shm";
        if (File.Exists(walPath) || File.Exists(shmPath))
        {
            throw new InvalidOperationException(
                $"The {role} index remains SQLite-sidecar-active after checkpoint and pool clearing.");
        }
    }
}
