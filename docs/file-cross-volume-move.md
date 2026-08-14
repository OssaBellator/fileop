# Cross-volume regular-file Move

Cross-volume Move is a destructive composite transaction. It is **not** implemented as an unchecked `Copy` followed by a path-based delete.

This document describes the reviewed transaction machinery implemented by draft PR #185. **Production Files execution does not currently enter this transaction.** `WindowsMoveOperationExecutionValidator` blocks freshly validated different-volume Move before durable history, destination Copy or source-delete mutation until the exact implementation passes the remaining native Windows stability tests.

Issue #186 selected the ordinary-user security contract source-side: FileOp follows Windows cross-volume Move semantics in which the newly created destination receives destination-side default/inherited security rather than preserving the source file's security descriptor. Security-descriptor equality is therefore intentionally not a destructive-fidelity requirement.

Issue #187 now has a deliberately scoped preservation contract rather than a claim that Windows can freeze every NTFS metadata dimension through a cross-volume copy/delete operation. The selected source directory entry and main/unnamed data stream are the destructive core; unsupported source ADS/EA state must be absent at proof checkpoints; hard-link topology and unrelated concurrent metadata mutations have explicit non-atomic semantics described below.

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

This preserves the composite implementation for deterministic/model/native testing without presenting it as a current user-reachable mutation route.

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
7. prove the current cross-volume preservation checkpoint while the inner identity/content lease is live;
8. optional cancellation-safe checkpoint;
9. durable `SourceDeleteStarted` barrier;
10. Core mints an exact one-shot authorization bound to that live evidence lease and durable history row;
11. re-check the preservation checkpoint with cancellation disabled;
12. perform same-handle delete-on-close through the reviewed Windows primitive;
13. release the exact capability/evidence lease;
14. durably commit `Moved`.

No cancellation token is passed after `SourceDeleteStarted`. From that point the operation must either reach durable completion or become `RecoveryRequired`.

## Preservation contract

`FileCrossVolumeMovePreservationPolicy` makes the product semantics explicit.

### Main-stream content is the destructive invariant

The source and destination main-stream SHA-256 fingerprints must still match the durable committed Copy fingerprint at both checkpoints. The raw source-delete capability keeps the exact source unnamed stream open with DELETE access and `FileShare.Read` only while the final proof and same-handle unlink occur.

That handle arrangement is intended to reject incompatible main-stream write opens and writable mappings. This is the remaining native boundary that #187 must demonstrate on the exact implementation before production enablement.

### Stable basic metadata is checkpoint evidence, not an atomicity claim

Copy-preserved stable basic metadata and supported file attributes are compared at each checkpoint. Observed drift blocks destructive completion.

However, FileOp does **not** claim that Windows share modes freeze `FILE_WRITE_ATTRIBUTES` or every metadata operation between the final observation and unlink. Cross-volume copy+delete is not presented as a global NTFS metadata transaction. `FileCrossVolumeMovePreservationPolicy.GuaranteesAtomicConcurrentMetadataMutationCapture` is false.

### Named streams and extended attributes

The current identity-bound Copy primitive does not preserve arbitrary source named data streams or EAs. Therefore a source with named streams or nonzero EA state at either destructive checkpoint is refused and retained.

Destination-only streams/EAs added by another actor do not prove loss of source semantics and are not destructive blockers. They also grant no delete authority; main-stream identity/content evidence and the separate durable source-delete authorization remain mandatory.

Windows sharing modes are stream-specific, and attribute/EA access is not governed by ordinary share flags. FileOp therefore does not falsely advertise post-checkpoint ADS/EA mutation as kernel-frozen.

### Security semantics

Security-descriptor equivalence is deliberately **not** part of the classifier. The selected product contract is `FileCrossVolumeMoveSecurityDisposition.DestinationDefaultInherited` and `FileCrossVolumeMoveSecurityPolicy.PreservesSourceSecurityDescriptor` is false.

The reviewed Windows Copy path creates the new destination with destination-context/default security rather than cloning the complete source security descriptor. This matches normal Windows cross-volume Move behavior and avoids requiring `ACCESS_SYSTEM_SECURITY`/SACL reads merely to decide whether an ordinary user's source can later be deleted.

This is a visible fidelity tradeoff, not an implementation accident: moving a file to another volume may change its ACL/security inheritance.

### Hard-link semantics

Hard links are directory entries to one underlying same-volume file. A new destination object on another volume cannot preserve that same hard-link topology.

FileOp therefore moves **the selected source directory entry**. Additional source hard links do not block the transaction: removing the selected source entry leaves other source-volume links to the original file valid. Additional destination links likewise do not mean the source Copy lost data. `FileCrossVolumeMovePreservationPolicy.PreservesHardLinkTopology` is false.

This also avoids a false #187 requirement. A source-stream oplock is not documented as a universal freeze on ordinary creation of an additional hard link; the relevant Windows oplock documentation qualifies the `FileLinkInformation` hard-link case around replacement/supersede behavior. FileOp does not claim otherwise.

## Two preservation checkpoints

The fidelity wrapper checks the current preservation contract once during source-delete lease acquisition, before `SourceDeleteStarted`.

