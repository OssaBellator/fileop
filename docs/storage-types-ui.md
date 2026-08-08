# Storage Types and Categories UI

## Purpose

The Storage **Types** view presents the file-type analytics introduced by indexing protocol v3 without adding another scanner, helper session, or database reader to `FileOp.App`.

Both Storage modes consume the same active metadata source:

- **Folders** uses recursive direct-child `AnalyzeStorage` data for the treemap and folder table.
- **Types** uses `AnalyzeStorageTypes` for extension and category breakdowns under the same current Storage path.

The active source remains either the whole indexed NTFS volume or the existing profile crawler snapshot. Switching views does not crawl the filesystem again.

## Navigation and caching

Folders and Types share one current Storage path. **Up** and **Refresh** act on the active mode.

Each mode keeps its own last analysis result. Switching modes reuses that cached result only when its `RootPath` still matches the current Storage path and the native/fallback source key has not changed. Otherwise the selected mode requests fresh analytics from the shared source.

Only the visible mode is fetched. Opening a folder in Folders does not automatically request Types, and switching to Types does not automatically re-run the folder analysis.

Folder and type requests share the existing Storage UI semaphore and the desktop engine's foreground/native gates. Newer view/navigation work invalidates the older generation instead of cancelling an already-transmitted named-pipe exchange, preserving the reviewed IPC session rule.

When native/fallback source ownership changes while Types is visible, the Types-aware state handler runs before the existing folder-only handler. It marks the old folder source cache as transitioned so the original handler does not queue an unnecessary folder request immediately before the Types request. Folder mode retains the existing source-transition behavior unchanged.

## Summary cards

The existing Storage summary cards remain authoritative in both modes:

- logical size;
- exact on-disk allocation when complete, otherwise `Unknown`;
- unique physical files;
- hard-link aliases.

This keeps folder and type presentations anchored to the same accounting model.

## Type table

The Types table shows each returned normalized extension with:

- extension display name, including an explicit `(no extension)` row;
- deterministic metadata-only category;
- logical bytes;
- nullable physical allocation;
- namespace file count;
- hard-link alias count.

The UI does not perform MIME sniffing or open file contents. Classification is the reviewed engine policy from `StorageFileCategoryClassifier`.

## Category bars

Category bars use one coherent unit for the whole analysis:

- when root `AllocatedBytes` is complete, bars represent hard-link-deduplicated physical allocation;
- otherwise every bar uses logical bytes.

The view never mixes physical and logical bytes in one chart.

If every distinct type was returned, category totals are labeled **Exact category totals**.

If `TypeCount` is larger than the returned bounded type list, the UI does **not** pretend that category totals from the top-N extension rows are complete. The represented categories are shown, and the missing root weight is surfaced as an unclassified `Other N types (not returned)` remainder when it has non-zero weight. The header always states `Top N of M types` and explicitly says the omitted remainder is unclassified.

A truncated omitted set can have zero physical weight when it consists only of hard-link aliases whose allocation is attributed to another returned extension. In that case the `Top N of M` status remains the completeness signal even though there is no positive-size remainder bar.

## Hard-link semantics

Types inherits the same namespace-versus-physical accounting as the engine:

- every visible hard-link name contributes logical bytes and a file-name count to its extension;
- physical allocation is attributed once per stable `FileIdentity`;
- non-canonical names increment alias counts and may legitimately have zero physical allocation.

The UI therefore does not interpret a zero-byte physical type/category row as an empty file. It can represent namespace names whose storage is already accounted for elsewhere in the analysis root.

## Native and fallback behavior

`DesktopSearchEngine.StorageTypes.cs` routes the Types request through the same private foreground/native gates used by Search and folder Storage.

Native mode calls protocol-v3 `AnalyzeStorageTypes`. `ResponseTooLarge` is handled the same way as directory Storage by retrying with a smaller bounded type count while preserving the healthy helper session.

Fallback mode calls `InMemoryFileIndex.AnalyzeFileTypesAsync` over the already-built profile snapshot. No second crawler is started.

## Validation without hosted Actions

`tools/verify_storage_ui.py` now understands the split `MainWindow*.cs` and `DesktopSearchEngine*.cs` partial files. Its repository mode checks the new XAML controls/event handlers and guards both native/fallback type-analysis routes in addition to the existing folder Storage invariants.

Its pure standard-library test path covers:

- 2,011 binary-treemap geometry cases;
- 4 treemap truncation/root-coverage cases;
- 4 category aggregation/truncation cases;
- 9 Windows path-containment cases.

`tools/test-local.ps1` already runs this verifier before the full Windows build/test/WinUI/helper-handshake sequence, so no additional local command is required for this UI slice.

The review branch uses the repository's established `offline/**` convention while hosted Actions usage is unavailable. The pull request head is committed with `[skip actions]`; this suppresses the `pull_request` workflow but is not counted as a compiler/runtime validation result.
