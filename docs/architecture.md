# FileOp architecture

## Product boundary

FileOp is a high-performance Windows storage operating layer, not a generic “PC cleaner.” Search, Files, Storage, duplicate evidence, cleanup and performance diagnostics are expected to reuse one filesystem model instead of launching unrelated scanners.

The priorities are speed, transparency, evidence quality, safety and user control. Registry cleaners, RAM boosters, opaque health scores, arbitrary service disabling and undocumented Windows-directory deletion are outside the product model.

A recurring architectural rule is:

> **evidence is not mutation authority**

Indexed metadata, same-size grouping, SHA-256 matches, physical reclaim estimates, known-location provenance and cleanup-readiness previews are all evidence. Destructive authority is created only inside separately reviewed operation boundaries with fresh action-time validation and explicit user intent.

## Component model

```text
FileOp.App (WinUI, asInvoker)
      |
      | shared Search / Files / Storage / Performance coordination
      |   \-- bounded user-profile crawler fallback where semantics permit
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
  |-- browse/storage models      |-- USN journal reader/coalescer
  |-- Optimize evidence          |-- file-ID metadata hydration
  |-- operation/delete contracts |-- hard-link expansion
  |-- recovery/history contracts |-- namespace synchronization
  |-- service protocol DTOs      |-- indexing service/pipe wrappers
  |-- fallback crawler           |-- Windows operation providers
      |                              |
      +---------------+--------------+
                      v
                per-volume SQLite
          current index + aggregate history

FileOp.Benchmarks
      |
      +---- synthetic Search/Storage baselines
```

`FileOp.Core` is discovery-provider independent. Windows filesystem controls, P/Invoke, NTFS semantics, helper/pipe implementation and Windows-specific mutation providers remain in `FileOp.Windows`. `FileOp.Indexer` is an indexing process host, not a general privileged filesystem-mutation service.

## Unified filesystem model

A persisted file record can carry stable provider identity in addition to its namespace path:

```text
FileIdentity   = (VolumeIdentity, FileReferenceNumber)
ParentIdentity = (VolumeIdentity, ParentFileReferenceNumber)
```

Path is a namespace attribute, not durable identity. Rename, move and hard-link operations can change names without changing the underlying NTFS file.

Indexed metadata includes path/name/extension, logical and allocated size, timestamps, attributes, provider volume identity and file/parent identity. Expensive content evidence such as SHA-256 is intentionally **not** part of the normal index; it is gathered lazily through a bounded desktop-side verification path.

## NTFS ingestion

The native path uses supported NTFS metadata interfaces rather than recursively opening every directory:

1. discover ready NTFS drive roots;
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

The current path schema is case-insensitive, matching ordinary Windows behavior. Per-directory case-sensitive NTFS namespaces remain a separate capability.

## Volume and database identity

When Windows exposes a stable volume GUID, FileOp derives the provider volume token from that identity; serial-based identity remains a fallback.

Requests also carry the current volume root because persisted namespace paths are absolute. A drive-letter/root change therefore uses a fresh absolute namespace database even when the physical volume identity is stable.

Every NTFS volume/root pair gets one deterministic SQLite file under the current user's `LocalApplicationData\FileOp\Index`. That database contains the current namespace/index plus additive aggregate-history tables.

`IndexDatabasePathResolver` is the filename authority used across native indexing and read-only service wrappers. It preserves the historical database naming contract rather than silently orphaning existing indexes.

## Exact directory browsing

The browser boundary is separate from recursive Storage analytics. `FileDirectoryBrowsePage` carries:

```text
DirectoryPath
TotalCount
Entries[]
NextCursor?
```

Rows are direct `FileRecord` metadata only. Recursive folder sizes, category totals and treemap weights remain Storage work.

Native paging is ordered by:

1. directories before files;
2. normalized name ascending;
3. normalized path ascending as deterministic tie-breaker.

The continuation cursor stores the last returned row's kind, name and absolute path. SQLite uses keyset pagination instead of `OFFSET`, so insertion before an existing cursor does not shift already-consumed rows into later pages.

A multi-page browse is live rather than transactionally frozen. Rebuild/sync exclusion is still enforced per request through the durable-checkpoint and cross-process lease model.

The desktop fallback pages the already-completed in-memory crawler snapshot rather than rescanning for every Files page. Native mode reads SQLite directly and does not materialize the entire persistent index merely to browse one directory.

## Files operation boundary

