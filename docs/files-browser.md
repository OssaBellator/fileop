# Indexed Files browser

## Purpose

The first File manager slice turns the existing **Files** navigation placeholder into a read-only indexed directory browser. It deliberately reuses the same metadata source as Search and Storage instead of adding another filesystem enumerator.

Native mode browses the primary whole-volume NTFS index. Fallback mode browses the completed user-profile crawler snapshot. The Files surface does not call `Directory.Enumerate*`, `GetFiles`, `GetDirectories`, `FileSystemWatcher` or another recursive scanner.

## Reused read boundary

This slice does not change protocol v5. Direct-child browsing currently routes through the reviewed `AnalyzeStorage` path:

```text
FileOp.App Files
    -> DesktopSearchEngine.AnalyzeStorageAsync
        -> native protocol AnalyzeStorage
        -> or InMemoryFileIndex.AnalyzeDirectoryAsync fallback
```

That gives the browser indexed direct children plus recursive per-entry metadata while preserving the existing foreground/native gates, checkpoint validation, cross-process lease behavior and fallback policy.

The rows show:

- directory/file name;
- file type derived from the indexed name;
- recursive logical bytes for directories and logical file size for files;
- physical allocation when exact metadata is available;
- indexed descendant file/folder counts for directories;
- hard-link alias status for file rows when relevant.

Invoking a directory drills down without scanning disk. Invoking a file uses the existing shell-open path. This slice adds no copy, move, delete or other mutation command.

## Ordering and bounded-directory limitation

The existing `AnalyzeStorage` contract is optimized for Storage visualization, not general directory paging. It returns at most 4,096 direct entries and may reduce that limit further if a named-pipe response would exceed the frame cap. The service chooses the bounded subset using Storage weight ordering before the Files UI reorders returned rows into:

1. directories first;
2. case-insensitive name order;
3. case-insensitive path tie-breaker.

Therefore a directory whose `DirectEntryCount` exceeds the returned row count is **not a complete alphabetical listing**. The Files UI says `Showing N of M` and explicitly identifies the view as bounded rather than hiding the omission.

This is acceptable for the first browsing foundation because it reuses a reviewed read boundary without protocol churn, but it should not become the long-term file-manager contract. Before tabs, dual-pane workflows or destructive file operations depend on exhaustive directory selection, FileOp should add a dedicated exact paged direct-child operation with a stable ordering/cursor model.

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

`tools/verify_files_ui.py` is standard-library-only and checks:

- directory-first, case-insensitive browser ordering over randomized fixtures;
- Windows root/path containment edge cases;
- exact XAML event-handler wiring;
- Files initialization after `MainWindow` XAML construction and before activation;
- use of the existing Files placeholder and dynamic content-grid insertion;
- shared `AnalyzeStorageAsync` routing and Storage UI gate use;
- native/fallback source handling;
- generation-based stale-result suppression;
- explicit bounded-directory disclosure;
- absence of direct filesystem-enumeration APIs in the Files coordinator.

Run it directly with:

```powershell
python tools/verify_files_ui.py --repo-root . --cases 10000
```

The local gate also supports verifier-only operation when the .NET SDK is unavailable:

```powershell
pwsh -File tools/test-local.ps1 -OfflineOnly
```

Without `-OfflineOnly`, `tools/test-local.ps1` continues into the .NET/Windows builds, regression tests, WinUI build, bundled-helper checks and real helper-process handshake.

## Next file-manager boundary

The next high-value file-manager change should be a dedicated paged direct-child query contract rather than immediately adding mutations. That contract should define exhaustive stable ordering, page/cursor semantics, source-generation behavior and response-size handling. Tabs and dual-pane state can then build on an actual browser API instead of the Storage-analysis compatibility bridge.
