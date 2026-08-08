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
  |-- storage analytics          |-- file-ID metadata hydration
  |-- service protocol DTOs      |-- hard-link expansion
  |-- fallback crawler           |-- namespace synchronization
      |                              |
      +---------------+--------------+
                      v
              per-volume indexes
                  /   |   \
            search analytics cleanup

FileOp.Benchmarks
      |
      +---- synthetic Search/Storage baselines
```

`FileOp.Core` is discovery-provider independent. Windows filesystem controls, P/Invoke and the helper/pipe implementation remain in `FileOp.Windows`. `FileOp.Indexer` is a thin process host around that backend. Destructive disk administration will use a separate future privileged surface rather than expanding the indexing helper.

## Unified filesystem model

A persisted file record is more than a path. The core model can carry:

```text
FileIdentity   = (VolumeIdentity, FileReferenceNumber)
ParentIdentity = (VolumeIdentity, ParentFileReferenceNumber)
```

Path is a namespace attribute, not durable identity. Rename, move and hard-link operations can change names without changing the underlying NTFS file.

Indexed metadata currently includes path/name/extension, logical and allocated size, timestamps, attributes, provider volume identity, file/parent identity and hard-link-relevant state. Expensive metadata such as hashes/content extraction remains lazy work rather than blocking filename/path availability.

## NTFS ingestion

The native fast path uses supported NTFS metadata interfaces rather than recursively opening every directory:

1. discover ready NTFS volumes, serials and stable Windows volume GUID paths when available;
2. open the volume device handle;
3. query journal identity/range;
4. enumerate MFT-backed namespace records with `FSCTL_ENUM_USN_DATA`;
5. reconstruct paths from file/parent references;
6. hydrate logical size, allocation, link count, timestamps and attributes by file ID;
7. expand ordinary multi-name hard links;
8. persist the snapshot plus durable journal checkpoint;
9. consume subsequent `FSCTL_READ_USN_JOURNAL` batches;
10. normalize rename/upsert/delete/hard-link-refresh events;
11. commit namespace mutations and checkpoint advancement atomically;
12. require a fresh snapshot on journal discontinuity or inconsistent namespace evidence.

The current parser handles USN record major version 2 explicitly. Unsupported record formats fail instead of being interpreted using the wrong layout.

### Rename and hard-link correctness

Rename-old/new records can cross a read-buffer boundary. The coalescer therefore does not advance the durable cursor beyond an unmatched old-name record. Directory renames are applied as subtree moves so descendants cannot retain stale absolute paths.

Persistent namespace rows are keyed by path because one physical NTFS file can have multiple names. Stable `FileIdentity` is an indexed relationship across those rows.

For files with multiple links, the snapshot enumerates ordinary hard-link names and requires the observed namespace count to match hydrated link-count evidence. Journal hard-link changes trigger targeted reconciliation. Ordinary metadata changes propagate to every persisted alias row.

The current path schema is case-insensitive, matching ordinary Windows behavior. Per-directory case-sensitive NTFS namespaces remain an explicit future capability rather than being represented incorrectly.

## Volume identity

Raw NTFS serials are only 32-bit and can collide across disks. When Windows exposes `\\?\Volume{GUID}\`, FileOp derives the provider volume token from that stable GUID; serial-only identity remains a fallback for environments where the GUID is unavailable.

Requests also carry the current volume root because persisted paths are absolute. A drive-letter change therefore creates a fresh absolute namespace even when the physical volume identity remains stable.

## Shared Storage analytics

Storage analytics reads the same indexed rows as Search. It never launches another recursive filesystem scan merely to calculate sizes, types or categories.

`IStorageAnalytics` exposes:

- `AnalyzeDirectoryAsync` — recursive direct-child folder/file aggregates for drill-down and treemap use;
- `AnalyzeFileTypesAsync` — bounded extension groups plus complete root/type totals and exact category aggregates.

SQLite/native and crawler-backed in-memory providers implement the same accounting semantics.

### Namespace versus physical storage

Every visible file name contributes logical bytes. Stable hard-linked names share one physical allocation. Within one analysis root, physical allocation is attributed once to the case-insensitive lexicographically first path for that `FileIdentity`; other names increment `HardLinkAliasCount` without adding allocation.

Unknown allocated-size metadata stays unknown. FileOp does not substitute logical size and label the result exact physical usage.

These invariants hold when allocation is complete:

```text
sum(directory child allocated bytes) = root allocated bytes
sum(extension allocated bytes)       = root allocated bytes
sum(category allocated bytes)        = root allocated bytes
```

Logical totals intentionally count all namespace names, including hard-link aliases.

### File types and exact categories

Extensions are normalized metadata; no content/MIME sniffing occurs. `StorageFileCategoryClassifier` deterministically maps extensions to NoExtension, Documents, Images, Video, Audio, Archives, Applications, Code, Data, DiskImages, Fonts or Other. Ambiguous extensions use one stable policy; `.ts` is Code while `.mts`/`.m2ts` are Video.

`MaxTypes` bounds only extension rows. Complete root totals and `TypeCount` are calculated before that limit.

Protocol v4 also returns exact category rows over the complete extension aggregate set. Category totals therefore remain exact even when the extension table contains only the top N types. A category can legitimately have zero physical bytes when it contains only non-canonical hard-link aliases.

SQLite performs the expensive recursive subtree/physical-identity aggregation once and emits two row kinds from the same query: bounded extension rows plus all category rows. The deterministic classifier is registered on the connection as `fileop_category`. The in-memory fallback groups exact categories from its complete pre-truncation extension set.

## Search and analytics performance

SQLite is the durable metadata store and current reference implementation of search/analytics semantics. It is not assumed to be the final Everything/WizTree-class specialized structure.

`FileOp.Benchmarks` provides synthetic 100k/1M baselines for:

- filename/path search;
- extension/size filtering;
- recursive directory aggregation;
- file-type/category aggregation.

Future specialized filename/path structures or incrementally maintained analytics may sit beside SQLite without changing the public semantics established here.

## Indexing service boundary

`FileOp.Indexer` owns native NTFS indexing and persistent writes for one desktop session. It accepts one authenticated client and exits when that client or launching desktop process exits.

Current **protocol v4** operations are:

- Hello / GetVolumes / GetStatus;
- RebuildVolume / SyncVolume;
- Search;
- AnalyzeStorage;
- AnalyzeStorageTypes.

Protocol history: v1 established indexing/search, v2 added directory Storage, v3 added file-type analysis, and v4 enriches the existing type-analysis response with exact category rows. Strict version negotiation prevents mismatched desktop/helper binaries from silently disagreeing about the response schema.

The pipe uses a random session name, current-user-only ACL, exact connected-client PID verification, non-empty request IDs and an 8 MiB frame cap. Oversized responses return retryable `ResponseTooLarge` while preserving the session. Interrupted exchanges fault the connection rather than risk request/response desynchronization.

## Persistence, validity and multi-instance concurrency

Every NTFS volume/root pair has a separate SQLite database. A rebuild of one volume therefore cannot clear another volume's data.

A volume is eligible for service-backed Search/Storage only with a valid durable checkpoint and while no maintenance operation owns it.

Per-volume coordination has two layers:

- process-local non-blocking operation gate;
- cross-process reader/writer file lease.

Search and Storage take shared leases. Rebuild and journal synchronization take an exclusive lease across the entire semantic operation, including checkpoint invalidation. This prevents two independent FileOp helpers from interleaving one multi-transaction snapshot.

If synchronization requires a fresh snapshot, the durable checkpoint is deleted before `SnapshotRequired` is returned. Invalid state therefore survives helper restarts.

The current rebuild path temporarily removes the affected volume from reads. Shadow-database rebuild plus atomic swap remains a future improvement for uninterrupted reads.

## Privilege policy

`FileOp.App` always remains `asInvoker`. The helper starts unelevated; access denied maps to structured `ElevationRequired`.

Helper-only UAC is currently permitted only for a limited split administrator token, preserving the same Windows account SID. Credential-over-the-shoulder elevation from a standard user is deliberately unsupported until a separately reviewed Windows service/ACL design exists.

The helper chooses its index root under the current account's `LocalApplicationData\FileOp\Index`; an elevated helper does not accept arbitrary database paths.

The desktop build bundles `FileOp.Indexer` beside the app and resolves only that exact adjacent non-reparse executable. This is not a cryptographic trust assertion. Production packaging still needs Authenticode/publisher verification before elevation is exposed.

Partition management, formatting, BitLocker changes and other destructive administration remain outside this protocol.

## Desktop native/fallback lifecycle

The desktop coordinator:

1. resolves and starts the helper unelevated;
2. discovers the NTFS volume containing the user profile;
3. builds or resumes a durable snapshot;
4. replays bounded USN batches until the cursor converges or startup budget expires;
5. exposes the valid native index while low-priority catch-up continues;
6. gives foreground Search/Storage priority over maintenance;
7. keeps a valid but stale native snapshot usable when live sync needs elevation;
8. falls back to the bounded user-profile crawler when native indexing is unavailable;
9. transitions back to fallback if a live native session later becomes unusable.

The first-run orchestrator targets the user-profile NTFS volume. Other valid attached-volume databases can participate in Search, while automatic bootstrap of every attached NTFS volume remains future lifecycle work.

### UI supersession

A newer query/navigation invalidates the visible generation but does not cancel an already-transmitted service request. Stale queued work is discarded before transmission and stale completed work before rendering. Only shutdown deliberately interrupts the active exchange.

### Storage UI

The WinUI Storage page has Folders and Types modes sharing one current path/source.

Folders provides summary cards, drill-down, direct-entry table and proportional treemap with an exact root-derived `Other entries` tile when children are truncated.

Types provides a bounded extension table and exact category bars from protocol v4. Category completeness is independent of the extension-table result limit. Native and fallback modes use the same UI/controller semantics and neither triggers another filesystem scan.

## Validation strategy

Hosted CI is useful but not the only gate. FileOp carries reproducible no-Actions validation:

- `tools/verify_storage_ui.py` — XAML/source/treemap/path/exact-category checks;
- `tools/verify_storage_ui_edgecases.py` — targeted hard-link/category UI regression;
- `tools/verify_storage_types.py` — exact SQLite v4 query and source/protocol wiring;
- `tools/verify_storage_types_fuzz.py` — 1,000 randomized SQL/reference parity cases;
- `tools/test-local.ps1` — full Windows Core/native/indexer/tests/WinUI/bundled-helper build and real process handshake.

The SQL verifier extracts the committed raw query and fails if its executable fixture drifts from source.

## Roadmap

### Fast NTFS engine — current

Implemented foundations include MFT/USN ingestion, durable SQLite metadata, hard-link namespaces, journal-safe mutations/checkpoints, authenticated helper IPC, native-first Search with fallback, multi-instance index leases, directory Storage, treemap drill-down, file-type analytics and exact category rollups.

Next engine work includes sparse/compressed/reparse semantics, measured search/analytics latency and memory budgets, specialized filename/path acceleration, case-sensitive namespace policy, shadow-index rebuild and broader multi-volume orchestration.

### File manager

Planned: indexed directory browsing, tabs, dual pane, queued copy/move/delete, collision policies, pause/resume, action history and safe undo.

### Storage intelligence

Implemented: recursive folder sizes, hard-link-aware physical accounting, treemap, extension types and exact categories.

Next: growth history/forecasting, duplicate pipeline, application/storage ownership and explainable cleanup recommendations.

### Storage administration

Future separate privileged surface: SMART/NVMe health, BitLocker/volume information, TRIM/filesystem diagnostics and partition operations with explicit pre-flight validation/review.
