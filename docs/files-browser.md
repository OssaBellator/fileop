# Indexed Files browser

## Purpose

The **Files** surface is an indexed dual-pane directory browser built on the same metadata source as Search and Storage. Browsing does not launch another recursive filesystem enumerator.

Native mode browses the primary whole-volume NTFS index. Fallback mode browses the completed user-profile crawler snapshot. Neither browse path launches `Directory.Enumerate*`, `GetFiles`, `GetDirectories`, `FileSystemWatcher` or another filesystem scan for each page.

Files now has two distinct kinds of operation UI that must not be conflated:

- a session-only **Copy/Move planning queue** whose reviewed regular-file Copy and same-volume regular-file Move plans can execute after successful read-only preflight and fresh execution validation. Different-volume Move is currently product-disabled by the Windows Move validator before durable history, destination Copy or source-delete mutation begins; the separate composite cross-volume engine remains draft infrastructure;
- a separately reviewed **file-only permanent-delete session**, which is a real destructive action with its own recovery, user authorization and mutation boundary.

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

Source transitions invalidate cached rows and selection conservatively so a selection cannot silently migrate into a different namespace/source context. A real backing-source identity transition also clears prepared and non-running queued operation plans. If a reviewed Copy or Move has already started, that active operation remains governed by its direct filesystem identity/history boundary and is not converted into source-token authority or automatic replay.

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

The repository contains reviewed production layers for file Copy and same-volume regular-file Move, plus a separate draft composite cross-volume Move transaction:

- read-only Windows preflight;
- canonical execution-grade validation contracts;
- durable Copy/same-volume Move action history plus a **separate composite cross-volume Move journal**;
- a production **file Copy** executor and identity-bound exclusive-create Windows copy mutation primitive;
- a production same-volume **file Move** executor and identity-preserving Windows handle-relative rename primitive;
- a draft cross-volume **file Move** executor that durably separates Copy commit from the later source-delete barrier;
- a Move-specific cross-volume source-delete provider that holds source root/file and committed destination root/file handles together and accepts only a Core-minted post-barrier authorization before same-handle delete-on-close;
- a fail-closed cross-volume preservation wrapper/classifier that checks the current supported source/destination checkpoint contract before the source-delete barrier and again immediately after that barrier before it delegates mutation authority;
- explicit cross-volume security/preservation policies: destination-default/inherited security, selected-source-entry hard-link semantics, stable main-stream content as the destructive invariant, and no claim that unrelated metadata writers are atomically frozen;
- a Windows Move validator that blocks different-volume production execution before durable history until the exact implementation passes its native main-stream stability and ordinary-token security-policy validation, and for enabled same-volume Move rejects case-sensitive or unavailable directory capability before durable mutation history.

A queued regular-file Copy or Move can be considered for execution only when its exact point-in-time preflight is `Ready` and the active source/destination pane, tab and directory context still matches the immutable plan. A fresh preflight already in flight also keeps both Run paths disabled even if an older Ready snapshot exists.

Preflight deliberately does not carry stable filesystem root identities, so it cannot safely determine same-volume versus cross-volume execution from drive-letter text. Fresh execution-grade validation resolves canonical root identities. A different-volume result is then product-blocked before any executor or durable history begins; same-volume Move continues through the namespace-capability checks.

Each enabled executor performs its own fresh canonical/identity validation before durable history begins and revalidates each mutation entry again at the relevant mutation boundary. Same-volume Move additionally requires both canonical roots to use the ordinary case-insensitive namespace semantics represented by the current path model.

The Files UI does not call `File.Copy`, `File.Move`, `File.Delete`, route mutation through the Indexer, or reconstruct a new path-only operation from the row. It passes the same immutable plan through the reviewed execution-grade validator. Only an enabled strategy reaches its reviewed executor.

The current **user-reachable executable slices** remain deliberately narrow:

