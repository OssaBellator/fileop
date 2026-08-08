# Indexed Files browser

## Purpose

The **Files** surface is a read-only indexed directory browser built on the same metadata source as Search and Storage. It does not launch another recursive filesystem enumerator.

Native mode browses the primary whole-volume NTFS index. Fallback mode browses the completed user-profile crawler snapshot. Neither UI path calls `Directory.Enumerate*`, `GetFiles`, `GetDirectories`, `FileSystemWatcher` or another filesystem scan.

## Exact page boundary

Files now consumes protocol-v6 `BrowseDirectory` directly:

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

The visible table therefore shows:

- name;
- folder/file or extension type;
- logical file size for files;
- exact physical allocation when known for files;
- last-write time.

Directory size columns intentionally show `—`. Invoking a directory loads its first exact page. Invoking a file uses the existing shell-open behavior. No copy, move, delete, rename or other mutation command exists in this slice.

## Ordering and continuation

Ordering is directories first, then normalized name and normalized path. Continuation uses a keyset cursor containing the last returned row's directory/file kind, name and absolute path. Native SQL requests `pageSize + 1`; the extra row only proves another page exists and is not returned.

The UI requests 256 rows per page. `Load more` is visible only while `NextCursor` exists. Returned pages are appended in service order rather than resorted in the UI, and duplicate paths are defensively suppressed case-insensitively while the current path model remains case-insensitive.

Offset paging is deliberately avoided. An insertion before an existing cursor cannot shift an already-consumed row into the next page. Inserts after the cursor may naturally appear later. A multi-page browse is therefore live and monotonic, not one transactionally frozen directory snapshot.

`TotalCount` is also live per page. The UI distinguishes a completed sequence from a count mismatch caused by the directory changing while pages were being read instead of claiming that an older page total is authoritative.

## Native and fallback behavior

Native browsing opens SQLite read-only, takes the existing shared cross-process lease and requires a valid durable checkpoint. When the requested directory has a stable identity, direct children are filtered by the existing parent-identity columns/index rather than by a whole-index path scan.

`DesktopSearchEngine.BrowseDirectoryAsync` exposes the same page model for fallback mode. Fallback paging scans only the already-completed in-memory profile snapshot; it performs no filesystem enumeration. Native mode is the performance-critical path and does not materialize the persistent index.

## UI lifecycle

`FilesView` is a standalone WinUI `UserControl`. `App.OnLaunched` constructs `MainWindow`, then calls `InitializeFilesFeature` after `MainWindow.InitializeComponent` and before activation.

The coordinator tracks:

- source key;
- current directory path;
- accumulated page rows;
- current live total count;
- next keyset cursor;
- loaded-empty state;
- visible generation;
- separate active-load generation.

New navigation invalidates stale Files work by generation. Already-transmitted helper requests are not cancelled merely because the user switches sections or directories, preserving the named-pipe synchronization rule used by Search and Storage.

The active-load generation prevents recurring engine state notifications from launching duplicate page loads or stopping/re-enabling controls while a query is active. Explicit navigation can still supersede an older load; stale completed results are discarded.

When index maintenance begins while Files is visible, cached pages are invalidated but the current path is retained. Once the same source is readable again, Files reloads the first page for that path if it remains inside the source root.

A failed navigation restores the last successfully displayed path when available. `Load more` preserves already-rendered rows while a later page is in flight; failed continuation does not pretend the page sequence completed.

## Validation without hosted Actions

`tools/verify_files_ui.py` now guards the exact paged UI rather than the old Storage-analysis compatibility bridge. It checks:

- page accumulation with duplicate-boundary suppression;
- Windows root/path containment;
- exact XAML handler wiring;
- `BrowseDirectoryAsync` usage and absence of `AnalyzeStorageAsync` from the Files coordinator;
- 256-row page policy and continuation state;
- Load-more event subscription/lifecycle;
- loaded-empty and active-load generation state;
- direct `FileRecord` row rendering;
- absence of recursive Storage row types and bounded/omitted-entry messaging;
- no direct filesystem enumeration APIs.

`tools/verify_directory_browse.py` separately covers the protocol/service keyset algorithm, read-only SQLite access, lease/checkpoint enforcement and native/fallback source wiring.

Run either directly:

```powershell
python tools/verify_files_ui.py --repo-root . --cases 10000
python tools/verify_directory_browse.py --repo-root . --cases 10000
```

Or run the whole standard-library suite without the .NET SDK:

```powershell
pwsh -File tools/test-local.ps1 -OfflineOnly
```

Without `-OfflineOnly`, the local gate continues into the .NET/Windows builds, regression tests, WinUI build, bundled-helper checks and real helper-process handshake.

## Next file-manager boundary

With exhaustive read-only directory browsing established, the next useful file-manager slice is navigation state: tabs and then dual-pane behavior can build on exact pages without depending on Storage analytics.

Mutation workflows should still wait until selection semantics, collision policy, queued operations, pause/resume and safe undo boundaries are separately designed and reviewed.
