# Cross-volume regular-file Move

Cross-volume Move is a destructive composite transaction. It is **not** implemented as an unchecked `Copy` followed by a path-based delete.

This document describes the reviewed transaction machinery implemented by draft PR #185. **Production Files execution does not currently enter this transaction.** `WindowsMoveOperationExecutionValidator` blocks freshly validated different-volume Move before durable history, destination Copy or source-delete mutation while #187 (final proof-to-mutation stability) remains unresolved and the exact implementation has not yet passed its native Windows gate.

Issue #186 selected the ordinary-user security contract source-side: FileOp follows Windows cross-volume Move semantics in which the newly created destination receives destination-side default/inherited security rather than preserving the source file's security descriptor. Security-descriptor equality is therefore intentionally not a destructive-fidelity requirement.

## Scope

The dormant composite boundary applies only to regular-file Move entries whose freshly validated source and destination roots have different filesystem volume identities.

It does not authorize:

- production Files execution while the product-readiness block is active;
- directory Move;
- overwrite or replacement;
- path-only source deletion;
- replay of a prior operation ID;
- using Copy success as delete authority;
- automatic cleanup of a copied/source-retained result;
- automatic recovery mutation from durable evidence.

Same-volume regular-file Move continues to use the separate identity-preserving production rename executor.

## Production enablement boundary

Read-only preflight intentionally carries path state/reparse/error evidence, not stable root filesystem identities. It therefore does not infer volume relationship from drive-letter text.

Fresh Windows execution validation resolves stable canonical root identities. For a mutation-ready Move:

- missing root identity fails closed as `Blocked` rather than throwing or guessing;
- different root volume serials return `Blocked` **before namespace probing, durable history, destination Copy or source-delete capability acquisition**;
- same-volume roots continue through the existing namespace-capability checks and same-volume executor.

This preserves the composite implementation for deterministic/model testing without presenting it as a current user-reachable mutation route.

## Composite durable states

Cross-volume Move owns independent `file_cross_volume_move_*` SQLite history. It does not reinterpret the Copy/same-volume Move journal.

The important per-entry states are:

1. `Pending` — no mutation barrier crossed.
2. `CopyMutationStarted` — durable Copy mutation barrier crossed; Copy outcome must be settled or treated as recovery-sensitive.
3. `DestinationCommitted` — the exclusive-create destination is durably bound by filesystem identity and SHA-256 main-stream content evidence. The original source is still retained.
4. `SourceDeleteStarted` — a separate durable destructive barrier has been crossed while an exact Move-specific source-delete lease is live.
5. `Moved` — source delete-on-close completed and the durable journal committed the original source identity.
6. `Skipped` — explicit collision Skip; no copied-destination evidence is recorded.
7. `Failed` — safe terminal failure with no unresolved mutation barrier.
8. `RecoveryRequired` — durable evidence exists for an unresolved Copy or source-delete barrier. Evidence grants no replay, rollback or delete authority.

`DestinationCommitted` is intentionally a **safe duplicate state**, not an ambiguity. In direct composite-engine execution, cancellation or a pre-delete fidelity/capability refusal may stop there: the copied destination remains and the original source remains. The engine records that condition explicitly and does not clean it up automatically.

Persisted-history validation is stricter than the SQLite table shape. Entry source identity must remain on the durable source-root volume, destination evidence must remain on the durable destination-root volume, a safe `Failed` row cannot hide an unresolved Copy barrier, and source-delete recovery must retain the earlier Copy/destination chronology.

## Mutation ordering

For one ready entry passed directly into the dormant engine through a validator that authorizes the composite strategy, the reviewed order is:

