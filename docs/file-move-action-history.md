# Same-volume file Move action history

## Scope

Same-volume regular-file Move uses the existing `file-operation-actions.sqlite` schema. No schema migration is required: schema v1 already persists operation kind, root identities, per-entry source/destination identities, generic mutation states, failure data and terminal operation state.

The change is semantic rather than structural. Copy and Move now have separate durable settlement methods so Copy-specific content fingerprints and delete-destination undo evidence cannot be reinterpreted as Move.

## Required sequence

A real same-volume Move executor must use this order:

```text
execution-grade validation
-> FileMoveExecutionStrategyClassifier == SameVolumeRenameRequired
-> BeginAsync
-> per-entry fresh validation
-> MarkMutationStartedAsync
-> identity-bound same-volume rename primitive
-> CommitSameVolumeMoveAsync
-> CompleteAsync
```

Once `MutationStarted` is durable, cancellation must not interrupt the rename/commit critical section. Failure after that barrier is recovery-sensitive.

## Commit invariant

A same-volume rename preserves the filesystem object identity. Therefore `CommitSameVolumeMoveAsync` accepts a destination identity only when it is exactly equal to the source identity recorded for that entry and both roots are identity-bound to the same volume.

Move commit records:

- `Committed` entry state;
- destination identity equal to source identity;
- no `DeleteCreatedDestination` undo kind;
- no Copy content fingerprint;
- no Copy destination hard-link evidence.

This distinguishes a namespace rename from a Copy that creates a new object.

## Recovery invariant

`MarkSameVolumeMoveRecoveryRequiredAsync` is valid only from `MutationStarted`. It may persist an observed destination identity when the destination can be verified, but that identity must equal the original source identity. A null observed destination identity means location remains ambiguous and requires inspection.

A terminal operation containing unresolved `MutationStarted` / `RecoveryRequired` state must settle as `RecoveryRequired`. Durable recovery history is evidence only and never grants automatic replay, rollback, overwrite or delete authority.

## Compatibility

Copy SQL remains explicitly gated to `FileOperationKind.Copy`. Move commit/recovery SQL is separately gated to `FileOperationKind.Move`.

`IFileMoveOperationActionHistoryStore` is an optional capability layered on top of `IFileOperationActionHistoryStore`, so unrelated history-store implementations are not forced to invent Move semantics.

Directory Move remains excluded from mutation-state history in this slice.

## Validation

`tools/verify_file_move_action_history.py` models same-volume identity preservation, Copy/Move separation and recovery-state shape. The complete .NET/SQLite tests and Windows gate remain part of the batched validation milestone tracked by the beta roadmap.
