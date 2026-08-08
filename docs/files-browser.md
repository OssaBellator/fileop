# Indexed Files browser

## Purpose

The first File manager slice turns the existing **Files** navigation placeholder into a read-only indexed directory browser. It deliberately reuses the same metadata source as Search and Storage instead of adding another filesystem enumerator.

Native mode browses the primary whole-volume NTFS index. Fallback mode browses the completed user-profile crawler snapshot. The Files surface does not call `Directory.Enumerate*`, `GetFiles`, `GetDirectories`, `FileSystemWatcher` or another recursive scanner.

## Current UI compatibility bridge

The currently rendered Files UI still routes through the reviewed `AnalyzeStorage` path:

```text
FileOp.App Files
    -> DesktopSearchEngine.AnalyzeStorageAsync
        -> native protocol AnalyzeStorage
        -> or InMemoryFileIndex.AnalyzeDirectoryAsync fallback
```

That gives the browser indexed direct children plus recursive per-entry metadata while preserving the existing foreground/native gates, checkpoint validation, cross-process lease behavior and fallback policy.

The rows show directory/file name, type, recursive logical/allocated size for folders, file size for files, indexed descendant counts and hard-link alias status where relevant. Invoking a directory drills down without scanning disk. Invoking a file uses the existing shell-open path. No copy, move, delete or mutation command exists yet.

## Protocol v6 exact browse boundary

Protocol v6 now adds the dedicated `BrowseDirectory` operation that the Files UI needs to replace the compatibility bridge.

The exact page model contains:

```text
DirectoryPath
TotalCount
Entries[]
NextCursor?
```

Each entry is direct `FileRecord` metadata. Recursive folder sizes are intentionally excluded so directory browsing does not become an analytics query.

Ordering is directories first, then normalized name and normalized path. Continuation uses a keyset cursor containing the last row's directory/file kind, name and absolute path. The native SQL requests `pageSize + 1`; the extra row only proves another page exists and is not returned to the caller.

Native browsing opens SQLite read-only, takes the existing shared cross-process lease and requires a valid durable checkpoint. When the directory has a stable identity, its children are filtered by the existing parent-identity index rather than by a whole-index path scan.

`DesktopSearchEngine.BrowseDirectoryAsync` exposes the same page contract for fallback mode. Fallback paging reads the already-completed in-memory profile snapshot and performs no filesystem enumeration. It can scan that in-memory snapshot per page; native mode is the performance-critical path and does not materialize the persistent index.

## Why keyset rather than offset paging

Offset paging is unstable when the live namespace changes between requests. If a row is inserted before offset 256, the next `OFFSET 256` query can repeat a row the caller already consumed; deletion before the offset can skip one.

The v6 cursor advances after the last observed ordering key instead. Inserts before that cursor do not shift consumed rows into the next page. Inserts after it may naturally appear in a later page. This is deliberate live-index behavior, not a claim that a multi-page browse is one database snapshot transaction.

## Existing bounded-UI limitation

Until the UI migrates to protocol v6, `AnalyzeStorage` still limits the visible compatibility view to at most 4,096 direct entries and may reduce that result further if a named-pipe response would exceed the frame cap. The service chooses the bounded subset using Storage weight ordering before the Files UI reorders the returned subset alphabetically.

Therefore a directory whose `DirectEntryCount` exceeds the returned row count is **not yet a complete alphabetical UI listing**. The Files view says `Showing N of M` and identifies the result as bounded rather than hiding the omission.

This limitation is now isolated to presentation wiring rather than the service model. The next Files UI slice should consume `BrowseDirectory` incrementally and remove the Storage-analysis bridge.

## UI lifecycle

`FilesView` is a standalone WinUI `UserControl`. `App.OnLaunched` constructs `MainWindow`, then calls `InitializeFilesFeature` after `MainWindow.InitializeComponent` has completed and before activation.

The initializer:

- reuses the existing disabled Files navigation placeholder;
- inserts `FilesView` into the same main content grid as Search and Storage;
- leaves the large `MainWindow.xaml` unchanged;
- subscribes to the shared engine state lifecycle;
- tracks a Files-specific source key, current path, cached analysis and visible generation.

New navigation invalidates stale Files work by generation. Already-transmitted helper requests are not cancelled merely because the user switched sections or directories, preserving the named-pipe synchronization rule used by Search and Storage.

When index maintenance begins while Files is visible, the cached directory result is invalidated but the current path is retained. Once the same source becomes readable again, Files refreshes that path if it remains inside the source root.

## Validation without hosted Actions

`tools/verify_files_ui.py` continues to guard the current UI bridge. `tools/verify_directory_browse.py` covers the new exact service boundary with randomized SQLite keyset paging, read-only/lease/checkpoint source checks, protocol/client/dispatcher wiring and native/fallback no-rescan guards.

Run either directly:

```powershell
python tools/verify_files_ui.py --repo-root . --cases 10000
python tools/verify_directory_browse.py --repo-root . --cases 10000
```

The local gate also supports verifier-only operation when the .NET SDK is unavailable:

```powershell
pwsh -File tools/test-local.ps1 -OfflineOnly
```

Without `-OfflineOnly`, `tools/test-local.ps1` continues into the .NET/Windows builds, regression tests, WinUI build, bundled-helper checks and real helper-process handshake.

## Next file-manager boundary

The next high-value file-manager change is now the **UI migration to exact pages**: replace `AnalyzeStorageAsync` in `MainWindow.Files.cs` with `BrowseDirectoryAsync`, maintain the continuation cursor, and expose an explicit Load more or incremental-scroll behavior.

After that migration, tabs and dual-pane navigation can be added on top of an exhaustive browser API. Mutation workflows should still wait until selection semantics, collision policy, operation queues and safe undo boundaries are separately designed and reviewed.
