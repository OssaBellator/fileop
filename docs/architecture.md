# FileOp architecture

## Product boundary

FileOp is a storage operating layer for Windows, not a generic "PC cleaner". The central architectural rule is that search, browsing, storage analytics, duplicate discovery and cleanup should consume a shared filesystem model instead of independently scanning disks.

## Component model

```text
WinUI application (asInvoker)
      |
      | bounded, versioned named-pipe protocol
      v
FileOp.Indexer (on-demand helper)
      |
      +-----------------------------+
      |                             |
      v                             v
FileOp.Core                   FileOp.Windows
  |-- query parser              |-- NTFS volume discovery
  |-- IFileIndex                |-- MFT namespace enumeration
  |-- stable FileIdentity       |-- USN journal reader/coalescer
  |-- index change batches      |-- file-ID metadata hydration
  |-- SQLite persistence        |-- hard-link namespace expansion
  |-- service protocol DTOs     |-- namespace synchronization
  |-- storage snapshots         |-- snapshot seeding
  |-- fallback crawler          |-- service backend / pipe transport
      |                             |
      +--------------+--------------+
                     v
             per-volume indexes
                 /   |   \
           search analytics cleanup

FileOp.Benchmarks
      |
      +---- synthetic persistent-index search baselines
```

`FileOp.Core` stays independent of how records are discovered. Windows-specific filesystem control codes, P/Invoke and named-pipe process-boundary implementation live in `FileOp.Windows`. `FileOp.Indexer` is a thin host around that Windows backend. Network, removable and non-NTFS providers can therefore use different implementations without contaminating the search/domain layer.

`FileOp.App` intentionally remains on the reviewed crawler until the service boundary itself has been reviewed and a separate WinUI cutover/fallback slice is validated.

## NTFS ingestion strategy

The native NTFS implementation uses supported Windows filesystem controls rather than recursively opening every directory:

1. Discover ready NTFS volumes and record their volume serial numbers.
2. Open a volume handle (`\\.\C:` style device path).
3. Query the current journal identity and USN range.
4. Enumerate MFT-backed namespace records with `FSCTL_ENUM_USN_DATA`.
5. Reconstruct a baseline path from file-reference and parent-file-reference relationships.
6. Open records by file ID and hydrate logical size, allocated size, link count, timestamps and attributes.
7. For files whose NTFS link count is greater than one, enumerate all ordinary hard-link names and expand the physical file identity into one namespace row per link path.
8. Persist the snapshot and its journal checkpoint.
9. Read subsequent `FSCTL_READ_USN_JOURNAL` batches.
10. Normalize journal records into upsert/delete/rename/hard-link-refresh events, pairing old/new rename records.
11. Translate normalized events into namespace mutations and commit those mutations together with the durable journal checkpoint.
12. If the journal identity changes, a cursor falls outside the readable range, namespace evidence is inconsistent, or a parent identity cannot be resolved, fail safe into a fresh namespace snapshot.

The native parser currently accepts USN record major version 2, which is the 64-bit file-reference format used for the NTFS provider. Unsupported major versions fail explicitly rather than being interpreted using the wrong layout.

### Metadata hydration

`NtfsFileMetadataReader` opens an item using its NTFS file reference and reads `FILE_STANDARD_INFO` / `FILE_BASIC_INFO`. This supplies:

- logical length (`EndOfFile`);
- allocated length (`AllocationSize`);
- hard-link count;
- directory state;
- delete-pending state;
- last-write time;
- file attributes.

Delete-pending file IDs are treated as absent so a still-open identity cannot be resurrected into the persistent namespace while Windows is already removing its final name.

This preserves `size:` query semantics while the native provider evolves. It is still not the final highest-throughput metadata strategy because opening records individually is more expensive than extracting all required attributes from NTFS metadata in bulk. It is an accuracy-first bridge between the journal/MFT foundation and a future bulk metadata parser.

### Hard-link namespace model

Persistent rows are keyed by path because one NTFS file can have multiple names. File identity is an indexed relationship, not a unique row key:

```text
physical file identity  (volume serial, file reference)
          |
          +-- C:\Work\artifact.bin
          +-- C:\Archive\artifact.bin
```

For a multi-link file, the hydrated `NumberOfLinks` is used as a completeness invariant. `NtfsHardLinkEnumerator` uses the Windows hard-link enumeration APIs only for files with `NumberOfLinks > 1`. Every returned link path is mapped back to a known directory file reference before it is persisted. If the distinct namespace count differs from `NumberOfLinks`, or a parent cannot be resolved, the snapshot is discarded and retried rather than silently losing a link.

