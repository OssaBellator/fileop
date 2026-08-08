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

The operation plan/intent records and operation/collision enums live in `FileOp.Core.Operations`; the WinUI surface consumes that shared domain model instead of owning a second App-only copy.

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

`FileOp.Core.Operations` defines the execution boundary without providing an implementation that can mutate files.

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

User cancellation has a single cancellation path. Validation can be cancelled immediately; once a mutation is running, `RequestCancellationAsync` drives the snapshot to `CancellationRequested` and the executor settles it only at an entry-safe boundary. `ExecuteAsync` intentionally has no arbitrary cancellation token, preventing a second API from bypassing those safe-boundary semantics.

If a late cancellation request arrives while the final in-flight entry is completing, safe-boundary settlement reports `Succeeded` when every entry is already done rather than falsely labelling a fully completed operation as cancelled.

The cancellation token on `RequestCancellationAsync` only controls the request call itself; it is not the cancellation mechanism for the filesystem mutation being requested to stop.

Failed snapshots do not have an in-place retry transition. A retry creates a new plan after current source/destination state is revalidated. This avoids pretending that replaying a partially completed multi-entry operation is automatically safe or idempotent.

There is still no concrete `IFileOperationExecutor` implementation, no running queue coordinator and no filesystem mutation call.

## Read-only live preflight

`IFileOperationPreflightValidator` and the Windows `WindowsFileOperationPreflightValidator` add a dry-run boundary between queued intent and any future executor. Preflight reads current path metadata only; it does not enumerate directories and does not write, rename, delete or create files.

The Windows path probe uses `File.GetAttributes` on individual captured paths. It classifies each lookup as file, directory, missing, inaccessible or error and records whether the inspected path itself is a reparse point.

Before classifying collisions, preflight rechecks the captured invariants against live state:

- source and destination roots must still be existing ordinary directories;
- source and destination roots cannot themselves be reparse points;
- source and destination roots must differ;
- each source must still be a direct child of the captured source root;
- the current source leaf name must match the captured name;
- the live source kind must still match the captured file/directory kind;
- source entries that are reparse points are blocked;
- alternate-data-stream names are blocked;
- a directory cannot target itself or a lexical descendant.

Collision classification is deliberately non-destructive:

```text
Destination missing      -> Ready
Destination exists + Ask -> NeedsDecision
Destination exists + Skip -> Skip
Destination exists + Stop -> Blocked
Destination inaccessible/error -> Blocked
```

A `Ready` preflight result means only that the plan can proceed to a later execution-grade validation. It is **not execution authorization**. The current check does not claim to prove canonical ancestry through every possible junction/reparse ancestor, and any real executor must re-resolve and revalidate paths immediately before mutation.

The preflight probe is cancellable because it performs read-only inspection. That cancellation mechanism is separate from the safe-boundary mutation cancellation contract because no mutation has begun.

### Queue preflight UI

The Files queue exposes **Preflight selected** for one selected planned operation. The action runs `IFileOperationPreflightValidator` against the immutable queued plan and stores a timestamped result keyed by that plan ID.

The queue row displays one of:

```text
Not checked
Ready for later validation · <time>
Needs decision · <time>
Blocked · <time>
```

The timestamp matters: this is a point-in-time observation of live metadata, not a durable guarantee. Later pane navigation does not rewrite the result because the check belongs to the captured plan rather than the current pane state. Removing a plan removes its preflight snapshot, and clearing the queue clears every snapshot.

While one read-only preflight is active, queue selection/removal/clear/preflight controls are disabled to avoid presenting competing queue mutations under the in-flight result. Preparing or adding another immutable plan remains independent of that selected-plan check.

The UI still exposes no **Run** or **Execute** operation action and does not call `IFileOperationExecutor.ExecuteAsync`. A displayed `Ready for later validation` status is deliberately worded so it cannot be mistaken for execution authorization.

## Native and fallback behavior

Native browsing opens SQLite read-only, takes the existing shared cross-process lease and requires a valid durable checkpoint. When the requested directory has a stable identity, direct children are filtered by the existing parent-identity columns/index rather than by a whole-index path scan.

`DesktopSearchEngine.BrowseDirectoryAsync` exposes the same page model for fallback mode. Fallback paging scans only the already-completed in-memory profile snapshot; it performs no filesystem enumeration. Native mode is the performance-critical path and does not materialize the persistent index.

## Validation without hosted Actions

`tools/verify_files_ui.py` guards the dual-pane browse, selection, prepared-intent, queue and preflight-UI boundaries. It checks exact paging, pane/tab isolation, selection lifecycle, immutable plans, collision policy UI, timestamped preflight snapshots, snapshot removal/clear behavior, handler wiring, the absence of Run/Execute controls and the absence of filesystem mutation APIs.

`tools/verify_file_operation_state.py` independently models the execution state machine with randomized transitions and source guards. It checks monotonic progress, terminal-state rejection, immediate pre-mutation cancellation, safe-boundary running cancellation including late cancellation, the single cancellation path, shared Core plan ownership and absence of mutation APIs.

`tools/verify_file_operation_preflight.py` models live preflight decisions and guards the committed implementation. It covers collision policy classification, source disappearance/type changes, direct-child/name/ADS checks, reparse blocking, recursive targets, inaccessible paths, read-only probing and the absence of enumeration/mutation APIs.

`tools/verify_directory_browse.py` separately covers the protocol/service keyset algorithm, read-only SQLite access, lease/checkpoint enforcement and native/fallback source wiring.

Run the Files UI verifier directly:

```powershell
python tools/verify_files_ui.py --repo-root . --cases 10000
```

Or run the whole standard-library suite without the .NET SDK:

```powershell
pwsh -File tools/test-local.ps1 -OfflineOnly
```

Without `-OfflineOnly`, the local Windows gate continues into the .NET builds, regression tests, WinUI build, bundled-helper checks and real helper-process handshake.

## Next file-manager boundary

The next slice should define execution-grade canonical-path validation and durable action-history/undo records before actual Copy is enabled. Preflight results must be revalidated immediately before any later mutation. Move should remain later still because partial cross-volume moves combine copy and deletion failure modes.
