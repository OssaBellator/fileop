# Storage Types and Categories UI

## Purpose

The Storage **Types** view presents shared-index file-type analytics without adding another scanner, helper session, or database reader to `FileOp.App`.

Both Storage modes consume the same active metadata source:

- **Folders** uses recursive direct-child `AnalyzeStorage` data for the treemap and folder table.
- **Types** uses `AnalyzeStorageTypes` for bounded extension rows plus exact category rollups under the same current Storage path.

The active source remains either the whole indexed NTFS volume or the existing profile crawler snapshot. Switching views does not crawl the filesystem again.

## Navigation and caching

Folders and Types share one current Storage path. **Up** and **Refresh** act on the active mode.

Each mode keeps its own last analysis result. Switching modes reuses that cached result only when its `RootPath` still matches the current Storage path and the native/fallback source key has not changed. Otherwise the selected mode requests fresh analytics from the shared source.

Only the visible mode is fetched. Folder and type requests share the existing Storage UI semaphore and the desktop engine's foreground/native gates. Newer work invalidates the older generation instead of cancelling an already-transmitted named-pipe exchange.

## Summary cards

The existing Storage summary cards remain authoritative in both modes:

- logical size;
- exact on-disk allocation when complete, otherwise `Unknown`;
- unique physical files;
- hard-link aliases.

## Type table

The Types table shows each returned normalized extension with extension display name, deterministic metadata-only category, logical bytes, nullable physical allocation, namespace file count and hard-link aliases.

`MaxTypes` bounds only this extension table. The UI reports either all extension groups or `Top N of M extension groups shown` when truncated.

The UI does not perform MIME sniffing or open file contents.

## Exact category bars

Protocol v4 introduced the exact `Categories` collection on `AnalyzeStorageTypes`; protocol v5 preserves that response unchanged while adding separate history operations.

The UI renders category bars directly from exact category rows rather than deriving them from returned top-N extensions. Category bars use one coherent unit for the whole analysis:

- when root `AllocatedBytes` is complete, bars represent hard-link-deduplicated physical allocation;
- otherwise every bar uses logical bytes.

An exact category may legitimately have zero physical bytes when it contains only non-canonical hard-link aliases whose allocation is attributed elsewhere.

## Native and fallback behavior

`DesktopSearchEngine.StorageTypes.cs` routes Types through the same foreground/native gates used by Search and folder Storage.

Native mode calls `AnalyzeStorageTypes` on the currently negotiated indexing protocol (v5). `ResponseTooLarge` retries can reduce the bounded extension count while preserving exact category semantics.

Fallback mode calls `InMemoryFileIndex.AnalyzeFileTypesAsync` over the existing profile snapshot. Its exact categories are grouped from complete pre-truncation extension aggregates, so no second crawler or second filesystem pass is required.

Storage history introduced in protocol v5 is currently native-only at the service boundary and is not yet presented by this view. A static crawler fallback snapshot is not silently mixed into native historical observations.

## Hard-link semantics

Types and Categories inherit the same namespace-versus-physical accounting as the engine:

- every visible hard-link name contributes logical bytes and a file-name count;
- physical allocation is attributed once per stable `FileIdentity` to the canonical path inside the analysis root;
- non-canonical names increment alias counts without adding allocation;
- category `TypeCount` counts complete extension groups, not only bounded extension rows shown in the table.

## Validation without hosted Actions

`tools/verify_storage_ui.py` checks the split `MainWindow*.cs` / `DesktopSearchEngine*.cs` source structure, XAML handlers, native/fallback type routes, treemap invariants and exact-category presentation.

Its pure standard-library path covers 2,011 binary-treemap geometry cases, treemap truncation/root coverage, exact-category coverage and Windows path containment. `tools/verify_storage_ui_edgecases.py` keeps the zero-physical hard-link category regression explicit.

`tools/verify_storage_types.py` and `verify_storage_types_fuzz.py` cover the exact category SQL contract. `tools/verify_storage_history_service.py` separately guards protocol-v5 history capture/query so history changes cannot silently alter this Types view.

`tools/test-local.ps1` runs all verifiers before the full Windows build/test/WinUI/helper-handshake sequence.
