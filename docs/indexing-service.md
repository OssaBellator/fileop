# FileOp indexing service boundary

## Purpose

`FileOp.Indexer` isolates native NTFS indexing from the WinUI desktop process. `FileOp.App` always remains `asInvoker`; only the on-demand indexing helper may be relaunched after explicit user action when raw NTFS access requires elevation.

The indexer is not the future disk-administration helper. It exposes indexing, search, exact read-only directory browsing, read-only Storage analytics, read-only optimization analysis and aggregate Storage-history capture/query. Partition changes, formatting, BitLocker administration and other destructive storage operations require a separate privileged surface.

## Process model

```text
FileOp.App (normal user token + known PID)
        |
        | native-first Search/Files/Storage coordinator
        |   \-- completed user-profile crawler fallback
        |
        | random named pipe
        | current-user ACL + exact client-PID check
        v
FileOp.Indexer (normal token first; same-account elevation only)
        |
        +-- NTFS discovery / MFT + USN access
        +-- metadata hydration / hard-link enumeration
        +-- journal synchronization
        +-- per-volume SQLite ownership
        +-- Search + paged direct-child browsing
        +-- directory/type/category analytics
        +-- read-only storage optimization analysis
        +-- aggregate Storage history capture/query
```

The helper accepts one authenticated client and exits when that desktop session disconnects or its launching process exits. It is not an always-running Windows service and is never permanently elevated.

## IPC security and framing

Every helper launch uses a cryptographically random pipe name. The pipe uses `PipeOptions.CurrentUserOnly`, and the server additionally verifies `GetNamedPipeClientProcessId` equals the exact desktop PID supplied at launch.

Messages use a four-byte little-endian length prefix plus UTF-8 JSON. Frames are capped at 8 MiB before allocation. Requests carry protocol version, non-empty request ID, operation and typed payload. Responses echo protocol version and request ID. Required fields are validated after deserialization.

Oversized responses are replaced by retryable `ResponseTooLarge` without killing the pipe. An interrupted exchange is different: once a request may have been written without its complete response being consumed, the client faults that session rather than risk request/response desynchronization. UI supersession therefore discards stale work instead of cancelling transmitted IPC.

Protocol mismatch is rejected before backend work.

## Protocol v7

Current operations are:

- `Hello`;
- `GetVolumes`;
- `GetStatus`;
- `RebuildVolume`;
- `SyncVolume`;
- `Search`;
- `BrowseDirectory`;
- `AnalyzeStorage`;
- `AnalyzeStorageTypes`;
- `AnalyzeStorageOptimization`;
- `CaptureStorageHistory`;
- `GetStorageHistory`.

Version progression is deliberate:

- v1 established indexing/search;
- v2 added directory Storage;
- v3 added file-type analysis;
- v4 enriched `AnalyzeStorageTypes` with exact category rows;
- v5 added aggregate history capture/query;
- v6 added exact paged direct-child browsing;
- v7 adds read-only storage optimization analysis.

The v7 bump is required because old desktop/helper binaries do not agree on the operation set. Strict negotiation fails safely instead of allowing an old helper to advertise compatibility with an optimization operation it cannot serve.

There are no cleanup, file-mutation, partition, format, TRIM, BitLocker or process-management commands in this protocol.

## Exact directory browsing

### `BrowseDirectory`

Request:

```text
VolumeIdentity
VolumeRootPath
DirectoryPath
PageSize   // default 256, maximum 1024 at the service boundary
Cursor?    // IsDirectory + Name + absolute Path
```

Response:

```text
DirectoryPath
TotalCount
Entries[]
NextCursor?
```

The result contains direct `FileRecord` metadata represented with the same `IndexingSearchResult` DTO used by Search. It intentionally does **not** calculate recursive directory sizes or category aggregates; those are Storage responsibilities.

Native ordering is:

```text
directories first
name_norm ascending
path_norm ascending
```

Continuation is a keyset cursor over the last returned row. The SQL predicate advances by directory/file rank and then normalized name/path rather than by numeric offset. This avoids the classic offset-page shift where an insertion before the current page can make an already-consumed row reappear on the next page.

The SQLite browse class opens the index in `ReadOnly` mode and never runs schema creation or mutation. When an indexed directory has a stable `FileIdentity`, direct-child filtering uses the existing parent-identity columns; the path predicate is only a fallback for indexes where that identity is unavailable.

