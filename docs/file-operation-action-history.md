# Durable file-operation action history

## Purpose

Canonical execution validation proves that a queued plan is safe enough to approach a mutation boundary, but it does not make a filesystem change recoverable. Before FileOp enables Copy, every attempted mutation needs durable per-entry history that survives process termination and can distinguish a completed effect from an uncertain one.

`IFileOperationActionHistoryStore` and `SqliteFileOperationActionHistoryStore` provide that persistence boundary. This slice writes only FileOp's own SQLite recovery metadata. It does **not** implement `IFileOperationExecutor`, Copy/Move/Delete, or an Undo command.

Action-history schema v1 is intentionally **file-mutation-only**. A directory may be recorded as `Skipped`, but a ready directory entry is rejected before the history transaction can commit. Recursive directory Copy needs child-level recovery/commit semantics and is not being inferred from one directory-root record.

## Commit barrier

A future file Copy executor must treat durable history as part of the entry commit protocol:

```text
canonical execution validation
        ↓
Begin action history
        ↓
Pending
        ↓ durable MarkMutationStarted
MutationStarted
        ↓ perform one file mutation
        ↓ capture resulting destination identity
        ↓ durable CommitCopy
Committed
        ↓ only now report entry progress/completion
```

The important barrier is after the filesystem mutation and before progress is reported. If `CommitCopyAsync` cannot durably record the resulting destination identity, the executor must not pretend the entry completed cleanly or continue as though recovery data existed.

A process crash while an entry remains `MutationStarted` is intentionally ambiguous. The record says mutation was allowed to begin but there is no durable proof of its final effect. On restart, `RequiresRecovery` is therefore true and automatic replay/undo must not guess what happened.

## Entry states

Action entries use these durable states:

- `Pending` — execution validation said a file destination was missing and mutation has not been declared started;
- `MutationStarted` — the durable pre-mutation barrier was crossed; a crash from here is recovery-sensitive;
- `Committed` — file Copy completed and the created destination's stable identity was durably captured;
- `Skipped` — execution validation selected non-destructive Skip; no mutation is expected;
- `Failed` — failure occurred before mutation began, so no recovery ambiguity is introduced;
- `RecoveryRequired` — mutation began but the effect cannot be represented as a clean committed Copy.

An operation can end `Succeeded` only when every entry is `Committed` or `Skipped`. If any entry is `MutationStarted` or `RecoveryRequired`, the operation may terminate only as `RecoveryRequired`.

## Copy undo candidates

This slice defines one recovery hint:

```text
DeleteCreatedDestination
```

It is attached only by `CommitCopyAsync`, and only after a file entry crosses `Pending → MutationStarted`. A `Pending` entry exists only when canonical execution validation saw the destination leaf as missing. Therefore the history record represents a destination FileOp intended to create rather than an existing object it replaced.

A committed file with the exact destination `FileIdentity` becomes an **undo candidate**, not deletion authorization. Identity matching is necessary to prove the path still names the same filesystem object, but it is **not sufficient** to prove the user or another program has not modified that object's contents or metadata since Copy. A future Undo implementation must add an explicit no-user-change guard—such as carefully selected post-copy metadata and/or content verification—before deleting anything. Path equality alone is also insufficient because a different object can later occupy the same path.

Directory entries are never undo candidates in schema v1. Existing destinations handled by `Skip` never receive undo metadata. Replace/overwrite remains unsupported. Move does not receive Copy-style undo candidates because Move has additional source-removal and cross-volume partial-failure semantics that are not designed yet.

## Persistence model

The SQLite store uses independently versioned additive tables:

```text
file_operation_action_schema_info
file_operation_actions
file_operation_action_entries
```

The operation row stores queued/validated/started/completed timestamps, operation/collision kind, captured roots, canonical roots and terminal state. Each entry stores its original source metadata, canonical source/destination paths, durable state timestamps, source/destination identities, undo kind and structured failure information.

The public `FileOperationActionHistory` aggregate defensively snapshots its entry sequence. Callers cannot retain a mutable list and rewrite the apparent durable history after construction.

Writes are serialized per store instance and state transitions use conditional SQL predicates against both the expected entry state and a non-terminal operation. This makes duplicate/out-of-order transitions fail rather than silently rewriting history.

SQLite uses WAL plus `synchronous = FULL` for this recovery log. The stronger synchronous setting is intentional: this metadata is not analytics cache; it is the evidence a future executor would rely on after a mutation.

Stable 64-bit identity values are stored by preserving their raw bit pattern through SQLite's signed 64-bit INTEGER representation (`unchecked` ulong/long conversion), so high-bit file references round-trip exactly.

## Failure semantics

`MarkEntryFailedBeforeMutationAsync` transitions `Pending → Failed` for errors that occur before the mutation boundary. Such an entry does not by itself require recovery.

After `MutationStarted`, a failure must use `MarkMutationRecoveryRequiredAsync`. It is intentionally not a normal Failed entry, because the filesystem may already have changed partially. The caller records the structured failure, and the operation must settle as `RecoveryRequired`.

If the process crashes before it can explicitly make that transition, the persisted `MutationStarted` state itself remains a recovery signal.

## Safety boundary

The new operation-history surfaces contain no `File.Copy`, `File.Move`, `File.Delete`, `Directory.Move`, `Directory.Delete`, or target-file write path. SQLite persistence necessarily writes FileOp's own action-history database; that application metadata write is separate from mutating the user's queued source/destination namespace.

There is still no concrete `IFileOperationExecutor` and no Run/Execute/Undo UI action.

## Validation without hosted Actions

Run the standard-library model directly:

```powershell
python tools/verify_file_operation_action_history.py --repo-root . --cases 20000
```

The verifier exercises successful file-Copy commit barriers, skipped file/directory entries, rejection of ready directory mutation, pre-mutation failures, crash-sensitive mutation starts, recovery-required settlement, undo candidate selection, terminal-state restrictions and schema/source guards.

The whole no-Actions gate remains:

```powershell
pwsh -File tools/test-local.ps1 -OfflineOnly
```

The normal Windows local gate additionally compiles Core/Windows, executes the SQLite/MSTest regressions, builds WinUI and verifies the bundled helper without spending hosted Actions usage.

## Next boundary

With canonical validation and durable recovery records defined, the next mutation slice can be a deliberately narrow **file Copy-only executor**. It should operate one file at a time, revalidate immediately at the mutation boundary, never overwrite, honor the durable history commit barrier, and stop on any recovery ambiguity. Directory Copy, Move and actual Undo execution should remain separate follow-up slices.
