# FileOp storage analytics

## Purpose

Storage analytics consumes FileOp's existing filesystem metadata index. It must not recursively rescan the filesystem merely to answer folder-size, treemap or file-type questions that the shared index can already answer.

`IStorageAnalytics` currently exposes two read-only views over the same indexed namespace:

- `AnalyzeDirectoryAsync` returns a bounded list of direct entries under one directory plus complete totals for that directory's indexed subtree.
- `AnalyzeFileTypesAsync` returns a bounded list of normalized file-extension groups for the same subtree plus complete whole-root totals and the complete number of distinct types.

Neither operation opens file contents or performs MIME sniffing. The first file-type slice classifies only metadata already present in the shared index.

## Size semantics

FileOp distinguishes namespace size from physical disk usage:

- `LogicalBytes` sums the logical length of every file name in the analyzed namespace. Hard-linked names therefore each contribute logical bytes because each name represents a visible file entry.
- `AllocatedBytes` represents physical allocation and counts each stable `FileIdentity` at most once inside the analyzed root.
- `AllocatedBytes` is nullable. If any physical file that owns allocation in an aggregate lacks allocated-size metadata, the aggregate remains unknown rather than substituting logical size and presenting an estimate as exact physical usage.
- `TreemapBytes` is `AllocatedBytes ?? LogicalBytes`. This gives rendering a usable per-entry weight while preserving the distinction in the data model.

Files without a stable provider identity are treated as unique. That is the correct conservative behavior for crawler/fallback providers that cannot prove two paths refer to the same physical file.

## Hard links

NTFS hard links create multiple namespace rows for one physical file. Naively summing `allocated_length` for every row would overstate disk usage and could make child treemap areas or file-type totals add up to more than the analyzed root.

Within one analysis request, FileOp chooses a deterministic canonical path for each repeated `FileIdentity`: the case-insensitive lexicographically first indexed path inside the analyzed scope. Physical allocation is attributed to that path exactly once. Other namespace rows remain in `LogicalBytes` and `FileCount` but increment `HardLinkAliasCount` instead of consuming allocation again.

This produces these invariants:

```text
UniqueFileCount = FileCount - HardLinkAliasCount
sum(direct child allocated bytes) = root allocated bytes   // when allocation is fully known
sum(file-type allocated bytes) = root allocated bytes      // when allocation is fully known
```

A directory containing only a non-canonical hard-link alias can therefore have non-zero logical bytes but zero allocated bytes for this analysis. The same rule applies to file types: if one physical file is named `shared.jpg` and `shared.png`, both extension groups receive logical namespace bytes, while physical allocation is attributed only to the extension of the canonical path. The other extension reports a hard-link alias.

Canonical attribution is a presentation/accounting rule, not an assertion that one hard-link name owns the physical file more strongly than another.

## Directory aggregation

A request for `C:\Data` returns the direct children of `C:\Data`. For a direct directory such as `C:\Data\Projects`, its row recursively includes all indexed descendants below `Projects`.

Each entry reports:

- path and display name;
- file/directory identity of the entry itself (`IsDirectory`);
- logical bytes;
- nullable physical allocated bytes;
- namespace file count;
- unique physical file count derived from hard-link aliases;
- directory count;
- hard-link alias count.

Directory counts include the direct directory itself plus descendant directories. Root totals exclude the analyzed root itself and summarize the returned direct-entry subtrees.

`MaxEntries` limits only the direct-entry list. Root totals and `DirectEntryCount` are calculated before truncation so the UI can display exact totals while rendering only the largest entries.

## File-type aggregation

`AnalyzeFileTypesAsync` walks the same indexed subtree but groups files by normalized extension. Extensions are lowercase and do not include the leading dot; files without an extension use the empty string.

Each `StorageFileTypeEntry` reports:

- normalized extension;
- deterministic metadata-only `StorageFileCategory` classification;
- logical bytes;
- nullable physical allocated bytes;
- namespace file count;
- unique physical file count derived from hard-link aliases;
- hard-link alias count.

The current categories are `NoExtension`, `Documents`, `Images`, `Video`, `Audio`, `Archives`, `Applications`, `Code`, `Data`, `DiskImages`, `Fonts` and `Other`.

Classification is intentionally extension-based and deterministic. It does not claim to identify content type from bytes. Ambiguous extensions need one stable policy; for example `.ts` is treated as TypeScript/`Code`, while `.mts` and `.m2ts` remain video transport-stream extensions.

`MaxTypes` limits only the returned extension list. `LogicalBytes`, `AllocatedBytes`, `FileCount`, `HardLinkAliasCount` and `TypeCount` describe the complete analyzed subtree before truncation. The returned list is ordered by physical allocation when root allocation is complete, otherwise by logical bytes so a single result set does not mix incompatible ranking units.

Each extension carries a category label, but this first API does not claim an exact category rollup from a truncated top-N type list. A future category-summary surface should aggregate categories before truncation rather than summing only the returned type rows.

## SQLite implementation

`SqliteStorageAnalytics` operates directly on the `files` table used by persistent search. Recursive CTEs walk descendants from direct-child rows. Window ranks over stable file identity select one physical-allocation owner for hard-linked names before directory or extension aggregation.

The file-type query groups by the persisted `extension_norm` column; it does not read file contents and does not need another filesystem pass.

