# FileOp indexing service boundary

## Purpose

`FileOp.Indexer` isolates native NTFS indexing from the WinUI desktop process. `FileOp.App` always remains `asInvoker`; only the on-demand indexing helper may be relaunched after explicit user action when raw NTFS access requires elevation.

The indexer is not the future disk-administration helper. It exposes indexing, search, read-only Storage analytics and aggregate Storage-history capture/query. Partition changes, formatting, BitLocker administration and other destructive storage operations require a separate privileged surface.

## Process model

```text
FileOp.App (normal user token + known PID)
        |
        | native-first Search/Storage coordinator
        |   \-- bounded user-profile crawler fallback
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
        +-- Search + directory/type/category analytics
        +-- aggregate Storage history capture/query
```

The helper accepts one authenticated client and exits when that desktop session disconnects or its launching process exits. It is not an always-running Windows service and is never permanently elevated.

## IPC security and framing

Every helper launch uses a cryptographically random pipe name. The pipe uses `PipeOptions.CurrentUserOnly`, and the server additionally verifies `GetNamedPipeClientProcessId` equals the exact desktop PID supplied at launch.

Messages use a four-byte little-endian length prefix plus UTF-8 JSON. Frames are capped at 8 MiB before allocation. Requests carry protocol version, non-empty request ID, operation and typed payload. Responses echo protocol version and request ID. Required fields are validated after deserialization.

Oversized responses are replaced by retryable `ResponseTooLarge` without killing the pipe. An interrupted exchange is different: once a request may have been written without its complete response being consumed, the client faults that session rather than risk request/response desynchronization. UI supersession therefore discards stale work instead of cancelling transmitted IPC.

Protocol mismatch is rejected before backend work.

## Protocol v5

Current operations are:

- `Hello`;
- `GetVolumes`;
- `GetStatus`;
- `RebuildVolume`;
- `SyncVolume`;
- `Search`;
- `AnalyzeStorage`;
- `AnalyzeStorageTypes`;
- `CaptureStorageHistory`;
- `GetStorageHistory`.

Version progression is deliberate:

- v1 established indexing/search;
- v2 added directory Storage;
- v3 added file-type analysis;
- v4 enriched `AnalyzeStorageTypes` with exact category rows;
- v5 adds aggregate history capture/query.

The v5 bump is required because old desktop/helper binaries do not agree on the operation set. Strict negotiation fails safely instead of allowing an old helper to receive unknown history operations.

There are no cleanup, file-mutation, partition, format, TRIM, BitLocker or process-management commands in this protocol.

## Live Storage semantics

`AnalyzeStorage` and `AnalyzeStorageTypes` identify `VolumeIdentity`, current absolute volume root and a target directory contained by that root. A durable NTFS checkpoint must be valid before either live read is served.

`AnalyzeStorage` returns bounded direct-entry rows with recursive logical/allocated bytes, file/directory counts and hard-link accounting. Complete root totals are calculated before `MaxEntries` truncation.

`AnalyzeStorageTypes` returns complete root totals, bounded extension groups and exact category aggregates. `MaxTypes` bounds only the extension rows; categories are calculated over the complete extension aggregate set.

Hard links preserve namespace meaning without double-counting physical allocation. Unknown physical metadata remains unknown instead of being replaced with logical size.

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

- a process-local non-blocking operation gate;
- a cross-process reader/writer file gate beside the persistent database.

Search and live Storage analytics take shared leases. Rebuild and journal synchronization hold an exclusive maintenance lease across the entire semantic operation, including checkpoint invalidation.

History persistence uses an independently versioned `storage_history_*` sub-schema in the same database. Namespace rebuilds do not erase history. History root identity uses a custom ordinal-ignore-case SQLite collation so Unicode Windows path casing follows the same semantics as the .NET domain model rather than SQLite's ASCII-only `NOCASE`.

The history-aware service wrapper deliberately leaves the reviewed `NtfsIndexingServiceBackend` lifecycle unchanged. Existing operations are delegated directly.

## Privilege policy

The helper starts unelevated. Access denied from native NTFS operations maps to structured `ElevationRequired`.

Helper-only UAC is allowed only for a limited split administrator token, preserving the same Windows user identity. Credential-over-the-shoulder elevation from a standard user remains unsupported until a separately reviewed Windows service/ACL model exists.

The desktop build places `FileOp.Indexer.exe` and its host metadata beside the application. Runtime resolution accepts only the exact adjacent non-reparse helper. This narrows path resolution but is not a substitute for Authenticode publisher verification before production elevation.

## WinUI integration

Search, Folders Storage and Types/Categories Storage already share one native/fallback lifecycle and foreground operation discipline.

Protocol v5 history operations are service-ready but are not yet scheduled or rendered by WinUI. The next UI/lifecycle slice should request low-priority hourly-bucket captures without cancelling in-flight IPC, then query a bounded history series for a read-only growth timeline.

Fallback history policy is intentionally not defined yet. Native history represents durable indexed volume state; a crawler fallback is a static profile snapshot and should not silently be mixed into the same time series without an explicit source model.

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

`CaptureStorageHistory` inherits live-analysis errors such as `SnapshotRequired` and `Busy`. `GetStorageHistory` can return `VolumeNotFound` for a detached/mismatched volume but does not require a current checkpoint.

## Validation without hosted Actions

The existing Storage SQL/UI/history verifiers remain in the local gate. Protocol v5 adds `tools/verify_storage_history_service.py`, which checks:

- 2,000+ randomized UTC-hour bucket cases;
- v5 DTO/client/dispatcher wiring;
- no client-controlled capture timestamp;
- capture live-analysis-before-persistence ordering;
- history-query independence from live checkpoint analysis;
- production host selection;
- deterministic database-key parity with the native backend.

`IndexingStorageHistoryProtocolTests` adds a typed named-pipe capture/query round trip and dispatcher limit validation for the full Windows/.NET test gate.

`tools/test-local.ps1` runs all offline verifiers before Core/native/indexer/tests/WinUI/bundled-helper compilation and the real process handshake without consuming GitHub Actions usage.