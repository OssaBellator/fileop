# Indexed Files browser

## Purpose

The **Files** surface is an indexed dual-pane directory browser built on the same metadata source as Search and Storage. Browsing does not launch another recursive filesystem enumerator.

Native mode browses the primary whole-volume NTFS index. Fallback mode browses the completed user-profile crawler snapshot. Neither browse path launches `Directory.Enumerate*`, `GetFiles`, `GetDirectories`, `FileSystemWatcher` or another filesystem scan for each page.

Files now has two distinct kinds of operation UI that must not be conflated:

- a session-only **Copy/Move planning queue** whose reviewed regular-file Copy plans plus reviewed **same-volume local regular-file or homogeneous directory Move** plans can execute after successful read-only preflight and fresh execution validation; cross-volume Move and mixed file/directory Move remain non-executable;
- a separately reviewed **file-only permanent-delete session**, which is a real destructive action with its own recovery, authorization and mutation boundary.

Browse selection, prepared intent, preflight evidence and Storage handoff are never themselves mutation authority.

## Exact page boundary

Files consumes protocol-v6 `BrowseDirectory` through `DesktopSearchEngine.BrowseDirectoryAsync`:

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

The table shows direct metadata such as name, type, logical/allocated size for files and last-write time. Directory recursive sizes are not calculated as part of browse paging.

## Dual-pane and tab state

Files renders two simultaneously visible panes, **Left** and **Right**. Each pane owns independent tabs, active-tab state, browse generation and load generation. A tab owns its path, accumulated rows, live count, continuation cursor and source-loaded state.

Both panes share one `DesktopSearchEngine` and authenticated helper session. Browse requests are serialized before the single request/response pipe, while presentation state remains independent.

A page result is accepted only when the pane generation and active tab still match the request. Stale queued work is discarded before transmission when possible; an already-transmitted helper request is allowed to complete and its stale result is ignored.

Source transitions invalidate cached rows and selection conservatively so a selection cannot silently migrate into a different namespace/source context. A real backing-source identity transition also clears prepared and non-running queued operation plans. If a reviewed Copy or same-volume Move has already started, that active operation remains governed by its direct filesystem identity/history boundary and is not converted into source-token authority or automatic replay.

## Ordering and continuation

Ordering is directories first, then normalized name and normalized path. Continuation uses a keyset cursor containing the last returned row's directory/file kind, name and absolute path.

Each pane requests 256 rows per page. `Load more` is visible only while `NextCursor` exists. Pages are appended in service order and duplicate paths are defensively suppressed case-insensitively.

`TotalCount` is live per page. A directory can change between requests, so a completed page sequence is not advertised as one transactionally frozen snapshot.

## Selection semantics

Each pane uses explicit multi-selection tracked by case-insensitive absolute path for the active tab.

Selection survives page append but is cleared on navigation/tab/source transitions and other state changes that make the old selection unsafe to reuse.

Single-click selection is separate from **Open**. Opening a directory still navigates through the indexed browse path; opening a file uses the existing shell-open behavior.

## Copy / Move planning boundary

The center command strip can capture **Left -> Right** or **Right -> Left** intent as an immutable snapshot of source pane/tab/directory, selected entries and destination pane/tab/directory.

A prepared intent can become a session-only queued Copy or Move plan. Preparing or queueing a plan performs no filesystem write.

Before a plan is accepted, the UI applies path-level guards such as source/destination separation, direct-child membership and directory-recursion checks.

The queue retains explicit collision intent such as Ask later, Skip existing and Stop on collision. Destructive replacement is not silently inferred from one of those choices.

### Preflight and execution-grade contracts

The repository contains distinct reviewed operation layers for executable file Copy, same-volume file Move and same-volume directory Move:

- read-only Windows preflight;
- canonical execution-grade validation contracts;
- durable action-history/recovery models;
- a production **file Copy** executor and identity-bound Windows copy mutation primitive;
- a same-volume **file Move** executor and identity-preserving Windows handle-relative rename primitive;
- a separate same-volume **directory Move** executor, directory-specific durable history and identity-preserving no-replace Windows directory rename primitive;
- a Windows Move namespace-capability validator that rejects case-sensitive or unavailable directory capability before durable mutation history.

A queued regular-file Copy or homogeneous same-volume local file/directory Move can run only when its exact point-in-time preflight is `Ready` and the active source/destination pane, tab and directory context still matches the immutable plan. A fresh preflight already in flight also keeps all Run paths disabled even if an older Ready snapshot exists.