Files is no longer accurately described as a wholly read-only surface. **Browsing** is read-only, but already-merged file-operation work includes reviewed mutation boundaries. The most security-sensitive current user-facing mutation is permanent file deletion.

The delete design deliberately does **not** turn generic selection into authority and does not add a generic `FileOperationKind.Delete` to the ordinary planning queue.

### Permanent file-delete session

A Files delete review starts only from an exact file-only selection in one ready pane. Directories, ambiguous dual-pane selection, stale/missing pane state and non-direct-child rows fail before a delete plan is usable.

The App obtains a per-user cross-process destructive-session lock before the first recovery inspection. From there, the reviewed sequence is:

```text
cross-process destructive-session lock
-> persistent recovery-history scan
-> Windows read-only delete preflight
-> canonical/protected-location execution validation
-> explicit permanent-delete confirmation for exact canonical paths
-> persistent recovery recheck
-> session-scoped user-authorization receipt
-> durable SQLite action-history Begin
-> file-delete orchestrator
-> reviewed stability/final-capability providers
-> same-handle final mutation boundary
```

Key invariants:

- authorization is minted only after explicit confirmation;
- durable history begins before the orchestrator can cross the mutation barrier;
- unresolved `MutationStarted` / `RecoveryRequired` history blocks new authorization;
- recovery records are not reusable consent and do not authorize automatic replay;
- the final mutation capability is identity-bound and consumed through the reviewed same-handle path rather than a path-only delete fallback;
- final-lease release failures retain cleanup ownership and expose cleanup-only retry without granting new mutation authority;
- active Files tabs are refreshed/invalidated after durable begin because namespace state may have changed even when completion becomes recovery-sensitive.

Current UI scope is intentionally narrow: regular files only, permanent deletion, no directory deletion, no Recycle Bin/undo/restore claim.

The detailed delete contracts live in `docs/file-delete-*.md` and the Windows provider docs.

## Shared Storage analytics

Storage analytics reads the same indexed rows as Search and Files. It does not launch another recursive filesystem scan merely to calculate sizes, types or categories.

`IStorageAnalytics` exposes recursive directory aggregates and bounded extension/category analysis.

### Namespace versus physical storage

Every visible file name contributes logical bytes. Stable hard-linked names share one physical allocation. Within one analysis root, physical allocation is attributed once per stable `FileIdentity`; aliases contribute namespace count without duplicating allocation.

Unknown allocated-size metadata stays unknown. FileOp does not substitute logical size and label the result exact physical usage.

This is why the crawler fallback is not automatically treated as equivalent Optimize evidence: ordinary fallback `FileRecord` construction lacks the NTFS identity/allocation evidence required for the same physical-accounting guarantees.

## Aggregate Storage history

History stores observations of the reviewed aggregate model rather than per-file event history.

Each observation contains root logical bytes, nullable physical allocation, file-name count, hard-link alias count, complete extension-group count and exact category rows with the same metrics.

History uses an independently versioned sub-schema in the same per-volume database. Namespace rebuilds clear current file/checkpoint state without erasing prior trustworthy observations.

Automatic desktop history is opportunistic and native-only. Foreground Search/Files/Storage work wins; contention defers history rather than queueing it ahead of the user. Repeated capture in the same UTC hour is idempotent at the service layer.

## Optimize evidence

Protocol v7 introduced `AnalyzeStorageOptimization`; protocol v8 retains it unchanged while adding index diagnostics. The helper-side operation is metadata-only and read-only. It returns bounded large-file, stale-large and same-size candidate evidence based on the durable native index.

Hard-link aliases are collapsed by stable physical identity before ranking or same-size grouping. Same-size remains a duplicate **prefilter**, not content equality.

The App layers additional explicit evidence on top of that metadata result.

### Evidence level 1: same-size candidate upper bound

The full indexed same-size group carries `PotentialLogicalSavingsUpperBound`. It is a cheap logical candidate bound derived from equal indexed size and distinct physical identity. It is not a duplicate verdict.

### Evidence level 2: explicit SHA-256 verification

The user can explicitly request bounded content verification for a current same-size group. This path stays in the unelevated desktop process instead of adding an arbitrary content-read operation to the indexing helper.

Default policy permits at most:

- 8 sampled files;
- 2 GiB of whole-file content;
- a 1 MiB hashing buffer.