If that first check fails, direct composite-engine execution can terminate safely with the committed destination retained and source retained. No source-delete barrier is crossed.

The wrapper checks again after `SourceDeleteStarted` and before it delegates the exact authorization to the inner delete primitive.

If that second check fails, **no inner delete mutation is called**, but the durable destructive barrier has already been crossed. The entry therefore becomes `RecoveryRequired` rather than pretending the failure is safely retryable.

The second check remains valuable because it catches observed drift. It is not described as an atomic freeze of metadata classes Windows does not freeze for this handle arrangement.

## Handle and authority separation

The raw Windows source-delete primitive and the fidelity wrapper have different jobs:

- the raw primitive owns exact root/file handles, canonical/identity revalidation, protected-location/namespace checks and same-handle delete-on-close;
- the fidelity verifier gathers current content/basic-metadata/stream/link/EA evidence;
- the Core classifier applies the explicitly scoped preservation policy;
- the Core executor records the durable delete barrier;
- only Core can mint the post-barrier `FileCrossVolumeMoveSourceDeleteAuthorization`;
- the fidelity wrapper cannot synthesize or substitute delete authority.

The dormant Files composition code uses `WindowsFidelityVerifiedFileCrossVolumeMoveSourceDeletePrimitive`, not the raw destructive primitive directly. Production validation currently returns before the composite executor is entered.

### DELETE-share compatibility of evidence reopens

The raw source lease already owns `DELETE` access and intentionally uses `FileShare.Read`. A second handle that FileOp itself opens to read fidelity evidence must include `FileShare.Delete` or Windows rejects that reopen because it is incompatible with the already-live DELETE-capable handle.

The native verifier consequently uses `FileShare.Read | FileShare.Delete` for evidence reopens. That compatibility flag does not turn recovery evidence or a path into mutation authority; the exact raw source handle and Core post-barrier authorization are still required.

## Remaining native stability validation (#187)

The source-side architecture is now deliberately narrow enough to test without inventing an unsupported all-metadata oplock guarantee.

Before production enablement, native Windows tests must prove the main-stream boundary on the exact source lease:

- a pre-existing writable unnamed-stream handle prevents acquisition of the DELETE-capable `FileShare.Read` source lease;
- a pre-existing writable unnamed-stream mapping prevents that lease;
- after the source lease is acquired, a new unnamed-stream write-capable open is rejected;
- both final SHA-256 observations happen while the same source capability remains live;
- same-handle source disposition uses that exact identity-bound capability after the durable barrier;
- failure/ambiguity before `SourceDeleteStarted` retains the source, while ambiguity after it remains `RecoveryRequired` without automatic retry/delete.

Native scope tests must additionally confirm that source ADS/EA state observed at either checkpoint blocks source deletion and that moving one hard link removes only the selected source directory entry while other links remain valid.

A generic oplock is **not** a prerequisite unless native evidence exposes a main-stream race the existing lease cannot close. Do not add an oplock merely to make the design sound stronger; Windows oplocks are stream-scoped and their set-information break behavior does not prove an all-metadata transaction.

Production validation remains disabled for different-volume Move until these tests pass on the exact final head.

## Cancellation and observable engine outcomes

Before the Copy barrier, direct composite-engine cancellation can stop with no mutation.

After `DestinationCommitted` and before `SourceDeleteStarted`, cancellation is safe and leaves a known duplicate: destination Copy plus retained original source.

After `SourceDeleteStarted`, cancellation does not interrupt the destructive critical section. Any failure to prove or complete the source-delete phase becomes recovery-sensitive.

These are transaction-engine semantics, not current Files production outcomes. Current Files validation refuses different-volume Move before the first durable barrier or Copy.

The original operation ID is single-use once execution can reach durable history. FileOp does not automatically replay the Copy, delete a retained source, or infer cleanup authority from the journal.

## Validation without GitHub Actions

GitHub-hosted Actions are not part of this feature's merge gate.

Portable/model/source verification is wired into `tools/test-local.ps1 -OfflineOnly` through `tools/verify_file_cross_volume_move.py` and the Files Move source verifier. The source contract pins:

- pre-barrier fidelity refusal => destination committed/source retained;
- post-barrier fidelity refusal => recovery required/no inner delete;
- successful deletion only after both checkpoints and the durable source-delete barrier;
- production different-volume validation => blocked before namespace probe, durable history or Copy;
- malformed mutation-ready root identity evidence => blocked rather than guessed or thrown;
- security => explicit destination-default/inherited policy with no privileged complete-security read dependency;
- hard links => selected-directory-entry semantics, not a one-link-only requirement;
- source ADS/EAs => unsupported and blocking when observed;
- concurrent metadata atomicity => explicitly not claimed.

Deterministic managed tests cover the SQLite state machine, persisted-history invariants/corruption refusal, executor ordering, fidelity wrapper behavior with injected evidence results, preservation/security policy and product-readiness validation.

The authoritative merge gate for executable changes remains the complete Windows invocation on the **exact final PR head**:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-local.ps1
```

Do not mark #185 ready or merge it based only on source review or randomized-model results. The remaining source-side contract is intentionally fail-closed at the production validator until the required native main-stream/metadata/link tests pass.
