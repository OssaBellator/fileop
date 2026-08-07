# FileOp architecture

## Product boundary

FileOp is a storage operating layer for Windows, not a generic "PC cleaner". The central architectural rule is that search, browsing, storage analytics, duplicate discovery and cleanup should consume a shared filesystem model instead of independently scanning disks.

## Component model

```text
WinUI application
      |
      +-----------------------------+
      |                             |
      v                             v
FileOp.Core                   FileOp.Windows
  |-- query parser              |-- NTFS volume discovery
  |-- IFileIndex                |-- MFT namespace enumeration
  |-- stable FileIdentity       |-- USN journal reader/coalescer
  |-- index change batches      |-- file-ID metadata hydration
  |-- SQLite persistence        |-- namespace synchronization
  |-- storage snapshots         |-- snapshot seeding
  |-- fallback crawler          |
      |                         +---- Windows-native provider boundary
      +--------------+--------------+
                     v
               persistent index
                 /   |   \
           search analytics cleanup
```

`FileOp.Core` stays independent of how records are discovered. Windows-specific filesystem control codes and P/Invoke live in `FileOp.Windows` so network, removable and non-NTFS providers can use different implementations without contaminating the search/domain layer.

## NTFS ingestion strategy

The native NTFS implementation uses supported Windows filesystem controls rather than raw-sector parsing:

1. Discover ready NTFS volumes and record their volume serial numbers.
2. Open a volume handle (`\\.\C:` style device path).
3. Query the current journal identity and USN range.
4. Enumerate MFT-backed namespace records with `FSCTL_ENUM_USN_DATA`.
5. Reconstruct paths from file-reference and parent-file-reference relationships.
6. Open records by file ID and hydrate logical size, allocated size, link count, timestamps and attributes.
7. Persist the snapshot and its journal checkpoint.
8. Read subsequent `FSCTL_READ_USN_JOURNAL` batches.
9. Normalize journal records into upsert/delete/rename events, pairing old/new rename records.
10. Translate normalized events into namespace mutations and commit those mutations together with the durable journal checkpoint.
11. If the journal identity changes, a cursor falls outside the readable range, or a parent identity cannot be resolved, fail safe into a fresh namespace snapshot.

The native parser currently accepts USN record major version 2, which is the 64-bit file-reference format used for the NTFS provider. Unsupported major versions fail explicitly rather than being interpreted using the wrong layout.

### Metadata hydration

`NtfsFileMetadataReader` opens an item using its NTFS file reference and reads `FILE_STANDARD_INFO` / `FILE_BASIC_INFO`. This supplies:

- logical length (`EndOfFile`);
- allocated length (`AllocationSize`);
- hard-link count;
- directory state;
- last-write time;
- file attributes.

This preserves `size:` query semantics while the native provider evolves. It is still not the final highest-throughput metadata strategy because opening every record individually is more expensive than extracting all required attributes from NTFS metadata in bulk. It is an accuracy-first bridge between the journal/MFT foundation and a future direct metadata parser.

### Rename durability

A rename is represented by separate `RENAME_OLD_NAME` and `RENAME_NEW_NAME` USN records. The change coalescer does not persist a checkpoint beyond an unmatched old-name record. If the pair is split at a read-buffer boundary or the process exits, the next read begins at the old record again and can reconstruct the pair.

Directory renames are applied as subtree moves. Updating only the renamed directory row would leave all descendant absolute paths stale, so the SQLite namespace coordinator rewrites the directory and every descendant path in one transaction. That transaction also advances the USN checkpoint.

## File identity and mutation model

Path is not a durable identity because rename and move operations change it. `FileRecord` can therefore carry:

```text
FileIdentity = (VolumeSerialNumber, FileReferenceNumber)
ParentIdentity = (VolumeSerialNumber, ParentFileReferenceNumber)
```

The generic `IFileIndex` supports initial insertion and stable-identity upsert/delete changes. `FileOp.Windows` adds namespace-aware move semantics on top of the SQLite schema for NTFS journal processing.

Persistent namespace rows remain keyed by path because one NTFS file can have multiple hard-link names. File identity is an indexed relationship, not a unique row key.

### Current hard-link limitation

The SQLite representation can store multiple paths for one file identity, but the current initial snapshot path resolver still reduces MFT/USN enumeration to one path per file reference. Therefore the native snapshot is not yet a complete hard-link namespace enumerator and is not enabled as the WinUI default.

The next namespace step should replace this one-path-per-FRN snapshot model with a representation that preserves every filename/parent relationship, ideally from direct NTFS metadata enumeration rather than treating `FSCTL_ENUM_USN_DATA` as a complete hard-link source.

## Target indexing architecture

```text
NTFS volume
  |-- namespace snapshot -------------------+
  |-- file-ID metadata hydration -----------|----> metadata pipeline
  |-- USN change journal -------------------|          |
                                             |          v
Other filesystem provider ------------------+    persistent index
                                                    /   |   \
                                              search analytics cleanup
```

### Constraints

- Initial NTFS discovery should read filesystem metadata rather than recurse through every directory.
- Incremental changes are applied from the USN journal.
- File identity uses volume identity + file ID, not path alone, whenever the provider supports it.
- Allocated size, hard links, sparse/compressed state and reparse points must be represented explicitly.
- Hashes and content extraction are lazy workloads and must not delay filename/path availability.
- Search results should remain responsive while indexing continues.
- Network/removable/non-NTFS volumes use provider-specific fallbacks rather than pretending MFT semantics exist everywhere.
- Journal discontinuity must fail safe into a fresh snapshot, never silently skip changes.
- Journal mutations and checkpoint advancement must be atomic.
- Parent identity resolution failures are consistency failures, not an invitation to guess an absolute path.

## Privilege boundary

The desktop UI should run as the user. Native read-only discovery and file-ID access may fail on systems where volume/file handles are not available to the current token; that is a capability failure, not a reason to run the whole app elevated.

Before the WinUI shell switches to the native provider by default, the indexing lifecycle needs a narrow background/service boundary that can obtain only the privileges required for NTFS discovery and expose a versioned, read-oriented interface to the desktop process. Partition and other destructive disk administration should remain a separate privileged surface.

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
- rename-old/new coalescing with buffer-boundary-safe checkpoints
- journal-to-index change translation
- transactional directory subtree moves
- atomic namespace mutation + checkpoint commits
- native snapshot seeder
- Windows regression tests for rename durability and subtree moves

Next:

- complete multi-name/hard-link namespace snapshots
- sparse/compressed/reparse metadata semantics
- indexing service / privilege boundary
- benchmark harness with multi-million-record synthetic and real-world datasets
- specialized filename/path search structure beyond SQLite substring scans
- switch the WinUI search snapshot to the native provider after correctness/performance validation

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
- privileged helper
- partition operations with pre-flight validation and explicit review