1. fresh execution-grade validation;
2. durable `CopyMutationStarted` barrier;
3. exclusive-create Copy through the reviewed Copy mutation primitive;
4. durable destination commit with destination identity and SHA-256 content fingerprint;
5. cancellation-safe checkpoint;
6. acquire a Move-specific source-delete lease that identity-binds source root/file and destination root/file;
7. prove destructive fidelity while the inner identity/content lease is live;
8. optional cancellation-safe checkpoint;
9. durable `SourceDeleteStarted` barrier;
10. Core mints an exact one-shot authorization bound to that live evidence lease and durable history row;
11. re-prove destructive fidelity with cancellation disabled;
12. perform same-handle delete-on-close through the reviewed Windows primitive;
13. release the exact capability/evidence lease;
14. durably commit `Moved`.

No cancellation token is passed after `SourceDeleteStarted`. From that point the operation must either reach durable completion or become `RecoveryRequired`.

## Destructive fidelity proof

File identity alone is insufficient for source deletion. A source can keep the same file ID while its bytes or metadata change after Copy commit.

The current fail-closed classifier permits source deletion only when all of the following are proven for the current pinned source and committed destination:

- source main-stream SHA-256 equals the durable committed destination fingerprint;
- destination main-stream SHA-256 still equals that durable fingerprint;
- Copy-preserved stable basic metadata matches;
- neither file has attribute bits outside the current ordinary-file Copy subset;
- neither object has named data streams;
- both objects have exactly one hard link;
- both objects report zero extended-attribute bytes;
- neither object is a directory or reparse point;
- canonical path and filesystem identity remain exact.

Unsupported or incomplete evidence is a refusal, not an assumption.

### Security semantics

Security-descriptor equivalence is deliberately **not** part of this fidelity classifier. The selected product contract is `FileCrossVolumeMoveSecurityDisposition.DestinationDefaultInherited` and `FileCrossVolumeMoveSecurityPolicy.PreservesSourceSecurityDescriptor` is false.

The reviewed Windows Copy path creates the new destination with destination-context/default security rather than cloning the complete source security descriptor. This matches normal Windows cross-volume Move behavior and avoids requiring `ACCESS_SYSTEM_SECURITY`/SACL reads merely to decide whether an ordinary user's source can later be deleted.

This is a visible fidelity tradeoff, not an implementation accident: moving a file to another volume may change its ACL/security inheritance. FileOp must document that behavior wherever cross-volume Move is eventually exposed.

### Why named streams, EAs and hard links block

The current Copy primitive copies the unnamed/main data stream plus a narrow stable basic-metadata subset. It does not yet prove preservation of arbitrary named-stream contents, extended attributes or hard-link topology. Therefore destructive Move must retain the source when any of those semantics are present.

## Two fidelity checkpoints

The fidelity wrapper verifies once during source-delete lease acquisition, before `SourceDeleteStarted`.

If that first proof fails, a direct composite-engine execution can terminate safely with the committed destination retained and source retained. No source-delete barrier is crossed.

The wrapper verifies again after `SourceDeleteStarted` and before it delegates the exact authorization to the inner delete primitive.

If that second proof fails, **no inner delete mutation is called**, but the durable destructive barrier has already been crossed. The entry therefore becomes `RecoveryRequired` rather than pretending the failure is safely retryable.

## Handle and authority separation

The raw Windows source-delete primitive and the fidelity wrapper have different jobs:

- the raw primitive owns exact root/file handles, canonical/identity revalidation, protected-location/namespace checks and same-handle delete-on-close;
- the fidelity verifier gathers content/basic-metadata/stream/link/EA evidence;
- the Core classifier decides whether that evidence is within the supported destructive subset;
- the Core executor records the durable delete barrier;
- only Core can mint the post-barrier `FileCrossVolumeMoveSourceDeleteAuthorization`;
- the fidelity wrapper cannot synthesize or substitute delete authority.

The dormant Files composition code uses `WindowsFidelityVerifiedFileCrossVolumeMoveSourceDeletePrimitive`, not the raw destructive primitive directly. Production validation currently returns before the composite executor is entered.

### DELETE-share compatibility of evidence reopens