`HARD_LINK_CHANGE` journal records request a targeted refresh of the affected file identity rather than automatically rebuilding the entire volume. The synchronizer reconciles live link paths against persisted rows and commits that reconciliation with the journal checkpoint. If several ambiguous namespace operations for the same hard-linked identity occur in one batch, it deliberately falls back to a resnapshot.

Ordinary data/metadata changes for a hard-linked file update every persisted alias row so logical size, allocated size, timestamps and attributes cannot diverge between names for the same physical file.

The current path store is case-insensitive to match normal Windows/NTFS behavior. Per-directory case-sensitive NTFS namespaces require a future schema/collation capability before two otherwise identical paths that differ only by case can be represented independently.

### Rename durability

A rename is represented by separate `RENAME_OLD_NAME` and `RENAME_NEW_NAME` USN records. The change coalescer does not persist a checkpoint beyond an unmatched old-name record. If the pair is split at a read-buffer boundary or the process exits, the next read begins at the old record again and can reconstruct the pair.

Directory renames are applied as subtree moves. Updating only the renamed directory row would leave all descendant absolute paths stale, so the SQLite namespace coordinator rewrites the directory and every descendant path in one transaction. That transaction also advances the USN checkpoint.

## File identity and mutation model

Path is not a durable identity because rename, move and hard-link operations change the namespace without necessarily changing the underlying file. `FileRecord` can therefore carry:

```text
FileIdentity = (VolumeSerialNumber, FileReferenceNumber)
ParentIdentity = (VolumeSerialNumber, ParentFileReferenceNumber)
```

The generic `IFileIndex` supports initial insertion and stable-identity upsert/delete changes. `FileOp.Windows` adds namespace-aware move and reconciliation semantics on top of the SQLite schema for NTFS journal processing.

NTFS volume serials are only 32-bit and can collide across attached volumes. The service therefore does not target a volume by serial alone: protocol requests include both the serial-derived identity and current root path. Per-volume database names include both values as well, so a drive-letter change does not silently reuse stale absolute paths.

A future provider may additionally persist Windows volume GUID paths or stronger filesystem-specific volume identities.

## Search performance strategy

SQLite is currently both the durable metadata store and the reference implementation of filename/path query semantics. It is intentionally not assumed to be the final Everything-class substring-search structure.

`FileOp.Benchmarks` creates deterministic 100,000- and 1,000,000-record synthetic indexes and measures:

- rare filename substring search;
- common path substring search;
- extension + size filtering.

The benchmark project is compiled in CI but long benchmark runs remain manual so normal validation stays fast. The results will provide a baseline for a specialized filename/path structure that can sit in front of or beside the durable SQLite metadata store without changing `IFileIndex` query semantics.

## Indexing service boundary

`FileOp.Indexer` owns native NTFS indexing and persistent index writes for a desktop session. It accepts exactly one client and exits after that client disconnects. It is not an always-running Windows service.

Version 1 exposes only:

- protocol/elevation handshake;
- NTFS volume and status discovery;
- fresh snapshot rebuild for one volume;
- one incremental USN synchronization batch for one volume;
- search across valid attached-volume indexes.

The pipe protocol uses a random per-session name, `PipeOptions.CurrentUserOnly`, non-empty request IDs, strict protocol-version validation and an 8 MiB frame cap. Required DTO fields are validated after deserialization rather than trusting serializer defaults.

### Per-volume persistence and validity

Each NTFS volume/root pair has a separate SQLite database. This avoids the current snapshot indexer's `ClearAsync` behavior allowing a rebuild of one drive to erase another drive's data.

A volume is searchable only when it has a durable checkpoint and is not undergoing a rebuild. Search takes the volume operation gate briefly so a rebuild cannot begin between checkpoint validation and the read. Other volumes remain searchable while one volume is busy.

If journal synchronization detects that a fresh snapshot is required, the service deletes that volume's durable checkpoint before returning `SnapshotRequired`. That makes invalidation persistent across helper restarts; stale data cannot become searchable merely because the process restarted.

The current rebuild path does not yet use a shadow database. The affected volume is therefore temporarily omitted from search during a rebuild. A future shadow-build + atomic-swap design can remove that per-volume outage without exposing partial snapshots.

### Privilege policy

The desktop UI always remains `asInvoker`. The helper starts unelevated first. Access-denied native operations are returned as the structured `ElevationRequired` capability result.

