# FileOp storage analytics

## Purpose

Storage analytics consumes FileOp's existing filesystem metadata index. It must not recursively rescan the filesystem merely to answer folder-size, treemap, file-type or category questions that the shared index can already answer.

`IStorageAnalytics` exposes two read-only views over the same indexed namespace:

- `AnalyzeDirectoryAsync` returns a bounded list of direct entries plus complete subtree totals.
- `AnalyzeFileTypesAsync` returns bounded normalized extension groups plus complete root totals, complete `TypeCount`, and exact category aggregates.

Neither operation opens file contents or performs MIME sniffing.

## Size and hard-link semantics

FileOp distinguishes namespace size from physical disk usage:

- `LogicalBytes` sums the logical length of every visible file name.
- `AllocatedBytes` counts each stable `FileIdentity` at most once inside the analysis root.
- `AllocatedBytes` is nullable; unknown physical metadata stays unknown.
- `HardLinkAliasCount` counts non-canonical namespace names whose allocation is attributed elsewhere.
- `UniqueFileCount = FileCount - HardLinkAliasCount`.

Within one analysis, the case-insensitive lexicographically first indexed path for a repeated `FileIdentity` owns physical allocation. Every hard-link name still contributes logical namespace bytes and a file-name count.

When physical metadata is complete, directory, extension and category allocated totals add back to the root. A row containing only non-canonical aliases can have non-zero logical bytes and zero physical bytes.

## Directory aggregation

A request for `C:\Data` returns the direct children of `C:\Data`. Direct directories recursively include indexed descendants.

Each `StorageDirectoryEntry` reports path/name, directory state, logical bytes, nullable allocated bytes, file count, directory count and hard-link aliases.

`MaxEntries` limits only the direct-entry list. Root totals and `DirectEntryCount` are calculated before truncation, allowing the UI to derive an exact `Other entries` remainder.

## File-type and exact-category aggregation

`AnalyzeFileTypesAsync` groups files by normalized extension. Extensions are lowercase without a leading dot; no-extension files use the empty string.

Each bounded `StorageFileTypeEntry` reports extension, deterministic metadata-only category, logical bytes, nullable allocated bytes, file count and hard-link aliases.

`MaxTypes` limits only extension rows. Complete root totals and distinct `TypeCount` are calculated before truncation. Exact category rows are also aggregated over the complete extension set, independently of `MaxTypes`.

The category set is `NoExtension`, `Documents`, `Images`, `Video`, `Audio`, `Archives`, `Applications`, `Code`, `Data`, `DiskImages`, `Fonts` and `Other`.

Classification is deterministic and extension-only. Ambiguous extensions use one stable policy; `.ts` is Code while `.mts` / `.m2ts` are Video.

Exact category invariants are:

```text
sum(category logical bytes) = root logical bytes
sum(category file counts)   = root file count
sum(category aliases)       = root alias count
sum(category type counts)   = root TypeCount
sum(category allocated)     = root allocated bytes   // when complete
```

## SQLite implementation

`SqliteStorageAnalytics` operates directly on the persistent `files` table. Recursive CTEs walk descendants and a stable-identity window rank selects one physical-allocation owner for hard links.

The type/category query performs the expensive subtree aggregation once:

1. build recursive subtree;
2. rank physical owners;
3. aggregate complete extension rows;
4. classify through deterministic `fileop_category`;
5. emit only top `MaxTypes` extension rows;
6. emit all exact category rows from the complete extension set.

No second filesystem scan or second recursive subtree traversal is required for categories.

Live service analytics acquire the process-local volume gate and shared cross-process read lease before validating the durable NTFS checkpoint, so a partially rebuilt index cannot be exposed as a valid live result.

## In-memory fallback

`InMemoryFileIndex` implements the same accounting over the completed crawler snapshot. It derives exact categories from complete pre-truncation extension aggregates and only then applies `maxTypes` to extension rows.

The normal crawler lacks stable NTFS identities and allocated sizes, so fallback physical allocation is generally unknown and the UI uses logical weighting consistently.

## Protocol history

Protocol v3 introduced `AnalyzeStorageTypes`. Protocol v4 enriched its response with exact category rows while keeping the request shape unchanged:

```text
VolumeIdentity
VolumeRootPath
DirectoryPath
MaxTypes
```

Protocol v5 preserves both live Storage operation shapes and their exact-category semantics. v5 only adds separate aggregate-history capture/query operations.

Live Storage targets must remain inside the selected volume root and require a valid durable checkpoint. Maintenance contention returns retryable `Busy`; invalid snapshots return `SnapshotRequired`.

`CaptureStorageHistory` in v5 reuses `AnalyzeStorageTypes(MaxTypes = 1)` as its trusted live aggregation boundary because category exactness is independent of the extension-row limit. See `docs/storage-history.md` for capture/query semantics.

## WinUI consumption

The Storage page has **Folders** and **Types** modes sharing one current path and one native/fallback coordinator.

The Types table may display only the top extensions, but category bars always consume exact category rows. Physical bars are used only when root physical allocation is complete; otherwise the chart uses logical size consistently.

Search, folder analytics and type/category analytics share the same foreground/native lifecycle. Superseding UI work invalidates generations instead of cancelling already-transmitted IPC.

History is not yet scheduled or rendered by WinUI.

## Performance and offline validation

`StorageAnalyticsBenchmarks` seeds deterministic 100,000- and 1,000,000-file SQLite indexes and measures directory and file-type/category aggregation.

`tools/verify_storage_types.py` executes the committed SQL against deterministic fixtures. `tools/verify_storage_types_fuzz.py` compares it with an independent reference model over randomized nested-directory/hard-link/nullable-allocation data.

`tools/verify_storage_ui.py` / `verify_storage_ui_edgecases.py` guard presentation invariants, while `verify_storage_history_service.py` guards the new protocol-v5 history reuse of exact category analytics.

The recursive SQLite implementation remains the correctness baseline, not an assumption that it is the final WizTree-class analytics engine. Measurements can later justify incremental aggregates or another specialized structure without changing public accounting semantics.
