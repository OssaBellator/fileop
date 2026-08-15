# Cross-volume Move native validation

This checklist is the native execution gate for the dormant regular-file cross-volume Move engine in draft PR #185.

It is deliberately separate from hosted GitHub Actions and from the ordinary `tools/test-local.ps1` run. The normal Windows test project discovers the opt-in two-volume tests, but when explicit roots are not supplied MSTest reports them as inconclusive/skipped. A normal full local gate therefore does **not** prove that two real filesystem volumes were exercised.

## Prerequisites

Use a normal unelevated FileOp development shell on Windows with:

- the exact PR head checked out and no uncommitted source changes;
- .NET 10 SDK matching the repository requirements;
- two existing writable ordinary directories on **different local filesystem volumes**;
- a source volume that supports NTFS hard links, named data streams and extended attributes for the current regression matrix;
- source/destination roots that are not reparse points and do not use per-directory case-sensitive namespace semantics;
- enough free space for the small temporary test payloads.

Do not point the test at production/user data. The two-volume tests create and recursively remove uniquely named `FileOp.CrossVolumeMoveNative*` subdirectories beneath both supplied roots. The security-policy test uses its own uniquely named temporary directory under the current user's temp path.

## Required commands

First run the ordinary authoritative local gate on the exact head:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-local.ps1
```

Then, from the same ordinary unelevated shell, run the explicit ordinary-token security-policy gate:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-cross-volume-move-security.ps1
```

That script refuses to run when the current token is elevated. It executes the `CrossVolumeMoveSecurityNative` category. The canonical regression gives the source file a protected NULL DACL while the destination parent retains an ordinary non-NULL DACL, then invokes the real `WindowsFileCopyMutationPrimitive`. The created destination must have an ordinary non-NULL default/destination-context DACL rather than cloning the source NULL DACL. The test queries/sets DACL information only; it does not request SACL, backup-security or `ACCESS_SYSTEM_SECURITY` evidence.

Finally run the explicit two-volume matrix:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-cross-volume-move-native.ps1 `
  -SourceRoot 'C:\FileOpNativeTestRoot' `
  -DestinationRoot 'D:\FileOpNativeTestRoot'
