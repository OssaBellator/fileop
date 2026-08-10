# FileOp architecture

## Product boundary

FileOp is a high-performance Windows storage operating layer, not a generic "PC cleaner". Search, browsing, Storage, duplicate discovery, cleanup and performance diagnostics should consume one shared filesystem model instead of launching independent scanners.

The priorities are speed, transparency, safety and user control. Registry cleaners, RAM boosters, opaque health scores, arbitrary service disabling and undocumented Windows-directory deletion are outside the product model.

## Component model

```text
FileOp.App (WinUI, asInvoker)
      |
      | shared Search/Files/Storage/history/Optimize coordinator
      |   \-- completed user-profile crawler fallback (no history/reclaim claims)
      |
      | authenticated versioned named pipe
      v
FileOp.Indexer (on-demand helper)
      |
      +------------------------------+
      |                              |
      v                              v
FileOp.Core                    FileOp.Windows
  |-- domain/query model         |-- NTFS discovery
  |-- IFileIndex                 |-- MFT namespace enumeration
  |-- exact browse page model    |-- USN journal reader/coalescer
  |-- SQLite browse/storage      |-- file-ID metadata hydration
  |-- analytics/history/Optimize |-- hard-link expansion
  |-- performance evidence model |-- namespace synchronization
  |-- service protocol DTOs      |-- service/pipe wrappers
  |-- fallback crawler           |
      |                              |
      +---------------+--------------+
                      v
                per-volume SQLite
          current index + aggregate history

FileOp.Benchmarks
      |
      +---- synthetic Search/Storage baselines
```

`FileOp.Core` is discovery-provider independent. Windows filesystem controls, P/Invoke and helper/pipe implementation remain in `FileOp.Windows`. `FileOp.Indexer` is a thin process host. Destructive disk administration remains a separate future privileged surface.

## Unified filesystem model

A persisted file record can carry stable provider identity in addition to its namespace path:

```text
FileIdentity   = (VolumeIdentity, FileReferenceNumber)
ParentIdentity = (VolumeIdentity, ParentFileReferenceNumber)
```

Path is a namespace attribute, not durable identity. Rename, move and hard-link operations can change names without changing the underlying NTFS file.

Indexed metadata includes path/name/extension, logical and allocated size, timestamps, attributes, provider volume identity and file/parent identity. Expensive metadata such as hashes and content extraction stays lazy.

## NTFS ingestion

The native path uses supported NTFS metadata interfaces rather than recursively opening every directory:

1. discover ready NTFS volumes and stable volume GUID paths when available;
2. open the volume device handle;
3. query journal identity/range;
4. enumerate MFT-backed namespace records with `FSCTL_ENUM_USN_DATA`;
5. reconstruct paths from file/parent references;
6. hydrate logical size, allocation, link count, timestamps and attributes by file ID;
7. expand ordinary multi-name hard links;
8. persist the snapshot plus durable journal checkpoint;
9. consume `FSCTL_READ_USN_JOURNAL` batches;
10. normalize rename/upsert/delete/hard-link-refresh events;
11. commit namespace mutations and checkpoint advancement atomically;
12. require a fresh snapshot on journal discontinuity or inconsistent namespace evidence.

Rename-old/new records may cross read-buffer boundaries, so the coalescer does not advance the durable cursor beyond an unmatched old-name record. Directory renames are subtree moves. Hard-link changes trigger targeted namespace reconciliation.

The current path schema is case-insensitive, matching ordinary Windows behavior. Per-directory case-sensitive NTFS namespaces remain an explicit future capability.

## Volume and database identity