- regular-file Copy only; directory Copy is rejected;
- regular-file same-volume Move uses an identity-preserving no-replace rename;
- different-volume Move is refused before durable history, destination Copy or source-delete mutation. The queued plan remains non-mutated rather than being partially executed;
- directory Move remains rejected;
- per-directory case-sensitive NTFS or unavailable namespace-capability evidence blocks enabled Move before durable mutation history; exact-case mutation is not claimed;
- `Ask later` collisions remain non-executable until an explicit decision is supplied; a `Skip existing` plan can durably skip existing destinations and `Stop on collision` fails closed at validation;
- no overwrite/replacement path exists in the Copy or Move mutation boundary;
- terminal or recovery-sensitive execution makes that operation ID single-use; the UI removes an invoked plan from the runnable queue rather than offering replay;
- durable history is recovery evidence, not automatic restart-time mutation authority;
- destination panes refresh after Copy; both matching source and destination panes refresh after an invoked same-volume Move settles.

The **dormant cross-volume transaction contract** is intentionally stricter than Copy and remains directly testable even though production validation currently blocks entry into it:

- the engine uses exclusive-create Copy first, then a separately durable and separately authorized deletion of the exact original source object; Copy success never means source-delete authority;
- `DestinationCommitted` is a safe engine checkpoint: the destination identity/content is durable and the original source still exists. Cancellation or a safe preparation/preservation refusal can settle there as an explicit duplicate state;
- before destructive completion, current source and destination main-stream SHA-256 must still match the durable Copy fingerprint and stable Copy-preserved basic metadata/attributes are compared at both checkpoints. Observed drift refuses deletion;
- source named data streams and EAs are unsupported by the current Copy primitive and retain the source whenever they are observed at a destructive checkpoint;
- cross-volume security intentionally follows Windows destination-default/inherited semantics rather than preserving the source security descriptor. The route does not require privileged SACL/`ACCESS_SYSTEM_SECURITY` evidence;
- hard-link topology is not recreated across volumes. FileOp moves the selected source directory entry; additional source-volume links remain valid and stable multi-link sources are not rejected merely for being linked elsewhere;
- destination-only extra streams/EAs or hard links do not prove loss of source semantics and do not create delete authority;
- the repeated pre/post-barrier preservation checks catch observed drift but do not claim a kernel-backed freeze of `FILE_WRITE_ATTRIBUTES`, EA or independent-stream metadata. Main/unnamed-stream stability through unlink is the remaining destructive invariant that must be demonstrated by native tests;
- once `SourceDeleteStarted` is durable, cancellation is not passed into final preservation revalidation, source disposition, lease release or durable source-delete commit. Any ambiguity or post-barrier refusal is recovery-sensitive;
- the raw source-delete lease keeps the committed destination open without ordinary write/delete sharing while it holds the exact source DELETE-capable handle. Fidelity evidence reopens share DELETE only because the source DELETE handle is already live; they do not turn evidence or a path into mutation authority.

See `docs/file-copy-executor.md`, `docs/windows-file-copy-mutation.md`, `docs/file-move-execution-strategy.md`, `docs/file-cross-volume-move.md` and the `docs/file-operation-*.md` recovery/history series.

A preflight result is still not mutation authorization. It can become stale, and action-time canonical identity checks remain mandatory even when the visible preflight row says Ready.

### Copy / Move progress, cancellation and durable outcome

While an enabled Copy or same-volume Move is active, Files displays entry-level `completed / total` progress from the executor's `FileOperationExecutionSnapshot`. It does not invent byte-level progress or infer completion from directory state. Planning, queue mutation and the other filesystem executor remain disabled until the active execution settles.

**Cancel Copy** and **Cancel Move** call the active enabled executor's `RequestCancellationAsync(operationId)`.

For Copy and same-volume Move, cancellation can stop validation and settles running work only at the reviewed boundary between entries. Once that entry's durable mutation barrier has been crossed, cancellation is not passed into Copy/commit or rename/commit.

The dormant cross-volume engine has two mutation barriers and one additional safe checkpoint:

1. `CopyMutationStarted` becomes durable before the exclusive-create Copy; cancellation is not passed through Copy plus durable destination commit.
2. `DestinationCommitted` proves the copied destination identity/content while the source is still retained. Cancellation may settle here safely as a known duplicate.
3. the exact source-delete capability is acquired while the committed destination is held stable, and the first preservation checkpoint must succeed. Unsupported or mismatched source semantics retain the source without crossing the delete barrier;
4. `SourceDeleteStarted` becomes durable immediately before Core mints the exact source-delete authorization;
5. the preservation checkpoint is repeated with cancellation disabled before the wrapper delegates that exact authorization to same-handle source deletion. A refusal here performs no inner delete mutation but becomes recovery-sensitive because the durable destructive barrier already exists;
6. cancellation is not passed beyond this point through source disposition, lease release or durable `Moved` commit.

Those cross-volume states describe the draft engine and its tests; current Files production validation does not enter them.

A cancellation request for an enabled executor means “stop at the next reviewed safe boundary”, not “tear down the current filesystem mutation immediately”.

After enabled execution settles, Files re-reads the appropriate persistent journal before presenting final status. Copy/same-volume Move use the shared action-history journal. Cross-volume formatter/journal plumbing remains present with the dormant engine but is not reached by the current product validator. Recovery-sensitive or unreadable/non-terminal history is handled conservatively.

For the dormant cross-volume engine, `Cancelled` or safe `Failed` may contain one or more `DestinationCommitted` entries. Those entries mean a destination copy is durable **and the original source was retained**. They are not recovery-required, but the engine does not automatically delete either side or replay the operation.

Recovery-sensitive or ambiguous history never becomes consent, overwrite/delete authority or automatic replay. The original operation ID remains single-use, and any later attempt begins from a fresh plan and fresh validation.

For same-volume Move specifically, a committed entry preserves the validated source file identity at its destination and uses no Copy-only destination-delete undo/fingerprint semantics. The dormant cross-volume transaction necessarily creates a new destination filesystem object, so its journal instead records the committed destination identity plus SHA-256 content fingerprint before any later source-delete authority can exist.

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

The dormant cross-volume Move design does **not** reuse this permanent-delete user authorization receipt. If that route is eventually enabled, it retains its own operation-scoped source-delete capability and durable barrier because “complete this Move” is a different user intent from “permanently delete these selected files”.

### Recovery and cleanup ownership

Unresolved `MutationStarted` / `RecoveryRequired` history blocks a new permanent-delete session.

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

`tools/test-local.ps1` is the authoritative validation inventory. Hosted GitHub Actions are optional duplicate evidence, not a merge prerequisite.

The Files boundary is covered by dedicated offline verifiers for browse/UI state, operation state/preflight/execution validation, Files Copy execution/cancellation/recovery, same-volume Move production wiring, dormant cross-volume Move strategy/history/executor/fidelity wiring, delete history/preparation/final capability/mutation/orchestration, recovery discovery and the user-facing Files delete session.

Run portable model/source validation through the aggregate gate:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-local.ps1 -OfflineOnly
```

Before merging a mutation branch, run the complete Windows local gate on the exact proposed head:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-local.ps1
```

The complete gate adds Core/Windows/Indexer Release builds, the Windows regression/integration test suite, WinUI x64 Release build, bundled-helper artifact checks and a real helper-process handshake. Passing source-only verifiers is not a substitute for this Windows gate.

See `docs/local-validation.md` for the validation workflow and `docs/windows-release-validation.md` for additional release qualification scenarios.

## Remaining queue boundary

Regular-file Copy and same-volume Move are wired to reviewed production mutation executors with progress, safe cancellation and recovery-sensitive terminal reporting. Different-volume Move is currently refused by production execution validation before durable history or destination Copy begins. The composite cross-volume journal/executor/preservation machinery remains implemented and directly testable, but it is dormant until the exact final head passes the #186 ordinary-token destination-security confirmation and #187 main-stream/share-mapping plus scoped metadata/link native tests.

Explicit overwrite/replacement remains separate because the current mutation primitives do not authorize replacement.

Directory Copy and directory Move remain disabled until recursive fidelity, mutation and recovery policies are implemented rather than inferred.

Permanent file deletion stays outside that generic queue unless a future design can preserve the stricter delete recovery/authorization semantics rather than weakening them into a generic operation kind.