Only whole files are hashed. If fewer than two whole files fit the budget, the verifier refuses before opening candidate paths. All selected files are opened read-only before hashing and the indexed logical length is checked before/after full SHA-256 processing.

Matching results expose matching **path sets**, not raw digest values. Only fully hashed matching sampled paths contribute `VerifiedLogicalDuplicateBytes`.

### Evidence level 3: current physical reclaim upper bound

While the original content-read handles remain open, matching sampled paths receive a second metadata-only current-handle check. FileOp revalidates current physical identity, hard-link count, allocation, logical size and path continuity before presenting `VerifiedPhysicalReclaimableBytesUpperBound`.

The physical value:

- is limited to the sampled SHA-256 matches;
- collapses current hard-link aliases by current identity;
- treats multi-link names conservatively;
- retains one content-equivalent physical file in each match set;
- fails closed when current identity/link/allocation evidence is incomplete;
- is an upper bound, not action authority.

Content-hash evidence can remain valid even when the physical-evidence step is unavailable. The two result levels are intentionally separate.

See `docs/same-size-content-verification.md` and `docs/physical-reclaim-evidence.md`.

### Threshold overlay and persistence

Optimize threshold controls filter the already-returned native analysis in memory. They can only be same-or-stricter than the helper's `StorageOptimizationPolicy`; a lower display threshold would imply evidence the helper did not return and is rejected.

Supported stricter selections are persisted as policy-relative multipliers rather than raw byte/day thresholds. On each new analysis the multipliers are resolved against the fresh helper policy, then validated again through the same-or-stricter rule. Corrupt/unsupported preferences fall back to baseline.

This persistence changes display policy only. It does not rerun the helper, scan, hash, mutate or change protocol v8.

A future request for evidence **below** the helper's native baseline would need separately reviewed typed helper-policy transport. The current product fails closed instead of pretending looser evidence is complete.

### Known-location review and cleanup readiness

The current-user Downloads and Temp review reuses native Optimize subtree evidence and adds explicit provenance/rule IDs. Downloads is resolved through the Windows known-folder API; Temp uses the current-user environment path.

Age, location, extension and rule IDs remain review provenance. They never become `SafeToDelete` or consent.

On current `main`, known-location review is limited to the active native indexed volume. A redirected location outside that source is disclosed rather than silently crawled.

`Check readiness` is a separate read-only current-path/canonical/identity/allocation/hard-link check. Even a `CurrentEvidenceConsistent` result has `CleanupMutationAuthorized == false` and does not prove continuity with the originally indexed physical object. Permanent deletion, if later chosen, must begin again from Files and independently pass the delete session.

## Performance and device evidence

Optimize/Performance surfaces are measurement-oriented. Current reviewed evidence includes bounded Search/Storage timings, helper-owned SQLite/index diagnostics, resource-footprint reporting, Disk I/O attribution/timing/loss evidence, system CPU/memory context, physical-device/failure-prediction/health evidence where available and volume fragmentation analysis.

These providers do not justify fake optimization behavior. No registry cleaning, RAM trimming, arbitrary service control, automatic `VACUUM`, forced WAL checkpointing or opaque health scoring is performed.

Expensive measurement workloads are coordinated so explicit hashing, Disk I/O capture and other foreground diagnostics do not intentionally contaminate each other's evidence.

## Indexing-service boundary

`FileOp.Indexer` owns native NTFS indexing and persistent writes for one desktop session. It accepts one authenticated client and exits when that client or launching desktop process exits.

Current **protocol v8** operations are:

- Hello / GetVolumes / GetStatus;
- GetIndexDiagnostics;
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
- v7 added read-only storage optimization analysis;
- v8 added read-only helper-owned index diagnostics.

Content verification, cleanup readiness and file deletion are **not** indexing-service operations. This keeps an elevated indexing helper from becoming a general content-read or destructive filesystem oracle.

Strict version negotiation prevents mismatched desktop/helper binaries from silently disagreeing about operations or payloads.

## Persistence, validity and concurrency

Live namespace reads have two coordination layers:

- process-local desktop/native operation discipline;
- cross-process reader/writer file leases around each per-volume index database.

Search, exact browse, live Storage analytics, Optimize analysis and index diagnostics take shared read leases. Rebuild and journal synchronization take an exclusive maintenance lease across the entire semantic operation, including checkpoint invalidation. A partial rebuild therefore cannot be exposed as valid live analysis.

If synchronization requires a fresh snapshot, the durable checkpoint is invalidated before `SnapshotRequired` is returned. Invalid state survives helper restarts.

