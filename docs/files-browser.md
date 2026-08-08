# Indexed Files browser

## Purpose

The **Files** surface is a read-only indexed directory browser built on the same metadata source as Search and Storage. It does not launch another recursive filesystem enumerator.

Native mode browses the primary whole-volume NTFS index. Fallback mode browses the completed user-profile crawler snapshot. Neither UI path calls `Directory.Enumerate*`, `GetFiles`, `GetDirectories`, `FileSystemWatcher` or another filesystem scan.

## Exact page boundary

Files consumes protocol-v6 `BrowseDirectory` directly:

```text
FileOp.App Files
    -> DesktopSearchEngine.BrowseDirectoryAsync
        -> native protocol BrowseDirectory
        -> or completed in-memory fallback snapshot
```

The page model is:

```text
DirectoryPath
TotalCount
Entries[]
NextCursor?
```

Each entry is direct `FileRecord` metadata. Recursive folder sizes and category aggregates remain Storage responsibilities rather than making every browse page an analytics query.

The visible table therefore shows name, type, logical/allocated size for files and last-write time. Directory size columns intentionally show `—`.

## Dual-pane and tab state

Files renders two simultaneously visible panes, **Left** and **Right**. Each pane owns its own tabs, active tab, browse generation and active-load generation. Each tab owns its path, accumulated page rows, live count, continuation cursor and loaded-for-source state.

The two panes share the same `DesktopSearchEngine` and the same authenticated helper session. Actual browse requests still pass through `_storageGate`, so they are serialized before entering the single request/response pipe. Presentation state remains independent: navigation or paging in one pane does not invalidate the other pane.

A page result is accepted only when both the pane generation and active tab still match the request. Stale work that waited behind the other pane is discarded before transmission once it reaches the gate; already-transmitted helper requests are allowed to complete and their stale results are ignored.

Source transitions clear paths and page caches across every tab because the available root may change. Temporary index maintenance clears cached page state and selection while retaining tab paths, so active tabs can reload when the same source becomes readable again.

## Ordering and continuation

Ordering is directories first, then normalized name and normalized path. Continuation uses a keyset cursor containing the last returned row's directory/file kind, name and absolute path. Native SQL requests `pageSize + 1`; the extra row only proves another page exists and is not returned.

Each pane requests 256 rows per page. `Load more` is visible only while `NextCursor` exists. Returned pages are appended in service order rather than resorted in the UI, and duplicate paths are defensively suppressed case-insensitively.

`TotalCount` is live per page. The UI distinguishes a completed sequence from a count mismatch caused by the directory changing while pages were being read rather than claiming that an older total is authoritative.

## Selection semantics

Each pane now uses explicit multi-selection with checkboxes. Selection belongs only to the currently active tab and is tracked by case-insensitive absolute path.

Selection is preserved when more pages are appended. It is deliberately cleared when switching tabs, refreshing, navigating to another directory, becoming unavailable during index maintenance, or changing the underlying source. This conservative boundary prevents future mutation work from silently carrying a selection into a different navigation or namespace context.

Single-click is reserved for selection. Opening a directory or file is an explicit **Open** action and is enabled only when exactly one entry is selected. Opening a directory still navigates through the indexed browse path; opening a file still uses the existing shell-open behavior.

## Prepared operation intent

The center command strip can capture either **Left → Right** or **Right → Left** intent. Preparing an intent takes an immutable snapshot of:

```text
Source pane
Source tab
Source directory
Selected entry paths/names/kinds
Destination pane
Destination tab
Destination directory
```

Preparing intent does **not** touch the filesystem. The visible Copy and Move controls remain disabled. Any pane/tab/path/selection readiness change clears the prepared intent so a future executor cannot act on an outdated selection or destination.

This establishes the UI/state boundary needed by later mutation work without prematurely defining collision handling, queue ownership, cancellation, rollback or undo.

## Native and fallback behavior

Native browsing opens SQLite read-only, takes the existing shared cross-process lease and requires a valid durable checkpoint. When the requested directory has a stable identity, direct children are filtered by the existing parent-identity columns/index rather than by a whole-index path scan.

`DesktopSearchEngine.BrowseDirectoryAsync` exposes the same page model for fallback mode. Fallback paging scans only the already-completed in-memory profile snapshot; it performs no filesystem enumeration. Native mode is the performance-critical path and does not materialize the persistent index.

## Validation without hosted Actions

`tools/verify_files_ui.py` guards the dual-pane browse, selection and intent boundary. It checks:

- exact page accumulation with duplicate-boundary suppression;
- left/right pane and per-tab isolation;
- maintenance/source invalidation;
- case-insensitive selection identity and selection preservation during page append;
- immutable prepared-intent snapshots;
- exact XAML handler wiring;
- multi-select checkbox mode and explicit Open activation;
- disabled Copy/Move execution controls;
- per-pane generation checks around `_storageGate`;
- `BrowseDirectoryAsync` usage and absence of legacy Storage-analysis or direct enumeration paths.

`tools/verify_directory_browse.py` separately covers the protocol/service keyset algorithm, read-only SQLite access, lease/checkpoint enforcement and native/fallback source wiring.

Run the Files verifier directly:

```powershell
python tools/verify_files_ui.py --repo-root . --cases 10000
```

Or run the whole standard-library suite without the .NET SDK:

```powershell
pwsh -File tools/test-local.ps1 -OfflineOnly
```

Without `-OfflineOnly`, the local Windows gate continues into the .NET builds, regression tests, WinUI build, bundled-helper checks and real helper-process handshake.

## Next file-manager boundary

The next mutation-focused slice should define collision policy and a queued operation model before any filesystem write is enabled. Copy/move/delete should remain disabled until queue ownership, pause/resume/cancellation, failure recovery, action history and safe undo boundaries are designed and covered by tests.