The production `PagedDirectoryIndexingServiceBackend` remains a thin wrapper around `StorageHistoryIndexingServiceBackend`, and protocol v7 adds `StorageOptimizationIndexingServiceBackend` as the outer composition layer. Existing index/search/storage/history/browse behavior is delegated unchanged. Browse requests independently:

1. resolve the attached physical NTFS volume/root;
2. validate directory containment;
3. take the existing shared cross-process read lease;
4. verify the durable NTFS checkpoint using a read-only query;
5. verify the requested path is an indexed directory;
6. execute the keyset page query;
7. translate SQLite contention to retryable `Busy`.

This keeps the reviewed `NtfsIndexingServiceBackend` synchronization lifecycle untouched.

The desktop exposes the same page model in fallback mode by filtering and paging the already-completed in-memory profile snapshot. That compatibility path can scan the in-memory snapshot for each page, but it performs no new filesystem enumeration. Native mode does not materialize the whole persistent index.

## Live Storage semantics

`AnalyzeStorage` and `AnalyzeStorageTypes` identify `VolumeIdentity`, current absolute volume root and a target directory contained by that root. A durable NTFS checkpoint must be valid before either live read is served.

`AnalyzeStorage` returns bounded direct-entry rows with recursive logical/allocated bytes, file/directory counts and hard-link accounting. Complete root totals are calculated before `MaxEntries` truncation.

`AnalyzeStorageTypes` returns complete root totals, bounded extension groups and exact category aggregates. `MaxTypes` bounds only the extension rows; categories are calculated over the complete extension aggregate set.

Hard links preserve namespace meaning without double-counting physical allocation. Unknown physical metadata remains unknown instead of being replaced with logical size.

## Storage optimization operation

### `AnalyzeStorageOptimization`

Request:

```text
VolumeIdentity
VolumeRootPath
DirectoryPath
```

The first protocol-v7 optimization policy is service-owned and explicit in the returned `StorageOptimizationAnalysis`. It surfaces bounded sets of large files, old large files and same-size physical-file candidate groups from the existing metadata index.

`StorageOptimizationIndexingServiceBackend` is the outer production wrapper. For optimization requests it:

1. resolves the attached physical NTFS volume/root;
2. validates directory containment;
3. takes the same shared cross-process read lease used by other live indexed reads;
4. verifies the durable NTFS checkpoint;
5. verifies that the requested path is an indexed directory;
6. opens the SQLite index read-only with `query_only` enabled;
7. runs bounded metadata queries; and
8. translates SQLite contention to retryable `Busy`.

Hard-link aliases are collapsed using stable volume/file-reference identity before physical candidates are selected. When identity is missing, the path remains a conservative distinct candidate.

Largest-file ranking uses allocated bytes when known and logical bytes otherwise. The stale list is an explicit last-write-age filter over large candidates; age is not interpreted as permission to remove a file.

Same-size groups use exact logical length only. They are a duplicate **prefilter**, not content-equality evidence. Potential savings is reported as a logical upper bound of `length × (candidate count - 1)` and must not be described as guaranteed reclaimable space until a later content-verification layer confirms equality.

The initial desktop Optimize view is native-only. A bounded profile crawler snapshot is not presented as complete volume reclaim analysis.

The operation does not hash contents and has no mutation capability.

## Storage history operations

### `CaptureStorageHistory`

Request:

```text
VolumeIdentity
VolumeRootPath
DirectoryPath
```

The request intentionally has no timestamp. Capture time belongs to the service and is canonicalized to the current UTC-hour boundary.

The production `StorageHistoryIndexingServiceBackend` wraps the reviewed native backend. It first calls `AnalyzeStorageTypes` with `MaxTypes = 1`. That call performs the trusted live read: attached-volume resolution, directory containment, process-local operation gate, shared cross-process read lease, durable-checkpoint validation and full exact-category aggregation.

Only after that live aggregate succeeds is it persisted to the same deterministic per-volume SQLite database. The aggregate is immutable after materialization, so the separate history transaction does not extend the NTFS semantic read lease. A rebuild that starts afterward cannot retroactively change the captured values.

Repeated captures in the same UTC hour use the persistence layer's same-root/same-timestamp upsert and replace the same bucket.

### `GetStorageHistory`

Request:

```text
VolumeIdentity
VolumeRootPath
DirectoryPath
Limit   // default 90, maximum 4096
```

History query requires the physical volume/root to remain attached and the requested directory to remain inside that root, but it intentionally does **not** require a current durable namespace checkpoint. Previously captured observations remain trustworthy and readable even when the live index later requires a rebuild.

