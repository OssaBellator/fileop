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

Only the visible mode is fetched. Folder and type requests share the existing Storage UI semaphore and the desktop engine's foreground/native gates. Newer view/navigation work invalidates the older generation instead of cancelling an already-transmitted named-pipe exchange, preserving the reviewed IPC session rule.

When native/fallback source ownership changes while Types is visible, the Types-aware state handler runs before the existing folder-only handler and suppresses an otherwise redundant folder refresh. Folder mode retains the existing source-transition behavior.

## Summary cards

The existing Storage summary cards remain authoritative in both modes:

- logical size;
- exact on-disk allocation when complete, otherwise `Unknown`;
- unique physical files;
- hard-link aliases.

This keeps folder, extension and category presentations anchored to the same accounting model.

## Type table

The Types table shows each returned normalized extension with:

- extension display name, including an explicit `(no extension)` row;
- deterministic metadata-only category;
- logical bytes;
- nullable physical allocation;
- namespace file count;
- hard-link alias count.

`MaxTypes` bounds only this extension table. The UI reports either all extension groups or `Top N of M extension groups shown` when the response is truncated.

The UI does not perform MIME sniffing or open file contents. Classification is the reviewed engine policy from `StorageFileCategoryClassifier`.

## Exact category bars

Protocol v4 enriches the same `AnalyzeStorageTypes` response with an exact `Categories` collection calculated before extension truncation. The UI renders category bars directly from those rows; it no longer derives categories from the returned top-N extension list and no longer needs a synthetic `Other types` approximation.

Category bars use one coherent unit for the whole analysis:

- when root `AllocatedBytes` is complete, bars represent hard-link-deduplicated physical allocation;
- otherwise every bar uses logical bytes.

The view never mixes physical and logical bytes in one chart. Category logical/file/alias/type counts reconcile to the complete analysis root even when only one extension row is returned.

An exact category may legitimately have zero physical bytes when it contains only non-canonical hard-link aliases whose allocation is attributed to another extension/category. That row remains meaningful because it describes namespace membership while physical disk usage is accounted for elsewhere.

## Hard-link semantics

Types and Categories inherit the same namespace-versus-physical accounting as the engine:

- every visible hard-link name contributes logical bytes and a file-name count;
- physical allocation is attributed once per stable `FileIdentity` to the canonical path inside the analysis root;
- non-canonical names increment alias counts without adding allocation;
- category `TypeCount` counts complete extension groups, not only the bounded extension rows shown in the table.

## Native and fallback behavior

`DesktopSearchEngine.StorageTypes.cs` routes the Types request through the same private foreground/native gates used by Search and folder Storage.

Native mode calls protocol-v4 `AnalyzeStorageTypes`. `ResponseTooLarge` is handled by retrying with a smaller bounded extension count while preserving the healthy helper session. The exact category collection remains bounded by the fixed category enum rather than by `MaxTypes`.

Fallback mode calls `InMemoryFileIndex.AnalyzeFileTypesAsync` over the already-built profile snapshot. Its exact categories are grouped from the complete pre-truncation extension aggregates, so no second crawler or second index pass is required.

## Validation without hosted Actions

`tools/verify_storage_ui.py` understands the split `MainWindow*.cs` and `DesktopSearchEngine*.cs` partial files. It checks XAML controls/event handlers, native/fallback routing, treemap invariants and the exact-category UI source contract.

Its pure standard-library path covers:

- 2,011 binary-treemap geometry cases;
- 4 treemap truncation/root-coverage cases;
- 4 exact-category coverage cases, including extension truncation and zero-physical alias categories;
- 9 Windows path-containment cases.

`tools/verify_storage_ui_edgecases.py` keeps the zero-physical hard-link category regression explicit. `tools/verify_storage_types.py` adds 5 deterministic protocol-v4 SQL/category fixtures, and `tools/verify_storage_types_fuzz.py` checks 1,000 randomized nested-directory/hard-link/nullable-allocation cases against an independent reference model using the 142 extension patterns parsed from the committed C# classifier across all 12 category values.

`tools/test-local.ps1` runs these verifiers before the full Windows build/test/WinUI/helper-handshake sequence.

When hosted Actions usage is unavailable, review branches use the repository's `offline/**` convention and a `[skip actions]` review-head commit. This suppresses hosted workflow consumption but is not represented as a Windows compiler/runtime result.
