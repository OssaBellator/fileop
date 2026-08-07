using System.ComponentModel;
using System.Security.Principal;
using FileOp.Core.Indexing.Service;
using FileOp.Core.Search;
using FileOp.Windows.Ntfs;

namespace FileOp.Windows.IndexingService;

public sealed class NtfsIndexingServiceBackend : IIndexingServiceBackend
{
    private const int ErrorAccessDenied = 5;
    private const int MaximumSearchLimit = 2_000;

    private readonly string _databaseDirectory;
    private readonly Dictionary<string, VolumeContext> _contexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _contextGate = new();
    private bool _disposed;

    public NtfsIndexingServiceBackend(string databaseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseDirectory);
        _databaseDirectory = Path.GetFullPath(databaseDirectory);
        Directory.CreateDirectory(_databaseDirectory);
    }

    public ValueTask<IndexingHelloResponse> HelloAsync(
        IndexingHelloRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        var version = typeof(NtfsIndexingServiceBackend).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
        return ValueTask.FromResult(new IndexingHelloResponse(
            IndexingServiceProtocol.CurrentVersion,
            "FileOp.Indexer",
            version,
            IsProcessElevated()));
    }

    public async ValueTask<IndexingGetVolumesResponse> GetVolumesAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var descriptors = new List<IndexingVolumeDescriptor>();
        foreach (var volume in DiscoverVolumes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = GetOrCreateContext(volume);
            descriptors.Add(await CreateDescriptorAsync(context, cancellationToken).ConfigureAwait(false));
        }

        return new IndexingGetVolumesResponse(descriptors);
    }

    public async ValueTask<IndexingServiceStatusResponse> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var volumes = await GetVolumesAsync(cancellationToken).ConfigureAwait(false);
        return new IndexingServiceStatusResponse(
            volumes.Volumes.Sum(static volume => volume.IndexedItemCount),
            volumes.Volumes.Count,
            volumes.Volumes.Count(static volume =>
                string.Equals(volume.State, VolumeState.Rebuilding.ToString(), StringComparison.Ordinal) ||
                string.Equals(volume.State, VolumeState.Syncing.ToString(), StringComparison.Ordinal)),
            volumes.Volumes);
    }

    public async ValueTask<IndexingVolumeOperationResponse> RebuildVolumeAsync(
        IndexingVolumeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = ResolveContext(request);
        if (!await context.OperationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new IndexingServiceException(
                IndexingServiceErrorCode.Busy,
                $"{context.Volume.RootPath} is already performing an indexing operation.",
                canRetry: true);
        }

        try
        {
            context.State = VolumeState.Rebuilding;
            context.LastError = null;
            var indexer = new NtfsSnapshotIndexer(context.Index, context.Index);
            var checkpoint = await indexer.RebuildAsync(context.Volume, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            context.State = VolumeState.Idle;
            return CreateOperationResponse(context, checkpoint);
        }
        catch (Exception exception)
        {
            throw TranslateOperationException(context, exception, "rebuild");
        }
        finally
        {
            if (context.State is VolumeState.Rebuilding or VolumeState.Syncing)
            {
                context.State = VolumeState.Idle;
            }

            context.OperationGate.Release();
        }
    }

    public async ValueTask<IndexingVolumeOperationResponse> SyncVolumeAsync(
        IndexingVolumeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = ResolveContext(request);
        if (!await context.OperationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new IndexingServiceException(
                IndexingServiceErrorCode.Busy,
                $"{context.Volume.RootPath} is already performing an indexing operation.",
                canRetry: true);
        }

        try
        {
            context.State = VolumeState.Syncing;
            context.LastError = null;
            var sourceKey = NtfsIndexSynchronizer.CreateSourceKey(context.Volume);
            var saved = await context.Index.GetCheckpointAsync(sourceKey, cancellationToken).ConfigureAwait(false);
            if (saved is null)
            {
                throw new IndexingServiceException(
                    IndexingServiceErrorCode.SnapshotRequired,
                    $"{context.Volume.RootPath} has no durable NTFS checkpoint. Build a fresh snapshot first.",
                    canRetry: true);
            }

            var checkpoint = new NtfsJournalCheckpoint(saved.Generation, saved.Position);
            var synchronizer = new NtfsIndexSynchronizer(context.NamespaceStore);
            var next = await synchronizer.ApplyNextBatchAsync(context.Volume, checkpoint, cancellationToken)
                .ConfigureAwait(false);
            context.State = VolumeState.Idle;
            return CreateOperationResponse(context, next);
        }
        catch (Exception exception)
        {
            throw TranslateOperationException(context, exception, "synchronize");
        }
        finally
        {
            if (context.State is VolumeState.Rebuilding or VolumeState.Syncing)
            {
                context.State = VolumeState.Idle;
            }

            context.OperationGate.Release();
        }
    }

    public async ValueTask<IndexingSearchResponse> SearchAsync(
        IndexingSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        var limit = Math.Clamp(request.Limit, 1, MaximumSearchLimit);
        var query = FileSearchQuery.Parse(request.Query ?? string.Empty, limit);
        var contexts = DiscoverVolumes().Select(GetOrCreateContext).ToArray();
        if (contexts.Length == 0)
        {
            return new IndexingSearchResponse([]);
        }

        var searchTasks = contexts.Select(async context =>
            await context.Index.SearchAsync(query, cancellationToken).ConfigureAwait(false)).ToArray();
        var perVolumeResults = await Task.WhenAll(searchTasks).ConfigureAwait(false);

        var merged = perVolumeResults
            .SelectMany(static result => result)
            .OrderBy(static record => record.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static record => record.Path, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(IndexingSearchResult.FromRecord)
            .ToArray();

        return new IndexingSearchResponse(merged);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_contextGate)
        {
            foreach (var context in _contexts.Values)
            {
                context.Dispose();
            }

            _contexts.Clear();
        }
    }

    private IReadOnlyList<NtfsVolume> DiscoverVolumes()
    {
        ThrowIfDisposed();
        return NtfsVolumeDiscovery.GetVolumes();
    }

    private VolumeContext ResolveContext(IndexingVolumeRequest request)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RootPath);
        var requestedRoot = NormalizeRoot(request.RootPath);
        var volume = DiscoverVolumes().FirstOrDefault(candidate =>
            candidate.VolumeIdentity == request.VolumeIdentity &&
            string.Equals(NormalizeRoot(candidate.RootPath), requestedRoot, StringComparison.OrdinalIgnoreCase));

        if (volume is null)
        {
            throw new IndexingServiceException(
                IndexingServiceErrorCode.VolumeNotFound,
                $"NTFS volume {request.RootPath} ({request.VolumeIdentity:X16}) is not currently available.",
                canRetry: true);
        }

        return GetOrCreateContext(volume);
    }

    private VolumeContext GetOrCreateContext(NtfsVolume volume)
    {
        var key = CreateVolumeKey(volume);
        lock (_contextGate)
        {
            if (_contexts.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var databasePath = Path.Combine(_databaseDirectory, $"{key}.sqlite");
            var context = new VolumeContext(volume, databasePath);
            _contexts.Add(key, context);
            return context;
        }
    }

    private async ValueTask<IndexingVolumeDescriptor> CreateDescriptorAsync(
        VolumeContext context,
        CancellationToken cancellationToken)
    {
        var sourceKey = NtfsIndexSynchronizer.CreateSourceKey(context.Volume);
        var checkpoint = await context.Index.GetCheckpointAsync(sourceKey, cancellationToken).ConfigureAwait(false);
        return new IndexingVolumeDescriptor(
            context.Volume.VolumeIdentity,
            context.Volume.RootPath,
            context.Volume.Label,
            context.Index.Count,
            checkpoint is not null,
            context.State.ToString(),
            context.LastError);
    }

    private static IndexingVolumeOperationResponse CreateOperationResponse(
        VolumeContext context,
        NtfsJournalCheckpoint checkpoint) =>
        new(
            context.Volume.VolumeIdentity,
            context.Volume.RootPath,
            context.Index.Count,
            checkpoint.JournalId,
            checkpoint.NextUsn,
            context.State.ToString());

    private static Exception TranslateOperationException(
        VolumeContext context,
        Exception exception,
        string operation)
    {
        if (exception is OperationCanceledException)
        {
            return exception;
        }

        if (exception is IndexingServiceException serviceException)
        {
            context.LastError = serviceException.Message;
            return serviceException;
        }

        if (exception is NtfsIndexResnapshotRequiredException)
        {
            context.LastError = exception.Message;
            return new IndexingServiceException(
                IndexingServiceErrorCode.SnapshotRequired,
                exception.Message,
                canRetry: true,
                exception);
        }

        if (exception is UnauthorizedAccessException ||
            exception is Win32Exception { NativeErrorCode: ErrorAccessDenied })
        {
            var message = $"Administrative NTFS access is required to {operation} {context.Volume.RootPath}.";
            context.LastError = message;
            return new IndexingServiceException(
                IndexingServiceErrorCode.ElevationRequired,
                message,
                canRetry: true,
                exception);
        }

        context.LastError = exception.Message;
        return new IndexingServiceException(
            IndexingServiceErrorCode.InternalError,
            $"Could not {operation} {context.Volume.RootPath}: {exception.Message}",
            canRetry: false,
            exception);
    }

    private static string CreateVolumeKey(NtfsVolume volume)
    {
        var root = NormalizeRoot(volume.RootPath);
        var rootToken = new string(root.Where(static character => char.IsLetterOrDigit(character)).ToArray());
        if (string.IsNullOrEmpty(rootToken))
        {
            rootToken = "root";
        }

        return $"ntfs-{volume.VolumeIdentity:X16}-{rootToken.ToLowerInvariant()}";
    }

    private static string NormalizeRoot(string rootPath) =>
        Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private static bool IsProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private enum VolumeState
    {
        Idle,
        Rebuilding,
        Syncing,
    }

    private sealed class VolumeContext : IDisposable
    {
        public VolumeContext(NtfsVolume volume, string databasePath)
        {
            Volume = volume;
            Index = new SqliteFileIndex(databasePath);
            NamespaceStore = new NtfsSqliteNamespaceStore(databasePath);
        }

        public NtfsVolume Volume { get; }

        public SqliteFileIndex Index { get; }

        public NtfsSqliteNamespaceStore NamespaceStore { get; }

        public SemaphoreSlim OperationGate { get; } = new(1, 1);

        public VolumeState State { get; set; }

        public string? LastError { get; set; }

        public void Dispose()
        {
            NamespaceStore.Dispose();
            Index.Dispose();
            OperationGate.Dispose();
        }
    }
}
