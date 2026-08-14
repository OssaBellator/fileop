# File Move execution strategy

## Purpose

File Move must not be implemented as one generic path-only mutation. A same-volume Move and a cross-volume Move have materially different mutation and recovery semantics.

`FileMoveExecutionStrategyClassifier` is therefore a **classification boundary only**. The classification is not mutation authorization and does not invoke any filesystem mutation API.

## Input boundary

The classifier accepts only execution-grade `FileOperationExecutionValidationResult` evidence for an immutable `FileOperationKind.Move` plan.

Before naming a strategy it requires:

- validation status ready to begin mutation;
- a non-empty item list matching the immutable plan order;
- regular files only;
- stable identities for both canonical source and destination directories;
- only `Ready` or `Skip` item decisions;
- each source file identity to be on the canonical source directory volume;
- each existing destination identity, when present, to be on the canonical destination directory volume;
- every `Ready` destination leaf to still be missing.

Any mismatch returns `Blocked` and creates no mutation authority.

## Strategy outcomes

### SkipOnly

If every validated item is `Skip`, no filesystem mutation strategy is needed for the entries themselves.

This does not mean Move execution is already enabled. Durable history and UI semantics for a Move operation still need to be explicit before a skip-only Move can be surfaced as an executable user action.

### SameVolumeRenameRequired

If at least one entry is `Ready` and source/destination directory identities share the same volume serial number, the plan requires a separately reviewed **same-volume rename/move** primitive.

That future primitive must be identity-bound and must define durable Move history before crossing mutation. It must not fall back to path-only `File.Move`.

### CrossVolumeCopyDeleteRequired

If at least one entry is `Ready` and source/destination roots are on different volume serials, the plan requires a separately reviewed **cross-volume copy + source-delete transaction**.

Existing Copy support is not sufficient authorization for this. A cross-volume Move needs explicit sequencing and recovery semantics for at least:

1. destination Copy mutation and durable commit;
2. proof that the committed destination is the intended copied object;
3. source-delete authorization and identity binding;
4. durable transition from copied-but-source-still-present to source-deleted;
5. crash/restart recovery when failure occurs between those stages;
6. cancellation rules that never strand ambiguous replay authority.

## Current durable-history limitation

The current action-history schema explicitly supports mutation state only for Copy files. Non-Copy entries may currently be recorded only as skipped.

A real Move executor therefore must not ship by reusing Copy mutation records as if they meant Move. Durable history must be extended or a separately reviewed Move history model must be introduced first.

## Deliberate non-goals of this slice

This strategy slice does not:

- call `File.Move`, `MoveFileEx`, `SetFileInformationByHandle`, or `NtSetInformationFile`;
- call Copy or Delete mutation primitives;
- execute Move from Files;
- support directory Move;
- support overwrite/replacement;
- define cross-volume rollback;
- change the Indexer protocol.

## Validation

`tools/verify_file_move_strategy.py` exercises randomized classification properties and pins the source-level non-mutation boundary. It is wired into `tools/test-local.ps1 -OfflineOnly`.

The full Windows/.NET/WinUI/helper-process gate remains intentionally batched under the beta roadmap and is still required before any stacked source work is merged as release-ready.
