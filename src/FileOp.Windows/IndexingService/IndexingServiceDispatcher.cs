using System.Text.Json;
using FileOp.Core.Indexing.Service;

namespace FileOp.Windows.IndexingService;

public sealed class IndexingServiceDispatcher
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IIndexingServiceBackend _backend;

    public IndexingServiceDispatcher(IIndexingServiceBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public async ValueTask<IndexingServiceResponse> DispatchAsync(
        IndexingServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequestId == Guid.Empty)
        {
            return Failure(
                Guid.Empty,
                IndexingServiceErrorCode.InvalidRequest,
                "An indexing service request must include a non-empty request ID.");
        }

        if (request.ProtocolVersion != IndexingServiceProtocol.CurrentVersion)
        {
            return Failure(
                request.RequestId,
                IndexingServiceErrorCode.ProtocolMismatch,
                $"Client protocol {request.ProtocolVersion} is not supported. " +
                $"FileOp.Indexer requires protocol {IndexingServiceProtocol.CurrentVersion}.");
        }

        try
        {
            return request.Operation switch
            {
                IndexingServiceOperation.Hello => Success(
                    request.RequestId,
                    await _backend.HelloAsync(
                        DeserializeHello(request.Payload),
                        cancellationToken).ConfigureAwait(false)),

                IndexingServiceOperation.GetVolumes => Success(
                    request.RequestId,
                    await _backend.GetVolumesAsync(cancellationToken).ConfigureAwait(false)),

                IndexingServiceOperation.GetStatus => Success(
                    request.RequestId,
                    await _backend.GetStatusAsync(cancellationToken).ConfigureAwait(false)),

                IndexingServiceOperation.GetIndexDiagnostics => Success(
                    request.RequestId,
                    await _backend.GetIndexDiagnosticsAsync(
                        DeserializeIndexDiagnostics(request.Payload),
                        cancellationToken).ConfigureAwait(false)),

                IndexingServiceOperation.RebuildVolume => Success(
                    request.RequestId,
                    await _backend.RebuildVolumeAsync(
                        DeserializeVolumeRequest(request.Payload),
                        cancellationToken).ConfigureAwait(false)),

                IndexingServiceOperation.SyncVolume => Success(
                    request.RequestId,
                    await _backend.SyncVolumeAsync(
                        DeserializeVolumeRequest(request.Payload),
                        cancellationToken).ConfigureAwait(false)),

                IndexingServiceOperation.Search => Success(
                    request.RequestId,
                    await _backend.SearchAsync(
                        DeserializeSearch(request.Payload),
                        cancellationToken).ConfigureAwait(false)),

                IndexingServiceOperation.BrowseDirectory => Success(
                    request.RequestId,
                    await _backend.BrowseDirectoryAsync(
                        DeserializeDirectoryBrowse(request.Payload),
                        cancellationToken).ConfigureAwait(false)),

                IndexingServiceOperation.AnalyzeStorage => Success(
                    request.RequestId,
                    await _backend.AnalyzeStorageAsync(
                        DeserializeStorageAnalysis(request.Payload),
                        cancellationToken).ConfigureAwait(false)),

                IndexingServiceOperation.AnalyzeStorageTypes => Success(
                    request.RequestId,
                    await _backend.AnalyzeStorageTypesAsync(
                        DeserializeStorageFileTypes(request.Payload),
                        cancellationToken).ConfigureAwait(false)),

                IndexingServiceOperation.AnalyzeStorageOptimization => Success(
                    request.RequestId,
                    await _backend.AnalyzeStorageOptimizationAsync(
                        DeserializeStorageOptimization(request.Payload),
                        cancellationToken).ConfigureAwait(false)),

                IndexingServiceOperation.CaptureStorageHistory => Success(
                    request.RequestId,
                    await _backend.CaptureStorageHistoryAsync(
                        DeserializeStorageHistoryCapture(request.Payload),
                        cancellationToken).ConfigureAwait(false)),

                IndexingServiceOperation.GetStorageHistory => Success(
                    request.RequestId,
                    await _backend.GetStorageHistoryAsync(
                        DeserializeStorageHistoryQuery(request.Payload),
                        cancellationToken).ConfigureAwait(false)),

                _ => Failure(
                    request.RequestId,
                    IndexingServiceErrorCode.InvalidRequest,
                    $"Unknown indexing service operation {request.Operation}."),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IndexingServiceException exception)
        {
            return Failure(request.RequestId, exception.Code, exception.Message, exception.CanRetry);
        }
        catch (JsonException exception)
        {
            return Failure(
                request.RequestId,
                IndexingServiceErrorCode.InvalidRequest,
                $"The request payload is invalid: {exception.Message}");
        }
        catch (Exception exception)
        {
            return Failure(
                request.RequestId,
                IndexingServiceErrorCode.InternalError,
                $"The indexing service could not complete the request: {exception.Message}");
        }
    }

    private static IndexingHelloRequest DeserializeHello(JsonElement payload)
    {
        var request = Deserialize<IndexingHelloRequest>(payload);
        if (string.IsNullOrWhiteSpace(request.ClientName))
        {
            throw new JsonException("clientName is required.");
        }

        return request;
    }

    private static IndexingIndexDiagnosticsRequest DeserializeIndexDiagnostics(JsonElement payload)
    {
        var request = Deserialize<IndexingIndexDiagnosticsRequest>(payload);
        if (string.IsNullOrWhiteSpace(request.VolumeRootPath))
        {
            throw new JsonException("volumeRootPath is required.");
        }

        if (!Path.IsPathFullyQualified(request.VolumeRootPath))
        {
            throw new JsonException("volumeRootPath must be an absolute path.");
        }

        try
        {
            return request with { VolumeRootPath = Path.GetFullPath(request.VolumeRootPath) };
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            throw new JsonException("volumeRootPath is invalid.", exception);
        }
    }

    private static IndexingVolumeRequest DeserializeVolumeRequest(JsonElement payload)
    {
        var request = Deserialize<IndexingVolumeRequest>(payload);
        if (string.IsNullOrWhiteSpace(request.RootPath))
        {
            throw new JsonException("rootPath is required.");
        }

        if (!Path.IsPathFullyQualified(request.RootPath))
        {
            throw new JsonException("rootPath must be an absolute path.");
        }

        return request;
    }

    private static IndexingSearchRequest DeserializeSearch(JsonElement payload)
    {
        var request = Deserialize<IndexingSearchRequest>(payload);
        if (request.Limit <= 0)
        {
            throw new JsonException("limit must be greater than zero.");
        }

        return request;
    }

    private static IndexingDirectoryBrowseRequest DeserializeDirectoryBrowse(JsonElement payload)
    {
        var request = Deserialize<IndexingDirectoryBrowseRequest>(payload);
        var (volumeRootPath, directoryPath) = NormalizeStoragePaths(
            request.VolumeRootPath,
            request.DirectoryPath);
        if (request.PageSize <= 0 || request.PageSize > 1_024)
        {
            throw new JsonException("pageSize must be between 1 and 1024.");
        }

        var cursor = request.Cursor;
        if (cursor is not null)
        {
            if (string.IsNullOrWhiteSpace(cursor.Name) || string.IsNullOrWhiteSpace(cursor.Path))
            {
                throw new JsonException("Directory browse cursors require both name and path.");
            }

            if (!Path.IsPathFullyQualified(cursor.Path))
            {
                throw new JsonException("Directory browse cursor paths must be absolute paths.");
            }

            try
            {
                var cursorPath = Path.GetFullPath(cursor.Path);
                var cursorParent = Path.GetDirectoryName(cursorPath);
                if (string.IsNullOrWhiteSpace(cursorParent) ||
                    !string.Equals(
                        NormalizeComparablePath(cursorParent),
                        NormalizeComparablePath(directoryPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new JsonException(
                        "The directory browse cursor does not belong to the requested directory.");
                }

                cursor = cursor with { Path = cursorPath };
            }
            catch (JsonException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                NotSupportedException or
                PathTooLongException)
            {
                throw new JsonException("Directory browse cursor paths are invalid.", exception);
            }
        }

        return request with
        {
            VolumeRootPath = volumeRootPath,
            DirectoryPath = directoryPath,
            Cursor = cursor,
        };
    }

    private static IndexingStorageAnalysisRequest DeserializeStorageAnalysis(JsonElement payload)
    {
        var request = Deserialize<IndexingStorageAnalysisRequest>(payload);
        var (volumeRootPath, directoryPath) = NormalizeStoragePaths(
            request.VolumeRootPath,
            request.DirectoryPath);

        if (request.MaxEntries <= 0 || request.MaxEntries > 4_096)
        {
            throw new JsonException("maxEntries must be between 1 and 4096.");
        }

        return request with
        {
            VolumeRootPath = volumeRootPath,
            DirectoryPath = directoryPath,
        };
    }

    private static IndexingStorageFileTypeRequest DeserializeStorageFileTypes(JsonElement payload)
    {
        var request = Deserialize<IndexingStorageFileTypeRequest>(payload);
        var (volumeRootPath, directoryPath) = NormalizeStoragePaths(
            request.VolumeRootPath,
            request.DirectoryPath);

        if (request.MaxTypes <= 0 || request.MaxTypes > 4_096)
        {
            throw new JsonException("maxTypes must be between 1 and 4096.");
        }

        return request with
        {
            VolumeRootPath = volumeRootPath,
            DirectoryPath = directoryPath,
        };
    }

    private static IndexingStorageOptimizationRequest DeserializeStorageOptimization(JsonElement payload)
    {
        var request = Deserialize<IndexingStorageOptimizationRequest>(payload);
        var (volumeRootPath, directoryPath) = NormalizeStoragePaths(
            request.VolumeRootPath,
            request.DirectoryPath);
        return request with
        {
            VolumeRootPath = volumeRootPath,
            DirectoryPath = directoryPath,
        };
    }

    private static IndexingStorageHistoryCaptureRequest DeserializeStorageHistoryCapture(JsonElement payload)
    {
        var request = Deserialize<IndexingStorageHistoryCaptureRequest>(payload);
        var (volumeRootPath, directoryPath) = NormalizeStoragePaths(
            request.VolumeRootPath,
            request.DirectoryPath);
        return request with
        {
            VolumeRootPath = volumeRootPath,
            DirectoryPath = directoryPath,
        };
    }

    private static IndexingStorageHistoryQueryRequest DeserializeStorageHistoryQuery(JsonElement payload)
    {
        var request = Deserialize<IndexingStorageHistoryQueryRequest>(payload);
        var (volumeRootPath, directoryPath) = NormalizeStoragePaths(
            request.VolumeRootPath,
            request.DirectoryPath);
        if (request.Limit <= 0 || request.Limit > 4_096)
        {
            throw new JsonException("limit must be between 1 and 4096.");
        }

        return request with
        {
            VolumeRootPath = volumeRootPath,
            DirectoryPath = directoryPath,
        };
    }

    private static (string VolumeRootPath, string DirectoryPath) NormalizeStoragePaths(
        string? volumeRootPath,
        string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(volumeRootPath))
        {
            throw new JsonException("volumeRootPath is required.");
        }

        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new JsonException("directoryPath is required.");
        }

        if (!Path.IsPathFullyQualified(volumeRootPath) ||
            !Path.IsPathFullyQualified(directoryPath))
        {
            throw new JsonException("volumeRootPath and directoryPath must be absolute paths.");
        }

        try
        {
            return (Path.GetFullPath(volumeRootPath), Path.GetFullPath(directoryPath));
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            throw new JsonException("volumeRootPath or directoryPath is invalid.", exception);
        }
    }

    private static string NormalizeComparablePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(root) &&
            string.Equals(
                fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static T Deserialize<T>(JsonElement payload)
    {
        var value = payload.Deserialize<T>(SerializerOptions);
        return value ?? throw new JsonException($"A {typeof(T).Name} payload is required.");
    }

    private static IndexingServiceResponse Success<T>(Guid requestId, T payload) =>
        new(
            IndexingServiceProtocol.CurrentVersion,
            requestId,
            true,
            JsonSerializer.SerializeToElement(payload, SerializerOptions));

    private static IndexingServiceResponse Failure(
        Guid requestId,
        IndexingServiceErrorCode code,
        string message,
        bool canRetry = false) =>
        new(
            IndexingServiceProtocol.CurrentVersion,
            requestId,
            false,
            JsonSerializer.SerializeToElement<object?>(null, SerializerOptions),
            new IndexingServiceError(code, message, canRetry));
}
