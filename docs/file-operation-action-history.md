# Durable file-operation action history

## Purpose

Canonical execution validation proves that a queued plan is safe enough to approach a mutation boundary, but it does not make a filesystem change recoverable. FileOp therefore records durable per-entry action history before and after each supported mutation boundary so a process restart can distinguish a completed effect from an uncertain one.

`IFileOperationActionHistoryStore` defines that persistence boundary and `SqliteFileOperationActionHistoryStore` implements it with SQLite. `FileCopyOperationExecutor` consumes the store and delegates filesystem mutation through `IFileCopyMutationPrimitive`; `WindowsFileCopyMutationPrimitive` provides the production Windows handle-bound implementation behind that executor. The Files UI still does not expose Run/Execute/Undo.

Action-history schema v1 is intentionally **Copy-file-only for mutation state**. A directory or Move entry may be recorded as `Skipped`, but a ready directory or ready Move entry is rejected before the history transaction can commit. Recursive directory Copy needs child-level recovery/commit semantics, and Move needs source-removal/cross-volume partial-failure semantics; neither is inferred from the file-Copy protocol.

## Commit barrier

The Copy executor uses durable history as part of each file commit protocol:

```text
canonical execution validation
        ↓
Begin action history
        ↓
Pending
        ↓ durable MarkMutationStarted
MutationStarted
        ↓ identity-bound Copy mutation lease
        ↓ capture resulting destination identity
        ↓ durable CommitCopy
Committed
        ↓ report entry progress
```

The important barrier is after the filesystem mutation and before progress is reported. If `CommitCopyAsync` cannot durably record the resulting destination identity, the executor must not pretend the entry completed cleanly. After the mutation receipt has passed executor validation, that exact destination identity is retained as recovery evidence while the entry settles as `RecoveryRequired`.

A process crash while an entry remains `MutationStarted` is intentionally ambiguous. The record says mutation was allowed to begin but there is no durable proof of its final effect. On restart, `RequiresRecovery` is therefore true and automatic replay/undo must not guess what happened.

## Entry states

Action entries use these durable states:

- `Pending` — execution validation said a file destination was missing and mutation has not been declared started;
- `MutationStarted` — the durable pre-mutation barrier was crossed; a crash from here is recovery-sensitive;
- `Committed` — file Copy completed and the created destination's stable identity was durably captured;
- `Skipped` — execution validation selected non-destructive Skip; no mutation is expected;
- `Failed` — failure occurred before mutation began, so no recovery ambiguity is introduced;
- `RecoveryRequired` — mutation began but the effect cannot be represented as a clean committed Copy. It may carry a verified destination identity when the mutation receipt was valid and only the durable Copy commit failed.

An operation can end `Succeeded` only when every entry is `Committed` or `Skipped`. If any entry is `MutationStarted` or `RecoveryRequired`, the operation may terminate only as `RecoveryRequired`.

## Copy undo candidates

This schema defines one recovery hint:

```text
DeleteCreatedDestination
```

It is attached only by `CommitCopyAsync`, and only after a file entry crosses `Pending → MutationStarted`. A `Pending` entry exists only when canonical execution validation saw the destination leaf as missing. The history therefore records a destination FileOp intended to create rather than an existing object it replaced.

A committed file with the exact destination `FileIdentity` becomes an **undo candidate**, not deletion authorization. Identity equality is necessary to prove the path still names the same filesystem object, but it is **not sufficient** to prove the user or another program has not modified that object's contents or metadata since Copy. A future Undo implementation must add an explicit no-user-change guard before deleting anything.

A `RecoveryRequired` entry is different. Even when it carries a verified destination identity, its `UndoKind` remains `None`: the identity is recovery evidence only and does not authorize deletion or make the entry an undo candidate. Primitive failures, missing leases and invalid receipts do not have a verified destination identity and therefore continue to persist recovery without one.

Directory entries are never undo candidates in schema v1. Existing destinations handled by `Skip` never receive undo metadata. Replace/overwrite remains unsupported. Move does not receive Copy-style undo candidates because Move has additional source-removal and cross-volume partial-failure semantics that are not designed yet.

## Persistence model

The SQLite store uses independently versioned additive tables:

```text
file_operation_action_schema_info
file_operation_actions
file_operation_action_entries
```

The operation row stores queued/validated/started/completed timestamps, operation/collision kind, captured roots, canonical roots and terminal state. Each entry stores its original source metadata, canonical source/destination paths, durable state timestamps, source/destination identities, undo kind and structured failure information.

Recovery destination identity uses the existing nullable `destination_volume_serial` and `destination_file_reference` columns. This is an additive schema-v1 behavior change and requires no migration or schema-version increment.

The public `FileOperationActionHistory` aggregate defensively snapshots its entry sequence. Callers cannot retain a mutable list and rewrite the apparent durable history after construction. The aggregate also enforces the schema-v1 mutation boundary, so invalid ready-directory/non-Copy histories abort `BeginAsync` before its SQLite transaction can commit.

Writes are serialized per store instance and state transitions use conditional SQL predicates against both the expected entry state and a non-terminal operation. Duplicate or out-of-order transitions fail rather than silently rewriting history.

SQLite uses WAL plus `synchronous = FULL` for this recovery log. The stronger synchronous setting is intentional: this metadata is recovery evidence, not an analytics cache.

Stable 64-bit identity values preserve their raw bit pattern through SQLite's signed 64-bit INTEGER representation (`unchecked` ulong/long conversion), so high-bit file references round-trip exactly for both committed and recovery identities.

## Failure semantics

`MarkEntryFailedBeforeMutationAsync` transitions `Pending → Failed` for errors that occur before the mutation boundary. Such an entry does not by itself require recovery.

After `MutationStarted`, a failure must use `MarkMutationRecoveryRequiredAsync`. It is intentionally not a normal Failed entry because the filesystem may already have changed partially. The method accepts an optional verified destination identity. `FileCopyOperationExecutor` supplies that identity only when a validated mutation receipt exists and `CommitCopyAsync` fails; earlier native/lease/receipt failures pass no identity. If the process crashes before the explicit recovery transition can be persisted, the durable `MutationStarted` state itself remains the restart-time recovery signal.

## Safety boundary

The action-history contract and SQLite store contain no `File.Copy`, `File.Move`, `File.Delete`, `Directory.Move`, `Directory.Delete`, or target-file write path. SQLite persistence necessarily writes FileOp's own action-history database; that application metadata write is separate from mutating the user's queued source/destination namespace.

The production Windows mutation primitive is handle-bound and remains outside the history store. The Files UI does not expose Run/Execute/Undo, and recovery identity alone must never be interpreted as deletion authorization.

## Validation without hosted Actions

Run the standard-library action-history model and source guard directly:

```powershell
python tools/verify_file_operation_action_history.py --repo-root . --cases 20000
```

Run the complete offline gate without consuming GitHub Actions quota:

```powershell
pwsh -File tools/test-local.ps1 -OfflineOnly
```

For the Copy executor orchestration layer, run:

```powershell
pwsh -File tools/test-copy-executor-local.ps1
```

On Windows with .NET 10, `tools\test-windows-copy-local.cmd` remains the compiler/runtime source of truth for Core/Windows and the focused native Copy regressions.

## Next boundary

Recovery now retains the exact destination identity when a validated Copy mutation completed but durable commit persistence failed. A future recovery/Undo slice can use that evidence to identify the object conservatively, but it still needs an explicit no-user-change guard and user-facing authorization before deleting or modifying anything. Directory Copy, Move and actual Undo remain separate later slices.