```

The dedicated runner refuses an elevated token, first executes `tools/verify_cross_volume_move_native_inventory.py`, then sets the two explicit roots and runs `TestCategory=CrossVolumeMoveNative`. The test code resolves the roots through the real Windows execution validator and fails if their stable filesystem volume serials are equal. Drive-letter text is not treated as proof of a cross-volume relationship.

## Matrix exercised by the dedicated two-volume gate

The `CrossVolumeMoveNative` category currently requires all of these cases:

1. **Successful composite Move** — the ordinary execution validator, real exclusive-create Copy primitive, composite SQLite journal, fidelity wrapper and identity-bound same-handle source-delete primitive complete one Move; the selected source entry disappears and destination bytes match.
2. **Source named stream refusal before the delete barrier** — a source ADS exists before execution. Copy may commit the main stream, but destructive completion must fail before `SourceDeleteStarted`; the original source including its ADS and the committed destination Copy both remain, with safe retained-duplicate history rather than recovery ambiguity.
3. **Source extended-attribute refusal before the delete barrier** — a real `FILE_FULL_EA_INFORMATION` entry is written to the source before execution. The committed destination main-stream Copy may remain, but source deletion must be refused before the destructive barrier and history must settle as a safe retained duplicate. The matrix fails if the supplied source filesystem cannot create/query EAs; unsupported EA coverage is not silently skipped.
4. **Cancellation after real Copy** — a wrapper around the real Copy primitive requests cancellation while the Copy lease is still live. The executor must finish the durable destination commit and settle at `DestinationCommitted`, retaining both files and creating no source-delete authority.
5. **Selected-entry hard-link semantics** — the selected source path has another hard link on the source volume. Cross-volume Move removes only the selected directory entry, creates the destination copy, and leaves the other source-volume hard link valid with the original content.
6. **Read-only safe refusal** — the real Copy phase preserves ReadOnly on the destination, but the first destructive checkpoint must reject both source and destination ReadOnly attributes before `SourceDeleteStarted`. Both paths remain, durable history is a safe retained duplicate, and no recovery-sensitive state is created. Issue #190 owns future exact-handle `FILE_DISPOSITION_IGNORE_READONLY_ATTRIBUTE` support; #185 does not claim it.
7. **Deterministic post-barrier fidelity refusal with the real Windows lease** — real Copy and the real raw four-handle source-delete lease are used, while an injected evidence-only verifier accepts the first proof and rejects the second after `SourceDeleteStarted`. The raw delete mutation must not run, both paths must still be directly observable, and durable history must settle as `RecoveryRequired`.
8. **Real post-barrier ADS race** — the first proof uses the real Windows fidelity verifier. After `SourceDeleteStarted`, the test creates a source ADS and then delegates the second proof to the real verifier. The second proof must report `SourceNamedDataStreams`; no raw delete runs and history becomes `RecoveryRequired`.
9. **Real post-barrier EA race** — the first proof uses the real Windows fidelity verifier. After `SourceDeleteStarted`, the test writes a source EA through `NtSetEaFile` and then delegates the second proof to the real verifier. The second proof must report `SourceExtendedAttributes`; no raw delete runs and history becomes `RecoveryRequired`.
10. **Real post-barrier main-stream writer exclusion** — after `SourceDeleteStarted`, while the exact raw source DELETE lease is live, the test attempts a new `FILE_WRITE_DATA` open against the selected source stream. It must fail with `ERROR_SHARING_VIOLATION`; the test then runs the real second fidelity proof and requires the Move to complete successfully. This proves the live destructive lease protects the selected main stream during the final proof/unlink boundary rather than merely observing content twice.

For all post-barrier refusal cases, generic recovery history remains conservative: it may retain destination recovery evidence and prove that the earlier normal destination commit occurred, but `HasRetainedSourceDuplicates` remains false because a generic recovery record must not promise current source presence merely from historical evidence. These deterministic tests separately assert that both paths are directly observable at the time of the refusal.

Separate always-on Windows tests pin lower-level assumptions before the explicit two-volume run:

- `FileCrossVolumeMoveFidelityShareCompatibilityTests` proves that pre-existing main-stream writers, writable mappings and DELETE-capable handles block destructive-lease acquisition; the live lease blocks new main-stream writers and new DELETE-capable handles; attribute/EA access is not misrepresented as share-frozen; deleting one hard link leaves another source-volume link valid; and fidelity reopens remain compatible with the live DELETE-capable handle.
- `FileCrossVolumeMoveHardLinkPathBindingTests.CanonicalResolverPreservesTheSpecificOpenedHardLinkName` proves that resolving two names for one hard-linked file preserves the specific selected directory entry while reporting the same filesystem identity. If the current Windows/filesystem combination cannot preserve that path binding, selected-entry multi-link Move is not considered validated.
- `FileCrossVolumeMoveMutationProofTests.FidelityWrapperRejectsInnerSuccessWithoutDispositionProof` proves the production fidelity wrapper cannot treat a provider return as destructive success unless the exact inner lease reports `SourceDeleteMutationPerformed`.
- `FileCrossVolumeMoveActionHistoryInvariantTests` plus `FileCrossVolumeMoveRecoveryEvidenceStoreTests` distinguish a safe durable destination commit from copy-barrier recovery observation and retain source-delete barrier uncertainty through recovery. Recovery-only destination identity/fingerprint evidence must not be upgraded into a known safe copied/source-retained state, including after SQLite reopen.
- `FileCrossVolumeMovePersistedRecoveryCorruptionTests` proves malformed source-delete recovery chronology and wrong-volume destination evidence are rejected during hydration rather than trusted merely because SQLite can structurally store them.

## Current disposition scope

The raw Windows source-delete primitive uses `FileDispositionInformationEx` on the exact opened source link with delete, POSIX-semantics and force-image-section-check flags. It intentionally does **not** use `FILE_DISPOSITION_ON_CLOSE`.

Draft #185 also intentionally does **not** use `FILE_DISPOSITION_IGNORE_READONLY_ATTRIBUTE`. ReadOnly is therefore excluded by the Core preservation classifier and tested as a safe pre-barrier refusal. #190 owns later support rather than letting a predictable read-only delete failure cross the durable source-delete barrier.

## Security-policy validation (#186)

The selected product contract is Windows-style destination-default/inherited security, not preservation of the source security descriptor. The cross-volume fidelity verifier must not require `ACCESS_SYSTEM_SECURITY`, SACL reads, or source/destination descriptor equality.

`WindowsFileCopyMutationSecurityPolicyTests.ReviewedCopyCreatesDestinationWithDefaultSecurityInsteadOfCloningSourceNullDacl` is the executable ordinary-token proof for that contract. Before #186 is closed, record a successful run through `tools/test-cross-volume-move-security.ps1` on the exact final head. Do not substitute an elevated run and do not interpret a failed privileged security read as evidence.

## Evidence to record

For PR #185 / issues #184, #186 and #187, record:

- exact git commit SHA;
- Windows version;
- .NET SDK version;
- confirmation that both dedicated scripts ran under an ordinary unelevated token;
- source and destination two-volume test roots;
- confirmation that the test validator observed different source/destination volume serials;
- confirmation that the always-on hard-link canonical-path binding regression passed rather than being inconclusive;
- confirmation that the read-only case retained both paths before `SourceDeleteStarted` and did not create recovery-sensitive history;
- confirmation that the post-barrier ADS and post-barrier EA tests both reached `SourceDeleteStarted`, refused the second real fidelity proof, retained both paths, and wrote `RecoveryRequired` history;
- confirmation that the post-barrier main-stream writer attempt was rejected with `ERROR_SHARING_VIOLATION` and the same transaction then completed through the real second proof and source unlink;
- complete `tools/test-local.ps1` result;
- complete `tools/test-cross-volume-move-security.ps1` result;
- complete `tools/test-cross-volume-move-native.ps1` result;
- any skipped/inconclusive tests from the ordinary suite.

Do not mark #185 ready merely because the source/model verifiers are green. Production `WindowsMoveOperationExecutionValidator` must remain fail-closed for different-volume Move until this exact-head native evidence is complete.