The current per-user pipe model permits UAC relaunch only for a limited split administrator token, where elevation retains the same Windows account SID. Credential-over-the-shoulder elevation from a standard account is deliberately unsupported because it can produce a different administrator identity, which would conflict with the current-user-only pipe and resolve a different account's LocalAppData.

The helper chooses its own persistent root under `LocalApplicationData\FileOp\Index`; an elevated process does not accept an arbitrary database path from its command line.

Production WinUI packaging must resolve and verify the installed `FileOp.Indexer` executable before offering elevation. Supporting different-user credential elevation requires a separately reviewed Windows service/ACL design rather than weakening the current pipe boundary.

Partition management, formatting, BitLocker changes and other destructive disk administration are explicitly outside the indexing protocol and remain a separate future privileged surface.

See `docs/indexing-service.md` for the detailed trust-boundary and cutover policy.

## Target indexing architecture

```text
NTFS volume
  |-- MFT namespace snapshot ----------------+
  |-- targeted hard-link expansion ----------|
  |-- file-ID metadata hydration ------------|----> FileOp.Indexer
  |-- USN change journal ---------------------|          |
                                               |          v
Other filesystem provider --------------------+    per-volume metadata
                                                      |
                                               fast search structure
                                                      |
                                           search / analytics / cleanup
```

### Constraints

- Initial NTFS discovery should read filesystem metadata rather than recurse through every directory.
- Incremental changes are applied from the USN journal.
- File identity uses volume identity + file ID, not path alone, whenever the provider supports it.
- Multiple namespace rows may share one physical file identity.
- Allocated size, hard links, sparse/compressed state and reparse points must be represented explicitly.
- Hashes and content extraction are lazy workloads and must not delay filename/path availability.
- Search results should remain responsive while indexing continues.
- Network/removable/non-NTFS volumes use provider-specific fallbacks rather than pretending MFT semantics exist everywhere.
- Journal discontinuity must fail safe into a fresh snapshot, never silently skip changes.
- Journal mutations and checkpoint advancement must be atomic.
- Parent identity resolution failures are consistency failures, not an invitation to guess an absolute path.
- A missing/invalid durable checkpoint means that volume is not eligible for service-backed search.
- The desktop process must never become elevated merely to index storage.

## Roadmap

### Milestone 1 — vertical slice — complete

- WinUI shell
- safe folder crawler
- in-memory metadata index
- debounced search
- basic `ext:` and `size:` query filters
- disk-capacity overview

### Milestone 2 — fast NTFS engine — in progress

Implemented:

- NTFS volume discovery and volume identity
- MFT-backed namespace enumeration via `FSCTL_ENUM_USN_DATA`
- version-aware USN v2 parser
- file-ID/parent-ID hierarchy reconstruction
- USN journal state/checkpoint model
- incremental journal batch reads
- journal replacement/overrun detection
- SQLite-backed persistent metadata store and checkpoint persistence
- logical/allocated-size, link-count, timestamp and attribute hydration by file ID
- delete-pending file-ID exclusion
- rename-old/new coalescing with buffer-boundary-safe checkpoints
- journal-to-index change translation
- transactional directory subtree moves
- atomic namespace mutation + checkpoint commits
- multi-name hard-link snapshot expansion
- targeted hard-link journal refresh and alias metadata propagation
- native snapshot seeder
- Windows-native regression/integration tests
- synthetic 100k/1M persistent-index benchmark harness
- versioned indexing service protocol
- on-demand `FileOp.Indexer` process boundary
- current-user named-pipe client/server transport
- structured capability/error contract
- per-volume persistent index isolation
- same-account helper-only UAC elevation policy

Next:

- sparse/compressed/reparse metadata semantics
- benchmark the current search implementation and establish latency/memory budgets
- specialized filename/path search structure beyond SQLite substring scans
- explicit support policy for per-directory case-sensitive NTFS namespaces
- optional shadow-index rebuild + atomic swap for uninterrupted per-volume search
- separate WinUI service-client integration and crawler fallback cutover

### Milestone 3 — file manager

- directory browsing backed by the shared index
- tabs and dual-pane mode
- queued copy/move/delete operations
- collision policies, pause/resume and verification
- action history and undo where safely possible

### Milestone 4 — storage intelligence

- folder-size aggregation
- treemap dataset
- growth history
- duplicates pipeline
- explainable cleanup recommendations

### Milestone 5 — storage administration

- SMART/NVMe health
- BitLocker/volume information
- TRIM/filesystem diagnostics
- separate privileged administration helper/service
- partition operations with pre-flight validation and explicit review
