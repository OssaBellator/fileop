using System.Text.Json;
using FileOp.Core.Models;
using FileOp.Core.Storage;

namespace FileOp.Core.Indexing.Service;

public static class IndexingServiceProtocol
{
    public const int CurrentVersion = 5;
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
    AnalyzeStorage,
    AnalyzeStorageTypes,
    CaptureStorageHistory,
    GetStorageHistory,
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
    ResponseTooLarge,
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

// The provider volume token is stable for the physical volume when Windows exposes a
// volume GUID, but requests still carry the current root path because all persisted
// namespace paths are absolute and a drive-letter change requires a fresh path snapshot.
public sealed record IndexingVolumeRequest(ulong VolumeIdentity, string RootPath);

public sealed record IndexingVolumeOperationResponse(
    ulong VolumeIdentity,
    string RootPath,
    int IndexedItemCount,
    ulong JournalId,
    long NextUsn,
    string State);

public sealed record IndexingSearchRequest(string Query, int Limit = 250);

public sealed record IndexingSearchResponse(IReadOnlyList<IndexingSearchResult> Results);

public sealed record IndexingStorageAnalysisRequest(
    ulong VolumeIdentity,
    string VolumeRootPath,
    string DirectoryPath,
    int MaxEntries = 256);

public sealed record IndexingStorageAnalysisResponse(StorageDirectoryAnalysis Analysis);

public sealed record IndexingStorageFileTypeRequest(
    ulong VolumeIdentity,
    string VolumeRootPath,
    string DirectoryPath,
    int MaxTypes = 128);

public sealed record IndexingStorageFileTypeResponse(StorageFileTypeAnalysis Analysis);

public sealed record IndexingStorageHistoryCaptureRequest(
    ulong VolumeIdentity,
    string VolumeRootPath,
    string DirectoryPath);

public sealed record IndexingStorageHistoryCaptureResponse(StorageHistorySnapshot Snapshot);

public sealed record IndexingStorageHistoryQueryRequest(
    ulong VolumeIdentity,
    string VolumeRootPath,
    string DirectoryPath,
    int Limit = 90);

public sealed record IndexingStorageHistoryQueryResponse(
    IReadOnlyList<StorageHistorySnapshot> Snapshots);

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