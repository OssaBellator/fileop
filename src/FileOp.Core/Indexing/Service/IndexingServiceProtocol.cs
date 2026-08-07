using System.Text.Json;
using FileOp.Core.Models;

namespace FileOp.Core.Indexing.Service;

public static class IndexingServiceProtocol
{
    public const int CurrentVersion = 1;
    public const int MaximumFrameBytes = 8 * 1024 * 1024;
}

public enum IndexingServiceOperation
{
    Hello,
    GetVolumes,
    GetStatus,
    RebuildVolume,
    SyncVolume,
    Search,
}

public enum IndexingServiceErrorCode
{
    None,
    InvalidRequest,
    ProtocolMismatch,
    VolumeNotFound,
    SnapshotRequired,
    ElevationRequired,
    Busy,
    InternalError,
}

public sealed record IndexingServiceRequest(
    int ProtocolVersion,
    Guid RequestId,
    IndexingServiceOperation Operation,
    JsonElement Payload);

public sealed record IndexingServiceResponse(
    int ProtocolVersion,
    Guid RequestId,
    bool Success,
    JsonElement Payload,
    IndexingServiceError? Error = null);

public sealed record IndexingServiceError(
    IndexingServiceErrorCode Code,
    string Message,
    bool CanRetry = false);

public sealed record IndexingHelloRequest(string ClientName);

public sealed record IndexingHelloResponse(
    int ProtocolVersion,
    string ServiceName,
    string ServiceVersion,
    bool IsElevated);

public sealed record IndexingVolumeDescriptor(
    ulong VolumeIdentity,
    string RootPath,
    string Label,
    int IndexedItemCount,
    bool HasCheckpoint,
    string State,
    string? LastError = null);

public sealed record IndexingGetVolumesResponse(IReadOnlyList<IndexingVolumeDescriptor> Volumes);

public sealed record IndexingServiceStatusResponse(
    int IndexedItemCount,
    int VolumeCount,
    int BusyVolumeCount,
    IReadOnlyList<IndexingVolumeDescriptor> Volumes);

public sealed record IndexingVolumeRequest(ulong VolumeIdentity);

public sealed record IndexingVolumeOperationResponse(
    ulong VolumeIdentity,
    string RootPath,
    int IndexedItemCount,
    ulong JournalId,
    long NextUsn,
    string State);

public sealed record IndexingSearchRequest(string Query, int Limit = 250);

public sealed record IndexingSearchResponse(IReadOnlyList<IndexingSearchResult> Results);

public sealed record IndexingSearchResult(
    string Path,
    string Name,
    string ParentPath,
    string Extension,
    long Length,
    long? AllocatedLength,
    bool IsDirectory,
    DateTimeOffset LastWriteTime,
    FileAttributes Attributes,
    FileIdentity? Identity,
    FileIdentity? ParentIdentity)
{
    public static IndexingSearchResult FromRecord(FileRecord record) => new(
        record.Path,
        record.Name,
        record.ParentPath,
        record.Extension,
        record.Length,
        record.AllocatedLength,
        record.IsDirectory,
        record.LastWriteTime,
        record.Attributes,
        record.Identity,
        record.ParentIdentity);
}
