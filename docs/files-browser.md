# Indexed Files browser

## Purpose

The **Files** surface is an indexed dual-pane directory browser built on the same metadata source as Search and Storage. Browsing itself does not launch another recursive filesystem enumerator.

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

Each pane uses explicit multi-selection with checkboxes. Selection belongs only to the currently active tab and is tracked by case-insensitive absolute path.

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

Any pane/tab/path/selection readiness change clears the prepared intent. A prepared intent can then be converted into a queued Copy or Move plan, but preparing or queueing still performs no filesystem write.

The operation plan/intent records and operation/collision enums now live in `FileOp.Core.Operations`; the WinUI surface consumes that shared domain model instead of owning a second App-only copy.

## Planned operation queue

Files owns an in-memory, session-only planned-operation queue. Each queued row captures an immutable operation snapshot:

```text
Operation id
Queued UTC timestamp
Kind: Copy | Move
Collision policy
Prepared source/destination intent
```

Queue entries do not follow later navigation or selection changes. Removing or clearing a queued entry changes only this in-memory plan list.

Before a plan is accepted, the UI applies path-level guards:

- source and destination folders must differ;
- every selected entry must still be a direct child of the captured source folder;
- a selected directory cannot target itself or one of its descendants.

These checks are intentionally performed before any future executor exists, so invalid intent cannot become accepted queue state.

### Collision policy

The queue currently offers three non-destructive policies:

- **Ask later** — preserve the collision as an unresolved decision for a future executor/UI;
- **Skip existing** — a future executor may leave an existing destination untouched and skip that item;
- **Stop on collision** — a future executor may stop the operation before changing the colliding destination.

Destructive replacement is deliberately not a queue policy yet. Safe replacement needs explicit file-vs-directory semantics, recovery behavior and undo/history guarantees before FileOp should encode it as executable intent.

## Execution contract and state machine

`FileOp.Core.Operations` now defines the execution boundary without providing an implementation that can mutate files.

The immutable `FileOperationExecutionSnapshot` state sequence is:

```text
Planned
  -> Validating
      -> Running
          -> Succeeded
          -> Failed
          -> CancellationRequested -> Cancelled | Succeeded
      -> Failed
      -> Cancelled
  -> Cancelled
```

Planning and validation can be cancelled immediately because no filesystem mutation is in flight. Only `Running` enters `CancellationRequested`, where an executor must wait for an entry-safe boundary before settling the request.

Progress is monotonic: `CompletedEntryCount` can never decrease or exceed the plan's entry count. `Succeeded` is allowed only after every entry has completed. Failure is terminal and stores a structured code/message/path plus a `Retryable` hint.

User cancellation is deliberately distinct from arbitrary task cancellation. Validation can be cancelled immediately; once a mutation is running, normal user cancellation first becomes `CancellationRequested` and is settled only at an entry-safe boundary. If a late cancellation request arrives while the final in-flight entry is completing, safe-boundary settlement reports `Succeeded` when every entry is already done rather than falsely labelling a fully completed operation as cancelled.

The `IFileOperationExecutor` contract exposes `RequestCancellationAsync` separately. Its `ExecuteAsync` cancellation token is named `shutdownCancellationToken` to reserve that token for host/application teardown rather than normal user cancellation of an in-flight file mutation.

Failed snapshots do not have an in-place retry transition. A retry creates a new plan after current source/destination state is revalidated. This avoids pretending that replaying a partially completed multi-entry operation is automatically safe or idempotent.

There is still no concrete `IFileOperationExecutor` implementation, no running queue coordinator and no filesystem mutation call in this slice.

## Native and fallback behavior

Native browsing opens SQLite read-only, takes the existing shared cross-process lease and requires a valid durable checkpoint. When the requested directory has a stable identity, direct children are filtered by the existing parent-identity columns/index rather than by a whole-index path scan.

`DesktopSearchEngine.BrowseDirectoryAsync` exposes the same page model for fallback mode. Fallback paging scans only the already-completed in-memory profile snapshot; it performs no filesystem enumeration. Native mode is the performance-critical path and does not materialize the persistent index.

## Validation without hosted Actions

`tools/verify_files_ui.py` guards the dual-pane browse, selection, prepared-intent and queue boundaries. It checks:

- exact page accumulation with duplicate-boundary suppression;
- left/right pane and per-tab isolation;
- maintenance/source invalidation;
- case-insensitive selection identity and selection preservation during page append;
- immutable prepared-intent and queued-operation snapshots;
- same-folder, non-direct-child and recursive-directory target rejection;
- exact XAML handler wiring;
- explicit `Ask`, `Skip` and `Stop` collision policies with no replacement policy;
- per-pane generation checks around `_storageGate`;
- `BrowseDirectoryAsync` usage and absence of legacy Storage-analysis, direct enumeration or filesystem mutation paths.

`tools/verify_file_operation_state.py` independently models the execution state machine with randomized transitions and source guards. It checks monotonic progress, terminal-state rejection, immediate pre-mutation cancellation, safe-boundary running cancellation including late cancellation, shared Core plan ownership, absence of in-place retry and absence of mutation APIs.

`tools/verify_directory_browse.py` separately covers the protocol/service keyset algorithm, read-only SQLite access, lease/checkpoint enforcement and native/fallback source wiring.

Run the operation-state verifier directly:

```powershell
python tools/verify_file_operation_state.py --repo-root . --cases 20000
```

Or run the whole standard-library suite without the .NET SDK:

```powershell
pwsh -File tools/test-local.ps1 -OfflineOnly
```

Without `-OfflineOnly`, the local Windows gate continues into the .NET builds, regression tests, WinUI build, bundled-helper checks and real helper-process handshake.

## Next file-manager boundary

The next mutation slice should implement a validation-only/dry-run executor first: resolve every queued source/destination against live filesystem state, classify collisions and produce per-item decisions without writing anything. A later reviewed slice can then add actual Copy semantics, durable action history and undo records on top of that validated boundary before Move is enabled.