The query reads only `storage_history_*`; it does not run live analytics and does not rescan the filesystem.

Detached-volume history browsing is not implemented yet.

## Per-volume persistence and concurrency

Each NTFS volume/root pair has its own SQLite database under the current Windows user's `LocalApplicationData\FileOp\Index`. Requests carry both provider volume identity and current root because persisted namespace paths are absolute.

Live namespace reads have two coordination layers:

- a process-local desktop/native operation discipline;
- a cross-process reader/writer file gate beside the persistent database.

Search, exact browse, live Storage analytics and optimization analysis take shared leases. Rebuild and journal synchronization hold an exclusive maintenance lease across the entire semantic operation, including checkpoint invalidation.

History persistence uses an independently versioned `storage_history_*` sub-schema in the same database. Namespace rebuilds do not erase history. History root identity uses a custom ordinal-ignore-case SQLite collation so Unicode Windows path casing follows the same semantics as the .NET domain model rather than SQLite's ASCII-only `NOCASE`.

The service composition deliberately leaves the reviewed `NtfsIndexingServiceBackend` lifecycle unchanged. History, exact-browse and optimization wrappers delegate existing operations directly.

## Privilege policy

The helper starts unelevated. Access denied from native NTFS operations maps to structured `ElevationRequired`.

Helper-only UAC is allowed only for a limited split administrator token, preserving the same Windows user identity. Credential-over-the-shoulder elevation from a standard user remains unsupported until a separately reviewed Windows service/ACL model exists.

The desktop build places `FileOp.Indexer.exe` and its host metadata beside the application. Runtime resolution accepts only the exact adjacent non-reparse helper. This narrows path resolution but is not a substitute for Authenticode publisher verification before production elevation.

## WinUI integration

Search, indexed Files and Storage share one native/fallback lifecycle and foreground operation discipline. Storage history scheduling is engine-owned and independent of whether its view is open.

Files consumes `DesktopSearchEngine.BrowseDirectoryAsync` pages incrementally with explicit continuation behavior. Storage Folders/Types continue to use recursive aggregate analysis, History uses persisted hourly observations, and Optimize uses the protocol-v7 read-only optimization analysis. Optimize invalidates stale loads when the user changes Storage mode so a completed background result cannot overwrite the newly selected view.

## Error contract

Expected errors remain structured:

- `InvalidRequest`;
- `ProtocolMismatch`;
- `VolumeNotFound`;
- `SnapshotRequired`;
- `ElevationRequired`;
- `Busy`;
- `ResponseTooLarge`;
- `InternalError`.

`BrowseDirectory` and `AnalyzeStorageOptimization` require an attached volume and current durable checkpoint and can return `Busy`, `SnapshotRequired`, `InvalidRequest` or `ResponseTooLarge`. `CaptureStorageHistory` inherits live-analysis errors such as `SnapshotRequired` and `Busy`. `GetStorageHistory` can return `VolumeNotFound` for a detached/mismatched volume but does not require a current checkpoint.

## Validation without hosted Actions

`tools/verify_directory_browse.py` exercises the keyset algorithm against in-memory SQLite over randomized directory fixtures and guards:

- exact multi-page reconstruction without duplicates;
- directory-first/name/path ordering;
- cursor behavior when rows are inserted before an already-consumed cursor;
- protocol-v7 DTO/client/dispatcher wiring while preserving the v6 browse contract;
- read-only SQLite mode;
- shared read-lease and checkpoint validation;
- wrapper isolation from the reviewed native synchronization backend;
- native/fallback desktop routing and no-rescan behavior.

`tools/verify_storage_optimization.py` exercises hard-link collapse, measured-size ranking, stale filtering and same-size upper-bound semantics over randomized inventories. Repository mode additionally guards the protocol/client/backend/Desktop/WinUI wiring, read-only boundary, safety disclaimers and stale-load invalidation.

`IndexingDirectoryBrowseProtocolTests` preserves dispatcher page-size validation and typed named-pipe browse/cursor round trips. `StorageOptimizationAnalyticsTests` covers SQLite ranking/scope/hard-link semantics, and `IndexingStorageOptimizationProtocolTests` covers protocol-v7 path normalization and typed named-pipe optimization evidence.

The existing Storage/history/UI verifiers remain in the local gate. `tools/test-local.ps1 -OfflineOnly` runs all standard-library checks without requiring the .NET SDK; the normal local gate continues through Core/native/indexer/tests/WinUI/bundled-helper compilation and the real process handshake without consuming GitHub Actions usage.
