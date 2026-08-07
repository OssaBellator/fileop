# FileOp architecture

## Product boundary

FileOp is a storage operating layer for Windows, not a generic "PC cleaner". The central architectural rule is that search, browsing, storage analytics, duplicate discovery and cleanup should consume a shared filesystem model instead of independently scanning disks.

## Initial component model

```text
WinUI application
      |
      v
FileOp.Core
  |-- query parser
  |-- IFileIndex
  |-- storage snapshot service
  |-- bootstrap filesystem crawler
      |
      +---- future NTFS MFT/USN source
```

`IFileIndex` is intentionally independent of how records are discovered. The bootstrap crawler uses ordinary Windows/.NET enumeration so the vertical slice works immediately. It must not become the permanent NTFS implementation.

## Target indexing architecture

```text
NTFS volume
  |-- initial MFT read --------------------+
  |-- USN change journal ------------------|----> metadata pipeline
                                           |          |
Other filesystem provider ----------------+          v
                                                persistent index
                                                   /   |   \
                                             search analytics cleanup
```

### Constraints

- Initial NTFS discovery should read filesystem metadata rather than recurse through every directory.
- Incremental changes should be applied from the USN journal.
- File identity should eventually use volume identity + file ID, not path alone.
- Allocated size, hard links, sparse/compressed state and reparse points must be represented explicitly.
- Hashes and content extraction are lazy workloads and must not delay filename/path availability.
- Search results should remain responsive while indexing continues.
- Network/removable/non-NTFS volumes use provider-specific fallbacks rather than pretending MFT semantics exist everywhere.

## Privilege boundary

The desktop UI should run as the user. A future administrative helper will expose a narrow, versioned command surface for operations that really require elevation (partition changes, selected disk operations, etc.). The helper should start only after explicit user action and exit after the operation completes.

## Roadmap

### Milestone 1 — vertical slice

- WinUI shell
- safe folder crawler
- in-memory metadata index
- debounced search
- basic `ext:` and `size:` query filters
- disk-capacity overview

### Milestone 2 — fast NTFS engine

- volume discovery
- MFT parser/provider
- file-ID-based hierarchy reconstruction
- USN journal catch-up/tailing
- persistent metadata store
- benchmark harness with multi-million-record synthetic data

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
