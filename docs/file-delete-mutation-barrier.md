# Durable file-delete mutation barrier under the final lease

This slice claims FileOp's first **live delete-mutation authority boundary** without adding a filesystem mutation primitive.

It consumes the exact final provider capability prepared by `FileDeleteOperationFinalMutationLeasePreparation`, transfers ownership away from the public pre-barrier scope, and durably advances the matching delete-history entry from `Pending` to `MutationStarted` while that same capability remains privately held.

It does not perform deletion.

## Authority composition

Durable history alone is never mutation authority. A live `FileDeleteOperationMutationBarrierScope` represents the conjunction of:

1. the exact session-only user authorization receipt;
2. the exact root/file identity and ordinal already bound by pre-mutation preparation;
3. the exact final provider-acquired delete-access capability still held in memory; and
4. a successfully validated durable `MutationStarted` transition for that same operation and ordinal.

The scope therefore reports `MutationBarrierSatisfied == true` but reports `DeleteMutationAuthorized == true` only while it still privately owns a live delete-access capability. Disposal removes live authority. The durable history remains recovery-sensitive evidence, not a reusable authorization token.

`DeleteMutationPerformed` remains false throughout this slice.

## Atomic ownership transfer

The #136 `FileDeleteOperationFinalMutationLeaseScope` and its new internal detach operation use the same semaphore as disposal.

That creates exactly two possible outcomes for a retained alias:

- alias disposal wins first, releases the capability, and later detach fails before the durable barrier; or
- detach wins first, clears the old scope's private lease slot and transfers the same lease to the barrier coordinator, after which every retained old-scope alias is inert.

There is no interval in which both the old scope and the barrier coordinator own the same final lease, and the underlying provider lease is never exposed publicly.

## Cancellation boundary

Cancellation is honored only before ownership transfer.

Once Core detaches the final capability, the short durability section intentionally uses `CancellationToken.None` for:

- `MarkMutationStartedAsync`;
- post-exception `GetAsync` outcome inspection; and
- `MarkMutationRecoveryRequiredAsync` when recovery marking is required.

This is not permission to ignore cancellation for filesystem mutation; no filesystem mutation exists here. It prevents a caller cancellation from interrupting the small ownership/durability settlement section and manufacturing an avoidable uncertain commit outcome after the old scope has already become inert.

A cancellation requested after detach therefore cannot cancel this barrier claim. A later destructive executor must define its own entry-safe cancellation semantics around the actual mutation.

## Returned-history validation

A successful `MarkMutationStartedAsync` result is not trusted merely because the store returned it.

Core requires operation-level provenance to remain unchanged:

- operation and authorization IDs;
- queued, validated, authorized, and started timestamps;
- source pane/tab;
- canonical source root and root identity;
- non-terminal operation state; and
- complete ordered entry count.

Every unselected entry must remain exactly equal to the pre-barrier snapshot. The selected entry must retain its exact ordinal, file entry, canonical path, and `FileIdentity`, while changing only into a valid `MutationStarted` shape with a mutation-start timestamp and no completion/failure data.

The selected path/identity must also match the final lease evidence. After the history transition, the coordinator rechecks that the transferred lease still reports its delete-access capability held, still remains non-authorizing on its provider contract, and still returns the exact final evidence object accepted before the barrier.

Only then is a `FileDeleteOperationMutationBarrierScope` returned.

## Barrier-write exceptions and durable outcome inspection

`SqliteFileDeleteOperationActionHistoryStore` performs its entry update and history materialization inside one transaction before calling `Commit`. A thrown barrier call therefore cannot always be interpreted by its caller as proof that no durable transition occurred.

When `MarkMutationStartedAsync` throws, Core keeps the final capability held and immediately inspects the current operation history using the non-cancellable critical section:

- if the exact prior `Pending` history is observed, no durable barrier is proven; the final lease is released and the original failure propagates;
- if the exact `MutationStarted` history is observed, the write outcome is recovery-sensitive; Core attempts to mark `RecoveryRequired` before releasing the capability;
- if history is missing, malformed, or cannot prove either exact state, the outcome remains explicitly ambiguous; Core releases the capability and reports failure without pretending the operation is safely pre-barrier.

No exception path attempts filesystem mutation.

## Post-barrier validation failure

If `MarkMutationStartedAsync` returns but the returned history or live capability fails validation, Core treats that as a post-barrier failure rather than rolling the logical state backwards.

It attempts `MarkMutationRecoveryRequiredAsync` while the final lease is still held, validates the returned recovery-sensitive entry when possible, and then releases the lease.

If recovery persistence itself fails, the earlier durable `MutationStarted` row remains the restart-time signal. The existing read-only recovery-history discovery can surface that state after restart. FileOp does not automatically retry or resume a delete from durable history.

## Barrier scope lifetime

The returned barrier scope privately owns the final lease and serializes disposal.

Ownership is cleared only after `DisposeAsync` succeeds. If provider disposal throws, `FinalLeaseHeld`, `DeleteAccessCapabilityHeld`, and therefore live `DeleteMutationAuthorized` remain observable so disposal can be retried. A successful later disposal clears live authority while `MutationBarrierSatisfied` remains a statement about the already-durable history transition.

## Still out of scope

This slice adds no:

- Windows final mutation-lease provider implementation;
- filesystem delete primitive;
- `SetFileInformationByHandle`, `NtSetInformationFile`, `DeleteFileW`, managed file/directory deletion, recycle-bin, move, or replacement operation;
- delete commit settlement after a filesystem mutation;
- automatic retry or recovery execution after restart;
- raw `SafeFileHandle`, underlying lease, or reusable provider-request exposure;
- generic `FileOperationKind.Delete` or `IFileOperationExecutor` integration;
- App, Windows, Indexer, Files, or Storage production consumer;
- protocol change; protocol remains v8.

The next separately reviewed native slice can implement a Windows final-lease provider and same-handle file-only delete primitive. That work must consume the live barrier scope/capability rather than reopen a pathname after validation, and it must be exercised by the full native Windows local gate before any cleanup UI is wired.

## Validation without GitHub Actions

`tools/verify_file_delete_mutation_barrier.py` independently models cancellation, ownership transfer, successful barrier authority, pre/post-persistence failures, recovery marking, ambiguous inspection, capability release, and disposal retry across randomized lifecycle states.

Its source guards require the shared detach/disposal gate, non-cancellable post-detach durability calls, exact history/capability validation, recovery-before-release behavior, unchanged protocol v8, absence of generic Delete, and absence of production consumers or mutation APIs.

Focused .NET tests cover successful ownership transfer, retained-alias disposal, cancellation on both sides of the transfer boundary, barrier exceptions before and after persistence, invalid returned history, recovery-persistence failure, and retryable barrier-scope disposal.
