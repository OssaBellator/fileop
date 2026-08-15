# Cross-volume regular-file Move

Cross-volume Move is a destructive composite transaction. It is **not** implemented as an unchecked `Copy` followed by a path-based delete.

This document describes the reviewed transaction machinery implemented by draft PR #185. **Production Files execution does not currently enter this transaction.** `WindowsMoveOperationExecutionValidator` blocks freshly validated different-volume Move before durable history, destination Copy or source-delete mutation until the exact implementation passes the remaining native Windows gates.

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
- equal 32-bit volume serials are **not** assumed to prove one volume: production validation opens the canonical roots and compares handle-bound Windows volume-GUID names;
- equal serials with different volume GUIDs are treated as cross-volume and remain `Blocked`;
- equal serials whose stronger GUID relationship cannot be obtained fail closed before namespace probing/history/mutation;
- only equal serials with the same proven volume GUID continue through the existing namespace-capability checks and same-volume executor.

The stronger relationship check is intentionally asymmetric: different serials are sufficient evidence of different volumes, while equal serials need stronger proof before FileOp can allow identity-preserving same-volume rename.

The dormant composite journal is still schema-v1 and requires distinct source/destination volume serials. Therefore an equal-serial/different-GUID pair is correctly recognized as cross-volume but is **not** yet a supported dormant composite transaction. Issue #189 must either keep that rare collision case blocked during product enablement or explicitly upgrade durable volume identity/schema before routing it into the composite executor. A serial collision must never fall through to the same-volume rename path.

This preserves the composite implementation for deterministic/model/native testing without presenting it as a current user-reachable mutation route.

## Composite durable states

Cross-volume Move owns independent `file_cross_volume_move_*` SQLite history. It does not reinterpret the Copy/same-volume Move journal.

The important per-entry states are:

1. `Pending` — no mutation barrier crossed.
2. `CopyMutationStarted` — durable Copy mutation barrier crossed; Copy outcome must be settled or treated as recovery-sensitive.
3. `DestinationCommitted` — the exclusive-create destination is durably bound by filesystem identity and SHA-256 main-stream content evidence. The original source is still retained.
4. `SourceDeleteStarted` — a separate durable destructive barrier has been crossed while an exact Move-specific source-delete lease is live.
5. `Moved` — the exact source entry disposition completed, the source capability/evidence lease released, and the durable journal committed the original source identity.
6. `Skipped` — explicit collision Skip; no copied-destination evidence is recorded.
7. `Failed` — safe terminal failure with no unresolved mutation barrier.
8. `RecoveryRequired` — durable evidence exists for an unresolved Copy or source-delete barrier. Evidence grants no replay, rollback or delete authority.

`DestinationCommitted` is intentionally a **safe duplicate state**, not an ambiguity. In direct composite-engine execution, cancellation or a pre-delete fidelity/capability refusal may stop there: the copied destination remains and the original source remains. The engine records that condition explicitly and does not clean it up automatically.

Persisted-history validation is stricter than the SQLite table shape. Entry source identity must remain on the durable source-root volume, destination evidence must remain on the durable destination-root volume, a safe `Failed` row cannot hide an unresolved Copy barrier, and source-delete recovery must retain the earlier Copy/destination chronology.

Recovery observation is deliberately weaker than normal destination commit. A Copy-barrier recovery row may retain an observed destination identity and fingerprint for forensic inspection, but `HasDestinationRecoveryEvidence` does not imply `DestinationIsDurablyCommitted`. Only normal `DestinationCommitted` lineage—or later source-delete lineage that necessarily passed through it—proves the safe commit checkpoint. Recovery evidence never becomes cleanup, replay or source-delete authority.

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
12. perform `FileDispositionInformationEx` on the exact opened source link through the reviewed Windows primitive;
13. require the exact inner lease to report `SourceDeleteMutationPerformed`;
14. release the exact capability/evidence lease, closing the source handle before destination evidence handles;
15. durably commit `Moved`.

The current raw primitive sets `FILE_DISPOSITION_DELETE | FILE_DISPOSITION_POSIX_SEMANTICS | FILE_DISPOSITION_FORCE_IMAGE_SECTION_CHECK`. It deliberately does **not** set `FILE_DISPOSITION_ON_CLOSE`, and #185 must not describe that implementation as relying on the `ON_CLOSE` flag. Durable `Moved` is still recorded only after the exact source-delete lease has released successfully; release uncertainty after the destructive barrier is recovery-sensitive.

No cancellation token is passed after `SourceDeleteStarted`. From that point the operation must either reach durable completion or become `RecoveryRequired`.

## Preservation contract