When Windows exposes `\\?\Volume{GUID}\`, FileOp derives the provider volume token from that stable identity; serial-only identity remains a fallback.

Requests also carry the current volume root because persisted namespace paths are absolute. A drive-letter/root change therefore uses a fresh absolute namespace database even when the physical volume identity is stable.

Every NTFS volume/root pair gets one deterministic SQLite file under the current user's `LocalApplicationData\FileOp\Index`. That database contains the current namespace/index plus additive aggregate-history tables.

## Exact directory browsing

The file-manager browse boundary is deliberately separate from Storage analytics. `FileDirectoryBrowsePage` carries:

```text
DirectoryPath
TotalCount
Entries[]
NextCursor?
```

Rows are direct `FileRecord` metadata only. Recursive folder sizes, category totals and treemap weights remain Storage work.

Protocol v6 introduced native paging ordered by:

1. directories before files;
2. normalized name ascending;
3. normalized path ascending as a deterministic tie-breaker.

The continuation cursor stores the last returned row's directory/file kind, name and absolute path. SQLite uses a keyset predicate rather than `OFFSET`. This prevents rows already consumed by the client from being shifted into a later page when another namespace row is inserted before the cursor between requests.

A multi-page browse is intentionally not advertised as one transactionally frozen snapshot. Inserts after the cursor may appear later; inserts before it do not rewind the cursor. Rebuild/sync exclusion is still enforced per page through the existing cross-process lease and durable-checkpoint model.

`SqliteFileDirectoryBrowser` opens SQLite in read-only mode. It first resolves the requested directory identity; when that identity exists, child filtering uses the persisted `(parent_volume_serial, parent_file_reference)` columns and their existing index. Path filtering is a fallback for rows without provider identity.

The desktop fallback uses the completed in-memory crawler snapshot. It can filter/sort that snapshot per page without touching the filesystem again. Native mode is the performance path and never materializes the complete persistent index merely to browse one directory.

The current Files UI consumes `DesktopSearchEngine.BrowseDirectoryAsync` incrementally in 256-entry pages. It exposes continuation only while the service returns a cursor and does not reuse recursive Storage rows as file-browser data.

## Shared Storage analytics

Storage analytics reads the same indexed rows as Search and Files. It never launches another recursive filesystem scan merely to calculate sizes, types or categories.

`IStorageAnalytics` exposes:

- `AnalyzeDirectoryAsync` — recursive direct-child folder/file aggregates for drill-down and treemap use;
- `AnalyzeFileTypesAsync` — bounded extension groups plus complete root/type totals and exact category aggregates.

SQLite/native and crawler-backed in-memory providers implement the same accounting semantics.

### Namespace versus physical storage

Every visible file name contributes logical bytes. Stable hard-linked names share one physical allocation. Within one analysis root, physical allocation is attributed once to the case-insensitive lexicographically first path for that `FileIdentity`; other names increment `HardLinkAliasCount` without adding allocation.

Unknown allocated-size metadata stays unknown. FileOp does not substitute logical size and label the result exact physical usage.

When allocation is complete:

```text
sum(directory child allocated bytes) = root allocated bytes
sum(extension allocated bytes)       = root allocated bytes
sum(category allocated bytes)        = root allocated bytes
```

### File types and exact categories

Extensions are normalized metadata; no content/MIME sniffing occurs. `StorageFileCategoryClassifier` deterministically maps extensions into a small fixed category set.

`MaxTypes` bounds only returned extension rows. Complete root totals and `TypeCount` are calculated before that limit, and exact category rows are calculated over the complete extension aggregate set. SQLite performs the recursive subtree/physical-identity work once and emits bounded extension rows plus complete categories.

## Aggregate Storage history

History stores observations of the already-reviewed aggregate model rather than per-file event history.

Each observation contains root logical bytes, nullable physical allocation, file-name count, hard-link alias count, complete extension-group count and exact category rows with the same metrics.

History uses an independently versioned `storage_history_*` sub-schema in the same per-volume SQLite database. Namespace rebuilds clear current file/checkpoint state without erasing prior trustworthy observations.

Root uniqueness uses a custom SQLite ordinal-ignore-case collation backed by `StringComparer.OrdinalIgnoreCase`, avoiding SQLite built-in `NOCASE`'s ASCII-only behavior for Windows Unicode paths.

`StorageHistoryDelta.Between` produces signed logical/count deltas and nullable physical deltas. Missing categories are known zero baselines; present categories with unknown physical allocation remain unknown. Physical uncertainty by itself is not treated as evidence of growth.

## Read-only optimisation evidence

Protocol v7 adds `AnalyzeStorageOptimization` over the current durable native index. Its default policy surfaces bounded large files, old large files and same-size candidate groups.

Hard-link aliases are collapsed by stable physical identity before ranking or same-size grouping. Same-size is a duplicate prefilter only: equal logical length is not content equality. Potential savings remains a logical upper bound until a later lazy content-verification stage proves matching bytes.

The first Optimize slice is native-only. FileOp does not present a profile-scoped fallback snapshot as complete reclaim analysis. The operation is read-only and performs no content hashing or mutation.

The Optimize UI also hosts an on-demand performance-diagnostics panel. It measures source/index state, volume free-space evidence and exact bounded Search/Storage probes through the existing desktop APIs. There is no periodic poller, registry cleaner, RAM trimming, arbitrary service control or opaque health score.

## Indexing service boundary

`FileOp.Indexer` owns native NTFS indexing and persistent writes for one desktop session. It accepts one authenticated client and exits when that client or launching desktop process exits.

Current **protocol v7** operations are:

- Hello / GetVolumes / GetStatus;
- RebuildVolume / SyncVolume;
- Search;
- BrowseDirectory;
- AnalyzeStorage;
- AnalyzeStorageTypes;
- AnalyzeStorageOptimization;
- CaptureStorageHistory;
- GetStorageHistory.

Protocol history:

- v1 established indexing/search;
- v2 added directory Storage;
- v3 added file-type analysis;
- v4 enriched type analysis with exact categories;
- v5 added aggregate history capture/query;
- v6 added exact paged direct-child browsing;
- v7 added read-only storage optimisation analysis.

Strict version negotiation prevents mismatched desktop/helper binaries from silently disagreeing about operations or payloads.

The pipe uses a random session name, current-user-only ACL, exact connected-client PID verification, non-empty request IDs and an 8 MiB frame cap. Oversized responses return retryable `ResponseTooLarge`; interrupted exchanges fault the session rather than risk request/response desynchronization.

### Service composition

`StorageHistoryIndexingServiceBackend` wraps the reviewed `NtfsIndexingServiceBackend` and provides v5 history behavior. `PagedDirectoryIndexingServiceBackend` wraps that history-aware backend and adds the v6 browse operation. `StorageOptimizationIndexingServiceBackend` is the current outer wrapper and adds the v7 Optimize operation. Existing operations continue to delegate inward unchanged.

Browse independently resolves the attached volume, validates directory containment, takes a shared `IndexingVolumeFileGate` lease, checks the durable checkpoint through a read-only SQLite query, verifies the directory exists in the index and then performs the keyset query. No schema initialization or write-capable `SqliteFileIndex` object is created on the browse path.

Optimize follows the same attached-volume/containment/shared-lease/checkpoint/indexed-directory discipline, then opens the database in read-only/query-only mode for bounded metadata analysis.

`CaptureStorageHistory` accepts volume/root/directory but no client timestamp. It calls the existing native `AnalyzeStorageTypes` path with `MaxTypes = 1`, canonicalizes capture time to the current UTC-hour start and persists the immutable aggregate. Repeated captures in one hour overwrite the same bucket.

`GetStorageHistory` requires the physical volume/root to remain attached and validates directory containment, but it does not require a current live checkpoint. Historical observations remain readable while the current namespace needs repair.

The wrappers currently mirror the native backend's deterministic database-key formula. Offline verifiers guard parity; centralizing the formula into one shared helper is a future cleanup that must preserve database names.

## Persistence, validity and concurrency

Live namespace reads have two coordination layers:

- process-local desktop/native operation discipline;
- cross-process reader/writer file lease.

Search, exact browse, live Storage analytics and Optimize analysis take shared leases. Rebuild and journal synchronization take an exclusive lease across the entire semantic operation, including checkpoint invalidation. A partial rebuild therefore cannot be exposed as a valid live analysis.

If synchronization requires a fresh snapshot, the durable checkpoint is deleted before `SnapshotRequired` is returned. Invalid state survives helper restarts.

History query is independent of live checkpoint validity because persisted observations are validated on write and read. History persistence uses ordinary SQLite transactions in its independent sub-schema.

The current rebuild path temporarily removes the affected volume from live reads. Shadow-database rebuild plus atomic swap remains a future improvement.

## Privilege policy

`FileOp.App` always remains `asInvoker`. The helper starts unelevated; access denied maps to structured `ElevationRequired`.

Helper-only UAC is permitted only for a limited split administrator token, preserving the same Windows account SID. Credential-over-the-shoulder elevation from a standard user remains unsupported until a separately reviewed Windows service/ACL design exists.

The helper chooses its index root under the current account's LocalAppData and never accepts an arbitrary database path from the desktop.

The app bundles `FileOp.Indexer` beside the desktop executable and resolves only that exact adjacent non-reparse helper. This is a deterministic location rule, not an Authenticode trust assertion; production packaging still needs publisher/signature verification before elevation is exposed.

Partition management, formatting, BitLocker changes and other destructive administration remain outside this protocol.

## Desktop native/fallback lifecycle

The desktop coordinator:

1. starts the helper unelevated;
2. discovers the NTFS volume containing the user profile;
3. builds or resumes a durable snapshot;
4. replays bounded USN batches until the cursor converges or startup budget expires;
5. exposes the valid native index while low-priority catch-up continues;
6. gives foreground Search/Files/Storage priority over maintenance;
7. keeps a valid but stale native snapshot usable when live sync needs elevation;
8. falls back to the bounded user-profile crawler when native indexing is unavailable;
9. transitions back to fallback if a live native session later becomes unusable.

Search, Files, Folders, Types/Categories and native History share this lifecycle. Fallback remains valid for Search/Files/Storage but does not contribute observations to the native history series or native reclaim recommendations.

### Native history scheduling

`DesktopSearchEngine` owns automatic history scheduling; it does not depend on a History page or even the Storage page being opened.

When engine state reports a current native index, the scheduler yields briefly after the state notification because background synchronization raises `StateChanged` before releasing the native operation gate. It then attempts both desktop foreground/native gates with `WaitAsync(0)`.

Existing Search/Files/Storage work therefore wins immediately. Service/SQLite contention and unavailable current-state conditions use cooldowns; a successful bucket suppresses more automatic captures for that UTC hour. The service remains authoritative for hourly idempotency.

The first desktop history policy captures only the whole primary native volume. It does not turn every folder visited in Storage into an implicitly tracked time series.

### UI supersession

A newer query/navigation invalidates the visible generation but does not cancel an already-transmitted service request. Stale queued work is discarded before transmission and stale completed work before rendering. Only shutdown deliberately interrupts the active exchange.

Automatic history follows the same pipe rule: it avoids queueing behind foreground work, but once its named-pipe request has been transmitted it is allowed to finish rather than faulting the reusable session.

Storage has four views: Folders, Types, History and Optimize. Files is a separate read-only main navigation surface. The exact browse coordinator follows the same foreground `_searchOperationGate` / `_nativeOperationGate` order as Search and Storage. Optimize invalidates in-flight visible work when the user changes mode; performance probes are on-demand and use the existing public Search/Storage paths rather than a new background scheduler.

## Validation strategy

Hosted CI is useful but not the only gate. FileOp carries reproducible no-Actions validation:

- `verify_storage_ui.py` / `verify_storage_ui_edgecases.py` — XAML, treemap, path and category presentation;
- `verify_storage_types.py` / `verify_storage_types_fuzz.py` — exact SQLite type/category semantics;
- `verify_storage_optimization.py` — hard-link-aware reclaim-candidate semantics plus protocol-v7/WinUI/read-only safety wiring;
- `verify_performance_diagnostics.py` — capacity arithmetic, exact bounded probes, no-polling/no-fake-optimiser boundary and Optimize integration;
- `verify_storage_history.py` / `verify_storage_history_unicode.py` — history persistence, deltas, corruption and Unicode root identity;
- `verify_storage_history_service.py` — protocol-v7 history wiring, service-owned timestamps, live-analysis-before-persist ordering, contention mapping and database-key parity;
- `verify_storage_history_ui.py` — engine-owned low-priority scheduling, UTC-hour cadence, whole-volume scope, fallback exclusion, timeline unit consistency and History UI/source wiring;
- `verify_files_ui.py` — exact paged Files lifecycle and no-rescan behavior;
- `verify_directory_browse.py` — randomized SQLite keyset paging plus protocol/read-only/lease/checkpoint/native-fallback source wiring;
- `test-local.ps1 -OfflineOnly` — all standard-library verifiers without the .NET SDK;
- `test-local.ps1` — full Windows Core/native/indexer/tests/WinUI/bundled-helper build and real process handshake.

## Roadmap

### Fast NTFS/storage engine — current

Implemented foundations include MFT/USN ingestion, durable SQLite metadata, hard-link namespaces, journal-safe mutation/checkpoints, authenticated helper IPC, native-first Search with fallback, multi-instance index leases, exact paged native directory browsing, directory Storage, treemap drill-down, file-type analytics, exact categories, aggregate history persistence, protocol-v5 history capture/query, low-priority native hourly capture, a read-only growth timeline and protocol-v7 read-only Optimize analysis.

Current measurable performance evidence includes on-demand source/index status, indexed item count, volume free-space capacity, end-to-end Search/root-Storage probe latency and timer-baseline disclosure. Next engine/lifecycle work includes helper-owned index-size/cache statistics, explicit USN freshness/backlog evidence, low-frequency latency distributions, sparse/compressed/reparse semantics, measured search/analytics memory budgets, specialized filename/path acceleration, case-sensitive namespace policy, shadow-index rebuild and broader multi-volume orchestration.

### File manager

Implemented: a read-only indexed Files surface using protocol-v6 exact incremental pages. Next: tabs/dual pane, then queued copy/move/delete, collision policies, pause/resume, action history and safe undo as separately reviewed slices.

### Storage intelligence

Implemented: Folders, Types/Categories, native History and a first read-only Optimize advisor for large/old/same-size candidates. Planned next: lazy content verification for duplicate candidates, explicit historical folder contributors, verified physical reclaim estimates, carefully scoped Downloads/installer/temp review and transparent explanations for every reclaim recommendation. Cleanup actions remain behind the Files preflight/recovery/authorization boundary.

### Disk and performance surfaces

Current: on-demand measurable performance diagnostics embedded in Optimize, with no health score or tuning action. Planned: helper-owned index diagnostics, SMART/health, volumes/partitions/filesystems, TRIM/defrag applicability/status where appropriate, BitLocker status, storage I/O bottleneck attribution and startup/disk activity diagnostics. Destructive/admin actions remain isolated behind a separately reviewed privileged helper.