Each executor performs its own fresh canonical/identity validation before durable history begins and revalidates each mutation entry again before crossing `MutationStarted`. Move additionally requires both canonical roots to use the ordinary case-insensitive namespace semantics represented by the current path model.

The Files UI does not call `File.Copy`, `File.Move`, route mutation through the Indexer, or reconstruct a new path-only operation from the row. It passes the same immutable plan to the reviewed executor that owns durable `BeginAsync`, per-entry mutation barriers, commit/recovery transitions and the identity-bound Windows mutation primitive.

The current executable slices remain deliberately narrow:

- regular-file Copy only; directory Copy is rejected;
- Move uses separate transactions: regular-file Move executes only as a same-volume local file rename, while a homogeneous directory-only Move can execute as a same-volume local directory rename; mixed file/directory batches and cross-volume Move mutation are rejected;
- a cross-volume Move is classified as requiring a separately reviewed Copy-plus-source-delete transaction and is left queued/non-executable because existing Copy authority is not source-delete authority;
- per-directory case-sensitive NTFS or unavailable namespace-capability evidence blocks Move before durable mutation history; exact-case mutation is not claimed;
- `Ask later` collisions remain non-executable until an explicit decision is supplied; a `Skip existing` plan can durably skip existing destinations and `Stop on collision` fails closed at validation;
- no overwrite/replacement path exists in the Copy or Move mutation boundary;
- terminal or recovery-sensitive execution makes that operation ID single-use; the UI removes an invoked plan from the runnable queue rather than offering replay;
- durable history is recovery evidence, not automatic restart-time mutation authority;
- destination panes refresh after Copy; both matching source and destination panes refresh after an invoked Move settles.

See `docs/file-copy-executor.md`, `docs/windows-file-copy-mutation.md`, `docs/file-move-execution-strategy.md`, `docs/directory-operations.md` and the `docs/file-operation-*.md` recovery/history series.

A preflight result is still not mutation authorization. It can become stale, and each executor's action-time canonical identity checks remain mandatory even when the visible preflight row says Ready.

### Copy / Move progress, cancellation and durable outcome

While Copy or Move is active, Files displays entry-level `completed / total` progress from the executor's `FileOperationExecutionSnapshot`. It does not invent byte-level progress or infer completion from directory state. Planning, queue mutation and the other filesystem executor remain disabled until the active execution settles.

**Cancel Copy** and **Cancel Move** call the active executor's `RequestCancellationAsync(operationId)`. Cancellation can stop validation and can settle running work at the reviewed boundary between entries. Once `MutationStarted` is durable for one entry, cancellation is intentionally not passed into that entry's identity-bound mutation / durable commit critical section. A cancellation request therefore means “stop at the next safe boundary”, not “tear down the current filesystem write immediately”.

After execution settles, Files re-reads the same persistent action-history operation before presenting the final status. The UI distinguishes:

- durable `Succeeded`;
- durable `Cancelled`, including already committed/skipped entries;
- durable non-recovery `Failed`;
- `RecoveryRequired` or any unresolved `MutationStarted` / recovery-required entry;
- non-terminal or unreadable durable history, which is treated conservatively rather than as success.

Recovery-sensitive or ambiguous history never becomes consent, overwrite/delete authority or automatic replay. The original operation ID remains single-use, and any later attempt begins from a fresh plan and fresh validation.

For Move specifically, a committed entry must preserve the validated source file identity at its destination and cannot reuse Copy-only destination-delete undo, content-fingerprint or hard-link evidence.

## Permanent file deletion

Permanent delete is intentionally **not** another Copy/Move queue kind. There is no generic `FileOperationKind.Delete` added to the planning queue.

A delete review starts from an exact selection in one ready Files pane and supports regular files only. Directories, simultaneous selection in the other pane, stale/missing tab state and rows that are no longer direct children of the captured directory fail before a usable delete plan is created.

### Destructive-session serialization

Before recovery inspection, the App acquires a per-user cross-process destructive-session lock under the FileOp LocalApplicationData control directory using exclusive file sharing. A second updated FileOp process cannot enter a concurrent authorization window while that lock is held.

The lock is application-control state only; it is never derived from a selected target path and grants no filesystem mutation capability itself.

### Reviewed session sequence

The user-facing flow is:

```text
per-user destructive-session lock
-> persistent delete recovery-history scan
-> read-only Windows delete preflight
-> canonical execution validation + protected-location policy
-> explicit permanent-delete confirmation for exact canonical paths
-> second persistent recovery-history scan
-> session-scoped FileDeleteOperationUserAuthorizationReceipt
-> durable SQLite BeginAsync
-> FileDeleteOperationOrchestrator
-> Windows stability/final-mutation capability providers
-> same-handle final mutation
```