The raw source lease already owns `DELETE` access and intentionally uses `FileShare.Read`, preventing ordinary later write/delete opens. A second handle that FileOp itself opens to read fidelity evidence must therefore include `FileShare.Delete` or Windows rejects that reopen because it is incompatible with the already-live DELETE-capable handle.

The native verifier consequently uses `FileShare.Read | FileShare.Delete` for evidence reopens. That does **not** weaken the raw lease: the earlier raw handle still omitted write/delete sharing, so outside callers cannot use the verifier's compatibility flag to acquire a new ordinary write/delete capability while the lease remains live.

## Remaining concurrency/stability limitation (#187)

Share denial is not a complete topology freeze. In particular, Windows exposes `FileLinkInformation` as a set-information operation and documents that no specific access right is required to issue it on an already-open file handle. A compatible handle that existed before FileOp acquired its DELETE-capable lease can therefore remain relevant even when new write/delete opens are denied.

A Windows Read-Handle oplock is a promising kernel-backed boundary because `FileLinkInformation` from a different oplock key breaks Read-Handle oplocks and requires acknowledgement before the link operation continues. However, FileOp's current source lease is opened for synchronous I/O, while Windows oplocks require an asynchronous handle. The implementation therefore cannot simply request an oplock on the existing handle.

A reviewed #187 solution needs to redesign source capability acquisition so that:

- the exact identity-bound source handle used across final fidelity proof and delete is asynchronous and can hold the required oplock;
- the oplock is successfully granted before the final destructive proof is trusted;
- any break before delete authorization causes fail-closed refusal/recovery according to the durable barrier already crossed;
- the oplock request remains outstanding through the final proof and same-handle delete-on-close;
- tests demonstrate that a pre-existing compatible handle attempting hard-link creation cannot change topology between proof and delete;
- content, basic-metadata and EA stability are either covered by share/access semantics or by the selected oplock behavior, with no unsupported dimension assumed stable.

Until this is implemented and validated on Windows, a repeated best-effort proof is not enough. Production validation remains disabled for different-volume Move and #185 remains draft.

## Cancellation and observable engine outcomes

Before the Copy barrier, direct composite-engine cancellation can stop with no mutation.

After `DestinationCommitted` and before `SourceDeleteStarted`, cancellation is safe and leaves a known duplicate: destination Copy plus retained original source.

After `SourceDeleteStarted`, cancellation does not interrupt the destructive critical section. Any failure to prove or complete the source-delete phase becomes recovery-sensitive.

These are transaction-engine semantics, not current Files production outcomes. Current Files validation refuses different-volume Move before the first durable barrier or Copy.

The original operation ID is single-use once execution can reach durable history. FileOp does not automatically replay the Copy, delete a retained source, or infer cleanup authority from the journal.

## Validation without GitHub Actions

GitHub-hosted Actions are not part of this feature's merge gate.

Portable/model/source verification is wired into `tools/test-local.ps1 -OfflineOnly` through `tools/verify_file_cross_volume_move.py` and the Files Move source verifier. The model covers cancellation and both fidelity checkpoints, including:

- pre-barrier fidelity refusal => destination committed/source retained;
- post-barrier fidelity refusal => recovery required/no inner delete;
- successful deletion only after both fidelity proofs and the durable source-delete barrier;
- production different-volume validation => blocked before namespace probe, durable history or Copy;
- malformed mutation-ready root identity evidence => blocked rather than guessed or thrown;
- security fidelity => explicit destination-default/inherited policy with no privileged complete-security read dependency.

Deterministic managed tests cover the SQLite state machine, persisted-history invariants/corruption refusal, executor ordering, fidelity wrapper behavior with injected evidence results and product-readiness validation. The remaining #187 tests must be native Windows tests because oplock and hard-link race behavior is the point of the boundary.

The authoritative merge gate for executable changes remains the complete Windows invocation on the **exact final PR head**:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-local.ps1
```

Do not mark #185 ready or merge it based only on source review or randomized-model results. A green Windows gate also does not by itself resolve #187 unless the required topology-stability tests are part of that exact head.