Desktop foreground paths use a consistent search-operation then native-operation gate order. New source-specific readers should preserve this ordering rather than introduce lock inversion.

History query is independent of live-checkpoint validity because historical observations are validated on write/read. Current live evidence remains checkpoint-bound.

The current rebuild path temporarily removes the affected volume from live reads. Shadow-database rebuild plus atomic swap remains a potential future improvement.

## Privilege policy

`FileOp.App` always remains `asInvoker`. The helper starts unelevated; access denied maps to structured elevation requirements.

Helper-only UAC is permitted only for a limited split administrator token, preserving the same Windows account SID. Credential-over-the-shoulder elevation from a standard user remains unsupported until a separately reviewed Windows service/ACL design exists.

The helper chooses its index root under the current account's LocalAppData and never accepts an arbitrary database path from the desktop.

The app bundles `FileOp.Indexer` beside the desktop executable and resolves only that exact adjacent non-reparse helper. This is a deterministic location rule, not an Authenticode trust assertion; production packaging still needs publisher/signature verification before elevation is exposed as a production trust boundary.

Permanent file deletion runs through the reviewed desktop/Windows file-operation boundary; it is not delegated to the indexing helper.

Partition management, formatting, BitLocker changes and other destructive administration remain outside protocol v8.

## Desktop native/fallback lifecycle

The desktop coordinator:

1. starts the helper unelevated;
2. discovers the NTFS volume containing the user profile;
3. builds or resumes a durable snapshot;
4. replays bounded USN batches until the cursor converges or startup budget expires;
5. exposes the valid native index while low-priority catch-up continues;
6. gives foreground Search/Files/Storage priority over maintenance;
7. keeps a valid but stale native snapshot usable when live sync needs elevation where existing semantics permit;
8. falls back to the bounded user-profile crawler when native indexing is unavailable;
9. transitions back to fallback if a live native session becomes unusable.

Fallback remains useful for Search, paged Files browsing and general Storage analytics, but it is not automatically promoted to equivalent native Optimize/reclaim evidence because its records do not preserve all NTFS physical identity/allocation semantics.

### UI supersession

A newer query/navigation invalidates the visible generation but does not cancel an already-transmitted named-pipe request. Stale queued work is discarded before transmission and stale completed work before rendering. Only shutdown deliberately interrupts an active exchange.

The same source-generation discipline is used for Storage/Optimize snapshots. Evidence captured for a superseded source is not rendered as current.

## Validation strategy

Hosted CI is optional; the repository maintains a complete no-Actions local gate.

`tools/test-local.ps1` is the authoritative inventory. It runs the standard-library randomized/model/source verifiers first, then—unless `-OfflineOnly` is used—the native .NET/Windows build/test portion.

```powershell
# Portable model/source checks only
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-local.ps1 -OfflineOnly

# Complete Windows gate
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-local.ps1
```

The full gate covers Core/Windows/Indexer/benchmark Release builds, the Windows regression/integration suite, WinUI x64 Release build, bundled Indexer artifact checks and a real bundled-helper process handshake in addition to the offline verifiers.

Security-sensitive boundaries have dedicated randomized/source verifiers, including file-delete preflight/history/final-capability/mutation/orchestration/session wiring, same-size content verification, physical reclaim evidence, threshold preferences and other Storage/Performance providers.

See `docs/local-validation.md` for the validation workflow. Individual verifier names should be treated as implementation detail of the authoritative gate rather than duplicated exhaustively in this architecture document.

## Architectural principles

1. **One shared metadata model.** Do not add a second scanner when the indexed model can answer the question with the required semantics.
2. **Lazy expensive evidence.** Content hashes and similar work run only under explicit bounded policy or reviewed idle-work rules.
3. **Physical claims require physical evidence.** Unknown allocation/identity must stay unknown rather than being inferred from logical namespace data.
4. **Evidence is not authority.** Recommendation, readiness, hashes and reclaim estimates do not become mutation consent.
5. **Mutation capability is narrow and action-time validated.** Destructive work must pass explicit reviewed boundaries and fail closed on ambiguous recovery/source state.
6. **Privilege is isolated.** The normal UI remains non-elevated and the indexing helper does not become a general mutation/content oracle.
7. **Measurement beats fake optimization.** Prefer observable performance/device evidence over registry cleaning, RAM boosting, opaque scores or unexplained system changes.
