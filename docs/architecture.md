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
  |-- stable FileIdentity       |-- USN journal reader
  |-- index change batches      |-- file-reference path resolver
  |-- storage snapshots         |
  |-- fallback crawler          +---- Windows-native provider boundary
      |                             |
      +--------------+--------------+
                     v
               persistent index
                 /   |   \
           search analytics cleanup
```

`FileOp.Core` stays independent of how records are discovered. Windows-specific filesystem control codes and P/Invoke live in `FileOp.Windows` so network, removable and non-NTFS providers can use different implementations without contaminating the search/domain layer.

## NTFS ingestion strategy

The first native NTFS implementation uses supported Windows filesystem controls rather than raw-sector parsing:

1. Discover ready NTFS volumes and record their volume serial numbers.
2. Open a volume handle (`\\.\C:` style device path).
3. Query the current journal identity and USN range.
4. Enumerate MFT-backed namespace records with `FSCTL_ENUM_USN_DATA`.
5. Reconstruct paths from file-reference and parent-file-reference relationships.
6. Persist a snapshot and its journal checkpoint.
7. Apply subsequent `FSCTL_READ_USN_JOURNAL` batches as index mutations.
8. If the journal identity changes or a saved cursor falls below the journal's readable range (`FirstUsn` or `LowestValidUsn`), discard the cursor and take a fresh namespace snapshot.

The native parser currently accepts USN record major version 2, which is the 64-bit file-reference format used for the NTFS provider. Unsupported major versions fail explicitly rather than being interpreted using the wrong layout.

### Why this is not yet the final metadata reader

`FSCTL_ENUM_USN_DATA` is an excellent fast namespace/file-identity source, but it is not a complete replacement for parsing NTFS metadata attributes. In particular, FileOp still needs an efficient strategy for:

- logical file size;
- allocated/compressed size;
- complete hard-link naming;
- sparse/compressed stream state;
- selected timestamps and other attributes that should not require opening every file individually.

Until those metadata fields can be hydrated efficiently and populated in the persistent index, the WinUI shell continues to use the existing crawler for its user-visible searchable snapshot. The native layer is compiled in CI so integration problems are caught without silently degrading query correctness.

## File identity and mutation model

Path is not a durable identity because rename and move operations change it. `FileRecord` can therefore carry:

```text
FileIdentity = (VolumeSerialNumber, FileReferenceNumber)
ParentIdentity = (VolumeSerialNumber, ParentFileReferenceNumber)
```

`IFileIndex` supports both initial batch insertion and `ApplyChangesAsync`, with upsert/delete changes keyed by stable identity when available. This is the contract the USN change processor will target.

The in-memory implementation is deliberately simple. The SQLite-backed persistent store provides indexed identity/path lookup and transactional batch application while preserving the same query/index contracts.

## Target indexing architecture

```text
NTFS volume
  |-- MFT namespace snapshot ----------------+
  |-- USN change journal ---------------------|----> metadata pipeline
                                             |          |
Other filesystem provider ------------------+          v
                                                  persistent index
                                                     /   |   \
                                               search analytics cleanup
```

### Constraints

- Initial NTFS discovery should read filesystem metadata rather than recurse through every directory.
- Incremental changes should be applied from the USN journal.
- File identity uses volume identity + file ID, not path alone, whenever the provider supports it.
- Allocated size, hard links, sparse/compressed state and reparse points must be represented explicitly.
- Hashes and content extraction are lazy workloads and must not delay filename/path availability.
- Search results should remain responsive while indexing continues.
- Network/removable/non-NTFS volumes use provider-specific fallbacks rather than pretending MFT semantics exist everywhere.
- Journal discontinuity must fail safe into a fresh snapshot, never silently skip changes.

## Privilege boundary

The desktop UI should run as the user. Native read-only discovery may fail on systems where a volume handle is not available to the current token; that is a capability failure, not a reason to run the whole app elevated.

A future administrative helper will expose a narrow, versioned command surface for operations that really require elevation (partition changes, selected disk operations, etc.). The helper should start only after explicit user action and exit after the operation completes.

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
- index upsert/delete mutation contract
- SQLite-backed persistent metadata store and checkpoint persistence

Next:

- metadata hydration for logical/allocated size and selected timestamps
- full hard-link representation
- journal-to-index change processor, including rename pairing
- benchmark harness with multi-million-record synthetic data
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