Important ordering rules:

- no delete authorization exists before explicit confirmation;
- recovery-sensitive history is checked both before review and again after the confirmation dialog;
- durable history begins before orchestration can cross the mutation barrier;
- a recovery/history record is not reusable consent and never becomes automatic replay authority;
- the final mutation capability is tied to the reviewed canonical/identity boundary rather than a path-only `File.Delete` fallback.

### Recovery and cleanup ownership

Unresolved `MutationStarted` / `RecoveryRequired` history blocks a new session.

A terminal recovery-required orchestration result is surfaced explicitly and does not manufacture replacement authorization. FileOp does not auto-replay a delete after restart.

If final mutation-lease release fails, the release exception retains cleanup ownership. Files retries that **cleanup-only** release and, if it remains pending, disables new deletion review and exposes a retry action. Cleanup retry grants no new mutation authority.

Once durable history has begun, Files tabs displaying the affected directory are invalidated/refreshed because namespace state may have changed even if terminal completion becomes recovery-sensitive.

### Current delete scope

The user-facing delete slice is deliberately narrow:

- file-only;
- permanent delete;
- no directory deletion;
- no Recycle Bin integration;
- no undo/restore claim;
- no automatic recovery replay;
- no Indexer mutation API;
- no path-only delete fallback.

The detailed security boundary is split across `docs/file-delete-preflight.md`, `docs/file-delete-execution-validation.md`, `docs/file-delete-user-authorization.md`, `docs/file-delete-action-history.md`, `docs/file-delete-recovery-history-discovery.md`, `docs/file-delete-stability-lease.md`, `docs/file-delete-final-mutation-lease-contract.md`, `docs/windows-file-delete-final-mutation-lease-provider.md`, `docs/file-delete-mutation-barrier.md`, `docs/file-delete-same-handle-mutation.md` and `docs/file-delete-multi-entry-orchestration.md`.

## Storage review handoff

Known-location review can hand a current candidate into Files for inspection when that path is within the active Files indexed source. The handoff selects/opens indexed browse state only; it does not queue, authorize or execute deletion.

Cleanup-readiness evidence is similarly non-authorizing. If the user later chooses permanent deletion, the Files delete session starts fresh from current Files selection and reruns its recovery/preflight/canonical/confirmation boundaries.

## Native and fallback browsing

Native browsing opens SQLite read-only, takes the shared cross-process lease and requires a valid durable checkpoint. When the requested directory has stable provider identity, the native path can use parent-identity columns rather than a whole-index path scan.

Fallback browsing pages the already-completed in-memory user-profile snapshot. It does not rescan the filesystem for every page.

This equivalence is a **browse** statement only. The crawler snapshot does not preserve every NTFS identity/allocation property needed to make it automatically equivalent to native Optimize physical-reclaim evidence.

## Validation without hosted Actions

`tools/test-local.ps1` is the authoritative validation inventory. The Files boundary is covered by dedicated offline verifiers for browse/UI state, operation state/preflight/execution validation, Files Copy execution/cancellation/recovery, Move strategy/history/executor/UI, delete history/preparation/final capability/mutation/orchestration, recovery discovery and the user-facing Files delete session.

Run portable model/source validation through the aggregate gate:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-local.ps1 -OfflineOnly
```

The complete Windows gate adds Core/Windows/Indexer builds, the regression/integration test suite, WinUI x64 build, bundled-helper artifact checks and a real helper-process handshake. During the current roadmap batch that complete Windows gate is intentionally deferred until the related Files operation slices are ready to validate together; no source-only verifier is a substitute for it.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-local.ps1
```

The focused native handoff scenarios for the final consolidation stack are listed in `docs/windows-release-validation.md`.

See `docs/local-validation.md` for the validation workflow.

## Remaining queue boundary

Regular-file Copy plus same-volume local regular-file and homogeneous directory Move are now queue operations wired to reviewed production mutation executors, with progress, safe cancellation and recovery-sensitive terminal reporting. File Move and directory Move use separate durable schemas/executors. Explicit overwrite/replacement remains separate because the current mutation primitives do not authorize replacement.

Cross-volume Move still requires a separately reviewed composite Copy-plus-source-delete transaction with its own durable source-delete authorization/recovery boundary. Directory Copy remains disabled until recursive copy fidelity, mutation and recovery policies are implemented rather than inferred; the reviewed directory Move slice is intentionally limited to homogeneous same-volume local rename.

Permanent file deletion stays outside that generic queue unless a future design can preserve the stricter delete recovery/authorization semantics rather than weakening them into a generic operation kind.
