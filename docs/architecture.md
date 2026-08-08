# FileOp architecture

## Product boundary

FileOp is a high-performance Windows storage operating layer, not a generic "PC cleaner". Search, browsing, Storage, duplicate discovery and cleanup should consume one shared filesystem model instead of launching independent scanners.

The priorities are speed, transparency, safety and user control. Registry cleaners, RAM boosters, opaque health scores, arbitrary service disabling and undocumented Windows-directory deletion are outside the product model.

## Component model

```text
FileOp.App (WinUI, asInvoker)
      |
      | shared Search/Storage coordinator
      |   \-- bounded crawler fallback
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
  |-- SQLite persistence         |-- USN journal reader/coalescer
  |-- storage analytics/history  |-- file-ID metadata hydration
  |-- service protocol DTOs      |-- hard-link expansion
  |-- fallback crawler           |-- namespace synchronization
      |                              |-- service/pipe backend
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

## Shared Storage analytics

Storage analytics reads the same indexed rows as Search. It never launches another recursive filesystem scan merely to calculate sizes, types or categories.

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

Each observation contains:

- root logical bytes;
- nullable physical allocation;
- file-name count;
- hard-link alias count;
- complete extension-group count;
- exact category rows with the same metrics.

History uses an independently versioned `storage_history_*` sub-schema in the same per-volume SQLite database. Namespace rebuilds clear current file/checkpoint state without erasing prior trustworthy observations.

Root uniqueness uses a custom SQLite ordinal-ignore-case collation backed by `StringComparer.OrdinalIgnoreCase`, avoiding SQLite built-in `NOCASE`'s ASCII-only behavior for Windows Unicode paths.

`StorageHistoryDelta.Between` produces signed logical/count deltas and nullable physical deltas. Missing categories are known zero baselines; present categories with unknown physical allocation remain unknown. Physical uncertainty by itself is not treated as evidence of growth.

## Indexing service boundary

`FileOp.Indexer` owns native NTFS indexing and persistent writes for one desktop session. It accepts one authenticated client and exits when that client or launching desktop process exits.

Current **protocol v5** operations are:

- Hello / GetVolumes / GetStatus;
- RebuildVolume / SyncVolume;
- Search;
- AnalyzeStorage;
- AnalyzeStorageTypes;
- CaptureStorageHistory;
- GetStorageHistory.

Protocol history:

- v1 established indexing/search;
- v2 added directory Storage;
- v3 added file-type analysis;
- v4 enriched type analysis with exact categories;
- v5 adds aggregate history capture/query.

Strict version negotiation prevents mismatched desktop/helper binaries from silently disagreeing about operations or payloads.

The pipe uses a random session name, current-user-only ACL, exact connected-client PID verification, non-empty request IDs and an 8 MiB frame cap. Oversized responses return retryable `ResponseTooLarge`; interrupted exchanges fault the session rather than risk request/response desynchronization.

### History service composition

`StorageHistoryIndexingServiceBackend` wraps the reviewed `NtfsIndexingServiceBackend` and delegates all pre-v5 operations unchanged.

`CaptureStorageHistory` accepts volume/root/directory but no client timestamp. It calls the existing native `AnalyzeStorageTypes` path with `MaxTypes = 1`. That live call resolves the attached volume, validates containment, acquires the existing operation gate/shared cross-process lease, validates the durable checkpoint and fully materializes exact category totals.

The service then canonicalizes capture time to the start of the current UTC hour and persists the immutable aggregate. Repeated captures in one hour overwrite the same bucket. The separate history write does not extend the NTFS semantic read lease because the aggregate is already fully materialized.

`GetStorageHistory` requires the physical volume/root to remain attached and validates directory containment, but it does not require a current live checkpoint. Historical observations remain readable while the current namespace needs repair. Detached-volume history browsing is future work.

The wrapper currently mirrors the native backend's deterministic database-key formula. The zero-Actions service verifier guards that parity; centralizing the formula into one shared helper is a later cleanup that should not change database names.

## Persistence, validity and concurrency

Live namespace reads have two coordination layers:

- process-local non-blocking operation gate;
- cross-process reader/writer file lease.

Search and live Storage analytics take shared leases. Rebuild and journal synchronization take an exclusive lease across the entire semantic operation, including checkpoint invalidation. A partial rebuild therefore cannot be exposed as a valid live analysis.

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
6. gives foreground Search/Storage priority over maintenance;
7. keeps a valid but stale native snapshot usable when live sync needs elevation;
8. falls back to the bounded user-profile crawler when native indexing is unavailable;
9. transitions back to fallback if a live native session later becomes unusable.

Search, Folders and Types/Categories already share this lifecycle. History is service-accessible in v5 but is not yet scheduled or presented by the desktop.

Fallback-history semantics are deliberately undefined. A static crawler snapshot should not silently join the same series as durable native observations without an explicit source model.

### UI supersession

A newer query/navigation invalidates the visible generation but does not cancel an already-transmitted service request. Stale queued work is discarded before transmission and stale completed work before rendering. Only shutdown deliberately interrupts the active exchange.

A future history scheduler must follow the same rule: capture should run only when foreground native work is idle and must not cancel an in-flight helper exchange.

## Validation strategy

Hosted CI is useful but not the only gate. FileOp carries reproducible no-Actions validation:

- `verify_storage_ui.py` / `verify_storage_ui_edgecases.py` — XAML, treemap, path and category presentation;
- `verify_storage_types.py` / `verify_storage_types_fuzz.py` — exact SQLite type/category semantics;
- `verify_storage_history.py` / `verify_storage_history_unicode.py` — history persistence, deltas, corruption and Unicode root identity;
- `verify_storage_history_service.py` — protocol-v5 capture/query wiring, 2,000+ timezone bucket cases, service-owned timestamps, live-analysis-before-persist ordering and database-key parity;
- `test-local.ps1` — full Windows Core/native/indexer/tests/WinUI/bundled-helper build and real process handshake.

## Roadmap

### Fast NTFS/storage engine — current

Implemented foundations include MFT/USN ingestion, durable SQLite metadata, hard-link namespaces, journal-safe mutation/checkpoints, authenticated helper IPC, native-first Search with fallback, multi-instance index leases, directory Storage, treemap drill-down, file-type analytics, exact categories, aggregate history persistence and protocol-v5 history capture/query.

Next engine/lifecycle work includes desktop history capture scheduling/timeline UI, sparse/compressed/reparse semantics, measured search/analytics latency and memory budgets, specialized filename/path acceleration, case-sensitive namespace policy, shadow-index rebuild and broader multi-volume orchestration.

### File manager

Planned: indexed directory browsing, tabs, dual pane, queued copy/move/delete, collision policies, pause/resume, action history and safe undo.

### Storage intelligence

Planned beyond the current history foundation: historical folder contributors, duplicate discovery, safe cleanup candidates, Downloads/installer analysis and transparent explanations for every reclaim recommendation.

### Disk and performance surfaces

Planned: SMART/health, volumes/partitions/filesystems, TRIM/defrag controls where appropriate, BitLocker status, storage I/O bottleneck attribution and startup/disk activity diagnostics. Destructive/admin actions remain isolated behind a separately reviewed privileged helper.