`FileCrossVolumeMovePreservationPolicy` makes the product semantics explicit. In particular, `FileCrossVolumeMovePreservationPolicy.MovesSelectedSourceDirectoryEntryOnly` is true: cross-volume Move removes the selected source directory entry rather than claiming to transfer same-volume hard-link topology.

### Main-stream content is the destructive invariant

The source and destination main-stream SHA-256 fingerprints must still match the durable committed Copy fingerprint at both checkpoints. The raw source-delete capability keeps the exact source unnamed stream open with DELETE access and `FileShare.Read` only while the final proof and same-handle source disposition occur.

The native matrix now contains both lower-level and full-transaction tests for that boundary: pre-existing main-stream writers and writable mappings must prevent destructive-lease acquisition; a new writer attempted after `SourceDeleteStarted` must fail with `ERROR_SHARING_VIOLATION`; after that refusal the real second fidelity proof must still pass and the same transaction must complete. These tests are implemented but remain merge evidence only after they run successfully on the exact final Windows head.

### Stable basic metadata is checkpoint evidence, not an atomicity claim

Copy-preserved stable basic metadata and supported file attributes are compared at each checkpoint. Observed drift blocks destructive completion.

However, FileOp does **not** claim that Windows share modes freeze `FILE_WRITE_ATTRIBUTES` or every metadata operation between the final observation and unlink. Cross-volume copy+delete is not presented as a global NTFS metadata transaction. `FileCrossVolumeMovePreservationPolicy.GuaranteesAtomicConcurrentMetadataMutationCapture` is false.

### Named streams and extended attributes

The current identity-bound Copy primitive does not preserve arbitrary source named data streams or EAs. Therefore a source with named streams or nonzero EA state at either destructive checkpoint is refused and retained.

Destination-only streams/EAs added by another actor do not prove loss of source semantics and are not destructive blockers. They also grant no delete authority; main-stream identity/content evidence and the separate durable source-delete authorization remain mandatory.

Windows sharing modes are stream-specific, and attribute/EA access is not represented as share-frozen. The explicit two-volume matrix therefore contains real post-barrier races: one test creates a source ADS after `SourceDeleteStarted`, another writes a source EA with `NtSetEaFile`, and both delegate to the real second Windows fidelity proof. Each must refuse raw deletion and settle `RecoveryRequired` with both paths directly observable.

### Security semantics

Security-descriptor equivalence is deliberately **not** part of the classifier. The selected product contract is `FileCrossVolumeMoveSecurityDisposition.DestinationDefaultInherited` and `FileCrossVolumeMoveSecurityPolicy.PreservesSourceSecurityDescriptor` is false.

The reviewed Windows Copy path creates the new destination with destination-context/default security rather than cloning the complete source security descriptor. This matches normal Windows cross-volume Move behavior and avoids requiring `ACCESS_SYSTEM_SECURITY`/SACL reads merely to decide whether an ordinary user's source can later be deleted.

This is a visible fidelity tradeoff, not an implementation accident: moving a file to another volume may change its ACL/security inheritance. The dedicated ordinary-token security test gives the source a protected NULL DACL and requires the real Copy primitive to create a destination with ordinary destination-context/default security instead of cloning that NULL DACL.

### Hard-link semantics

Hard links are directory entries to one underlying same-volume file. A new destination object on another volume cannot preserve that same hard-link topology.

FileOp therefore moves **the selected source directory entry**. Additional source hard links do not block the transaction: removing the selected source entry leaves other source-volume links to the original file valid. Additional destination links likewise do not mean the source Copy lost data. `FileCrossVolumeMovePreservationPolicy.PreservesHardLinkTopology` is false.

This avoids a false requirement for a universal source-stream oplock. The native matrix instead pins the contract directly: the canonical resolver must preserve the specific opened hard-link name while both names report the same identity, and the real two-volume Move must remove only the selected entry while another source-volume link remains valid.

## Two preservation checkpoints

The fidelity wrapper checks the current preservation contract once during source-delete lease acquisition, before `SourceDeleteStarted`.

If that first check fails, direct composite-engine execution can terminate safely with the committed destination retained and source retained. No source-delete barrier is crossed.

The wrapper checks again after `SourceDeleteStarted` and before it delegates the exact authorization to the inner delete primitive.

If that second check fails, **no inner delete mutation is called**, but the durable destructive barrier has already been crossed. The entry therefore becomes `RecoveryRequired` rather than pretending the failure is safely retryable.

The second check catches observed drift; it is not described as an atomic freeze of metadata classes Windows does not freeze for this handle arrangement. The native matrix includes both a deterministic second-proof refusal with the real raw lease and real post-barrier ADS/EA changes detected by the real verifier.

## Handle and authority separation

The raw Windows source-delete primitive and the fidelity wrapper have different jobs:

- the raw primitive owns exact root/file handles, canonical/identity revalidation, protected-location/namespace checks and exact-handle source disposition;
- the fidelity verifier gathers current content/basic-metadata/stream/link/EA evidence;
- the Core classifier applies the explicitly scoped preservation policy;
- the Core executor records the durable delete barrier;
- only Core can mint the post-barrier `FileCrossVolumeMoveSourceDeleteAuthorization`;
- the fidelity wrapper cannot synthesize or substitute delete authority;
- after delegation, the wrapper refuses to report successful destructive completion unless the exact inner lease reports that source disposition was actually performed.

The dormant Files composition code uses `WindowsFidelityVerifiedFileCrossVolumeMoveSourceDeletePrimitive`, not the raw destructive primitive directly. Production validation currently returns before the composite executor is entered.

### DELETE-share compatibility of evidence reopens

The raw source lease already owns `DELETE` access and intentionally uses `FileShare.Read`. A second handle that FileOp itself opens to read fidelity evidence must include `FileShare.Delete` or Windows rejects that reopen because it is incompatible with the already-live DELETE-capable handle.

The native verifier consequently uses `FileShare.Read | FileShare.Delete` for evidence reopens. That compatibility flag does not turn recovery evidence or a path into mutation authority; the exact raw source handle and Core post-barrier authorization are still required.

## Native validation status (#186 / #187)

The source-side architecture is now deliberately narrow and the required native tests are implemented. #186 and #187 remain open because those tests have **not yet been recorded as passing on the exact final head**, not because the branch still needs another preservation architecture.

The exact native evidence matrix covers:

- pre-existing main-stream writer prevents source DELETE-lease acquisition;
- pre-existing writable main-stream mapping prevents that lease;
- live post-barrier lease rejects a new main-stream writer with `ERROR_SHARING_VIOLATION` and the Move then completes through the real second proof;
- source ADS and EA before the delete barrier cause safe source-retained refusal;
- real source ADS and EA created after `SourceDeleteStarted` are detected by the real second proof and cause `RecoveryRequired` without raw delete;
- selected-entry hard-link path binding and real two-volume hard-link behavior;
- ordinary-token destination-default security behavior;
- deterministic post-barrier refusal using the real raw four-handle lease;
- mutation-provider success without `SourceDeleteMutationPerformed` is rejected;
- recovery observation versus normal destination commit survives SQLite round-trip and malformed recovery rows are rejected during hydration;
- equal-serial volume relationships cannot silently bypass the cross-volume product block: equal serials require the handle-bound volume-GUID probe, while an unavailable stronger proof fails closed.

A generic oplock is **not** a prerequisite unless actual native evidence exposes a main-stream race the existing lease cannot close. Do not add an oplock merely to make the design sound stronger.

Production validation remains disabled for different-volume Move until all exact-head gates below pass.

## Cancellation and observable engine outcomes

Before the Copy barrier, direct composite-engine cancellation can stop with no mutation.

After `DestinationCommitted` and before `SourceDeleteStarted`, cancellation is safe and leaves a known duplicate: destination Copy plus retained original source.

After `SourceDeleteStarted`, cancellation does not interrupt the destructive critical section. Any failure to prove or complete the source-delete phase becomes recovery-sensitive.

These are transaction-engine semantics, not current Files production outcomes. Current Files validation refuses different-volume Move before the first durable barrier or Copy.

The original operation ID is single-use once execution can reach durable history. FileOp does not automatically replay the Copy, delete a retained source, or infer cleanup authority from the journal.

## Validation without GitHub Actions

GitHub-hosted Actions are not part of this feature's merge gate.

Portable/model/source verification is wired into `tools/test-local.ps1 -OfflineOnly` through `tools/verify_file_cross_volume_move.py` and the Files Move source verifier. The explicit two-volume runner additionally executes `tools/verify_cross_volume_move_native_inventory.py` and `tools/verify_move_volume_identity.py` before any opt-in native mutation test.

The exact final PR head must pass **all three** Windows commands before #185 can be considered merge-ready:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-local.ps1

powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-cross-volume-move-security.ps1

powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-cross-volume-move-native.ps1 `
  -SourceRoot 'C:\FileOpNativeTestRoot' `
  -DestinationRoot 'D:\FileOpNativeTestRoot'
```

The two dedicated scripts must run under an ordinary unelevated token. The explicit schema-v1 two-volume roots must resolve to different stable filesystem volume serials. Record exact SHA, Windows/.NET versions, roots, token context, complete outputs and any inconclusive tests from the ordinary suite.

Do not mark #185 ready or merge it based only on source review or randomized-model results. Production `WindowsMoveOperationExecutionValidator` must remain fail-closed for different-volume Move until that exact-head evidence is complete.