The queries are read-only. The indexing service acquires the existing process-local volume operation gate and a shared cross-process volume-index lease before checking the durable NTFS checkpoint and executing analytics. Snapshot rebuild and journal synchronization hold the exclusive lease, so analytics cannot observe a partially rebuilt namespace.

An additive `parent_path` index accelerates recursive child lookup. It does not change the persisted row shape or schema-version contract.

## In-memory fallback

`InMemoryFileIndex` implements the same `IStorageAnalytics` contract over the completed crawler snapshot. It applies the same subtree, extension normalization, category classification and canonical hard-link rules whenever stable identities are present.

The normal crawler currently does not supply stable NTFS identities or allocated sizes, so fallback results generally report logical size and unknown allocation. The UI preserves that distinction rather than claiming the fallback snapshot knows exact physical usage.

The desktop does not run another fallback crawl when the user opens Storage. It reuses the same in-memory snapshot already built for Search. The available fallback root is therefore the user profile, not the entire drive, and the Storage page labels that scope explicitly.

## Protocol

Indexing protocol v3 includes both read-only storage operations:

```text
AnalyzeStorage
  VolumeIdentity
  VolumeRootPath
  DirectoryPath
  MaxEntries

AnalyzeStorageTypes
  VolumeIdentity
  VolumeRootPath
  DirectoryPath
  MaxTypes
```

The helper verifies that the requested physical volume/root pair exists, the target directory remains inside that root, and a durable checkpoint is valid before serving either result. Invalid snapshots return `SnapshotRequired`; maintenance contention returns retryable `Busy`.

Protocol v3 is a deliberate version bump because `AnalyzeStorageTypes` is a new wire operation. Desktop and helper are shipped together; an older adjacent helper fails version negotiation and follows the existing native-unavailable/fallback path instead of receiving an unknown operation under an unchanged version.

Neither storage operation adds cleanup, deletion, partition, formatting, BitLocker, or other destructive capabilities to `FileOp.Indexer`.

## WinUI consumption

`DesktopSearchEngine` is the desktop's shared foreground coordinator for Search and the existing directory Storage view. Storage does not start a second helper session and does not open the SQLite database directly.

In native mode, the coordinator exposes the root of the indexed NTFS volume containing the user profile and forwards directory analysis to `AnalyzeStorage`. In fallback mode, it exposes the already-indexed user-profile root and forwards analysis to the same `InMemoryFileIndex` used by Search.

Search and Storage both pass through the coordinator's foreground operation gate. This matters because the reviewed native-to-fallback transition waits that gate before disposing the active helper session. A storage read therefore cannot race helper teardown any more than a search request can.

The current WinUI Storage page provides:

- whole-root logical/on-disk/unique-file/hard-link summary cards;
- Up and Refresh navigation;
- click-through folder drill-down;
- a detailed direct-entry list;
- a proportional binary treemap of the largest returned entries;
- an exact root-total `Other entries` remainder when rendered or service-returned children are truncated.

The file-type engine/service contract is intentionally landed separately from its WinUI presentation. A later UI slice can add a Types/Categories view without reopening aggregation semantics or protocol correctness.

A newer Storage navigation request supersedes the visible generation but does not cancel an already-transmitted IPC exchange. This mirrors Search's reviewed request/response rule: interrupting an exchange faults that helper connection, so supersession is handled by discarding stale work instead. Window shutdown may cancel the active exchange because the entire helper session is being destroyed.

### Treemap weighting

If the analyzed root has complete `AllocatedBytes`, tile area represents hard-link-deduplicated physical allocation.

If root allocated size is unknown, the UI switches **all** tile weights to logical bytes for that render. It does not mix known physical allocation for some branches with logical fallback for others, because that would produce a visually non-additive treemap with no coherent root total.

Entries whose physical allocation is zero because they contain only non-canonical hard-link aliases may therefore disappear from a physical treemap while remaining visible in the detailed list. That is expected: the list describes namespace entries, while the physical treemap describes disk allocation.

## Performance and offline validation

`StorageAnalyticsBenchmarks` seeds deterministic 100,000- and 1,000,000-file SQLite indexes and measures root/project directory aggregation plus root/project file-type aggregation. The synthetic dataset spans multiple extension/category families. Long BenchmarkDotNet runs remain manual.

`tools/verify_storage_types.py` is a Python-standard-library-only semantic gate for the new file-type query. It executes representative SQLite fixtures without a .NET SDK or GitHub Actions and checks nested recursion, extension normalization, cross-extension hard-link allocation, unknown allocation with result truncation, sibling-root exclusion and empty-root behavior. In repository mode it also guards protocol/client/dispatcher/backend wiring and checks that its SQL fixture has not drifted from `SqliteStorageAnalytics.FileTypeAnalysisSql`.

`tools/test-local.ps1` runs both offline Storage verifiers before reproducing the normal Core/native/indexer/tests/WinUI build and real bundled-helper handshake on a Windows development machine without consuming hosted Actions usage.

The current recursive SQLite implementation is the correctness/reference baseline, not an assumption that it is the final WizTree-class aggregation engine. If measurements require it, FileOp can later maintain incremental directory/type aggregates or another specialized structure while retaining the same `IStorageAnalytics` semantics.
