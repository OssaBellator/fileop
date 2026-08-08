# FileOp storage analytics

## Purpose

Storage analytics consumes FileOp's existing filesystem metadata index. It must not recursively rescan the filesystem merely to answer folder-size, treemap, file-type or category questions that the shared index can already answer.

`IStorageAnalytics` exposes two read-only views over the same indexed namespace:

- `AnalyzeDirectoryAsync` returns a bounded list of direct entries under one directory plus complete totals for that directory's indexed subtree.
- `AnalyzeFileTypesAsync` returns a bounded list of normalized file-extension groups plus complete root totals, complete `TypeCount`, and exact category aggregates for the same subtree.

Neither operation opens file contents or performs MIME sniffing.

## Size and hard-link semantics

FileOp distinguishes namespace size from physical disk usage:

- `LogicalBytes` sums the logical length of every visible file name in the analyzed namespace.
- `AllocatedBytes` represents physical allocation and counts each stable `FileIdentity` at most once inside the analyzed root.
- `AllocatedBytes` is nullable. If a physical allocation owner lacks allocated-size metadata, the affected aggregate remains unknown instead of substituting logical size and presenting an estimate as exact usage.
- `HardLinkAliasCount` counts non-canonical namespace names whose physical allocation is attributed elsewhere.
- `UniqueFileCount = FileCount - HardLinkAliasCount`.

Within one analysis request, FileOp chooses a deterministic physical-allocation owner for every repeated `FileIdentity`: the case-insensitive lexicographically first indexed path in the analysis scope. Every hard-link name still contributes logical bytes and a file-name count; only the canonical path contributes allocated bytes.

This makes directory, extension and category physical totals additive to the analyzed root when allocation metadata is complete. A row that contains only non-canonical aliases can therefore have non-zero logical bytes and zero physical bytes.

Files without a stable provider identity are treated as unique. That is the conservative behavior for fallback providers that cannot prove two paths refer to the same physical file.

## Directory aggregation

A request for `C:\Data` returns the direct children of `C:\Data`. For a direct directory such as `C:\Data\Projects`, its row recursively includes all indexed descendants below `Projects`.

Each `StorageDirectoryEntry` reports path/name, directory state, logical bytes, nullable allocated bytes, file count, directory count and hard-link aliases. Directory counts include the direct directory itself plus descendants.

`MaxEntries` limits only the direct-entry list. Root totals and `DirectEntryCount` are calculated before truncation, so the UI can render only the largest entries while keeping exact whole-root accounting. The treemap uses an exact root-derived `Other entries` remainder when returned/rendered direct entries are truncated.

## File-type aggregation

`AnalyzeFileTypesAsync` groups all files below the selected root by normalized extension. Extensions are lowercase and omit the leading dot; files without an extension use the empty string.

Each bounded `StorageFileTypeEntry` reports:

- normalized extension;
- deterministic metadata-only `StorageFileCategory`;
- logical bytes;
- nullable allocated bytes;
- namespace file count;
- hard-link aliases and derived unique-file count.

`MaxTypes` limits only the returned extension rows. `LogicalBytes`, `AllocatedBytes`, `FileCount`, `HardLinkAliasCount` and complete distinct `TypeCount` describe the whole subtree before truncation.

When root physical allocation is complete, extension ranking uses physical allocation. Otherwise the whole result is ranked by logical bytes so one list does not mix incompatible units.

## Exact category aggregation

Protocol v4 adds exact category rows to the same file-type analysis result. `StorageFileTypeAnalysis.Categories` contains one `StorageFileCategoryEntry` for every category represented anywhere in the complete subtree, independently of `MaxTypes`.

Each category reports:

- category identity;
- complete logical bytes;
- nullable physical allocated bytes;
- complete namespace file count;
- complete hard-link alias count;
- complete extension-group `TypeCount` for that category.

The current categories are `NoExtension`, `Documents`, `Images`, `Video`, `Audio`, `Archives`, `Applications`, `Code`, `Data`, `DiskImages`, `Fonts` and `Other`.

Classification remains deterministic and extension-only. Ambiguous extensions use one stable policy rather than pretending content certainty; `.ts` is TypeScript/`Code`, while `.mts` and `.m2ts` are `Video`.

Category totals are not reconstructed from the bounded top-N extension list. They are aggregated from the complete extension set before the limit is applied. Therefore:

```text
sum(category logical bytes) = root logical bytes
sum(category file counts) = root file count
sum(category aliases) = root alias count
sum(category type counts) = root TypeCount
sum(category allocated bytes) = root allocated bytes   // when root allocation is complete
```

A category can legitimately have zero physical bytes when all of its names are non-canonical aliases. That is an exact result, not an omitted-data placeholder.

## SQLite implementation

`SqliteStorageAnalytics` operates directly on the `files` table used by persistent Search. Recursive CTEs walk descendants and a stable-identity window rank selects one physical-allocation owner for hard links.

The file-type/category query performs the expensive subtree and physical-identity aggregation once:

1. build the recursive subtree;
2. rank physical owners;
3. aggregate complete extension rows;
4. assign categories with the deterministic `StorageFileCategoryClassifier` exposed to SQLite as the deterministic `fileop_category` scalar function;
5. rank extensions and emit only `MaxTypes` extension rows;
6. aggregate and emit every exact category row from the complete extension set.

The final reader therefore receives two row kinds from one recursive query: bounded extension rows and a fixed-size exact category collection. It does not execute a second filesystem scan or a second recursive subtree traversal for categories.

The queries are read-only. The indexing service acquires the process-local volume gate and shared cross-process read lease before validating the durable NTFS checkpoint, so Search/Storage cannot observe a partially rebuilt index.

## In-memory fallback

`InMemoryFileIndex` implements the same semantics over the completed crawler snapshot. It builds the complete extension aggregate set, derives exact categories from that pre-truncation set, and only then applies `maxTypes` to the extension rows.

The fallback therefore needs neither a second crawler nor a second pass over filesystem contents. Its normal crawler currently lacks stable NTFS identities and allocated sizes, so physical allocation is generally unknown and the UI uses logical weighting consistently.

## Protocol v4

`AnalyzeStorage` remains unchanged. `AnalyzeStorageTypes` keeps the same request shape:

```text
VolumeIdentity
VolumeRootPath
DirectoryPath
MaxTypes
```

Protocol v4 changes the response schema by enriching `StorageFileTypeAnalysis` with exact `Categories`. The version advances even though the operation name is unchanged, because desktop and helper must not silently disagree about whether category rows are present.

The target directory must be inside the selected volume root and the volume must have a valid durable checkpoint. Maintenance contention returns retryable `Busy`; invalid snapshots return `SnapshotRequired`. The operation remains read-only and does not add cleanup, mutation, partition, formatting or BitLocker capabilities.

## WinUI consumption

The Storage page has **Folders** and **Types** modes sharing one current path and one native/fallback coordinator.

The Types table may display only the top `MaxTypes` extensions, but the category chart always consumes the exact `Categories` rows. Category bars use physical allocation only when root allocation is complete; otherwise every bar uses logical size. The header reports exact category totals separately from extension-table truncation.

Search, folder analytics and type/category analytics all pass through the same foreground/native lifecycle gates. Superseding UI work invalidates generations instead of cancelling an already-transmitted named-pipe exchange.

See `docs/storage-types-ui.md` for presentation and caching details.

## Performance and offline validation

`StorageAnalyticsBenchmarks` seeds deterministic 100,000- and 1,000,000-file SQLite indexes and measures directory and file-type aggregation. The file-type benchmark now includes exact category aggregation because categories are part of the same query/result contract.

`tools/verify_storage_types.py` is a Python-standard-library semantic/source gate tied byte-for-byte to `SqliteStorageAnalytics.FileTypeAnalysisSql`. Its deterministic fixtures verify nested recursion, exact categories, cross-extension hard links, unknown allocation with `LIMIT 1`, sibling-root exclusion and empty roots.

`tools/verify_storage_types_fuzz.py` compares the committed SQL against an independent reference model over 1,000 randomized nested-directory/hard-link/nullable-allocation fixtures, including both bounded extension rows and exact category rows.

`tools/verify_storage_ui.py` and `tools/verify_storage_ui_edgecases.py` guard the corresponding WinUI exact-category presentation. `tools/test-local.ps1` runs all offline verifiers before the full Windows Core/native/indexer/tests/WinUI/bundled-helper gate.

The recursive SQLite implementation remains the correctness baseline, not an assumption that it is the final WizTree-class analytics engine. Measurements can later justify incremental aggregates or another specialized structure without changing these public accounting semantics.
