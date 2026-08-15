# Cross-volume Move native validation

This checklist is the native execution gate for the dormant regular-file cross-volume Move engine in draft PR #185.

It is deliberately separate from hosted GitHub Actions and from the ordinary `tools/test-local.ps1` run. The normal Windows test project discovers the opt-in two-volume tests, but when explicit roots are not supplied MSTest reports them as inconclusive/skipped. A normal full local gate therefore does **not** prove that two real filesystem volumes were exercised.

## Prerequisites

Use a normal unelevated FileOp development shell on Windows with:

- the exact PR head checked out and no uncommitted source changes;
- .NET 10 SDK matching the repository requirements;
- two existing writable ordinary directories on **different local filesystem volumes**;
- for the schema-v1 #185 matrix, roots whose observed stable filesystem volume serials are different;
- a source volume that supports NTFS hard links, named data streams and extended attributes for the current regression matrix;
- source/destination roots that are not reparse points and do not use per-directory case-sensitive namespace semantics;
- enough free space for the small temporary test payloads.

Do not point the test at production/user data. The two-volume tests create and recursively remove uniquely named `FileOp.CrossVolumeMoveNative*` / `FileOp.CrossVolumeMovePreflightNative*` subdirectories beneath both supplied roots. The security-policy test uses its own uniquely named temporary directory under the current user's temp path.

A 32-bit volume serial is not considered collision-free for production Move classification. Equal serials require a stronger handle-bound Windows volume-GUID relationship before same-volume rename can remain mutation-ready. However, the dormant composite journal in #185 still requires distinct serials, so an equal-serial/different-GUID pair is **not** an acceptable pair for this native matrix. Issue #189 owns the later enablement choice to keep such pairs unsupported or upgrade durable volume identity/schema.

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

The dedicated runner refuses an elevated token, first executes `tools/verify_cross_volume_move_native_inventory.py`, `tools/verify_cross_volume_move_source_preflight_native_inventory.py` and `tools/verify_move_volume_identity.py`, then sets the two explicit roots and runs `TestCategory=CrossVolumeMoveNative`. The test code resolves the roots through the real Windows execution validator and fails if their stable filesystem volume serials are equal. Drive-letter text is not treated as proof of a cross-volume relationship.

`tools/verify_move_volume_identity.py` separately pins the production serial-collision boundary before mutation tests: different serials short-circuit as cross-volume; equal serials require the stronger Windows volume-GUID probe; that probe must be bound to the exact root `FileIdentity` values returned by fresh execution validation; and the schema-v1 composite store must continue to reject equal-serial root pairs.

### One-command exact-head batch

When the two Windows roots are ready, the same required gate can be executed and recorded as one batch:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-cross-volume-move-merge-gate.ps1 `
  -SourceRoot 'C:\FileOpNativeTestRoot' `
  -DestinationRoot 'D:\FileOpNativeTestRoot'
```

The batch wrapper does **not** weaken or replace the three required commands above. It executes them in that order and fails on the first non-zero result. Before starting, it refuses non-Windows hosts, elevated tokens, dirty working trees and equal source/destination stable volume serials. It records the exact git HEAD, Windows version, .NET SDK version, resolved roots, filesystem names and volume serials, then writes complete output for the ordinary gate, security gate and native gate into one evidence directory. By default that directory is created under the current user's temporary directory; `-EvidenceDirectory` can select another path outside the repository.

`tools/verify_cross_volume_move_merge_gate.py` is the platform-independent source contract for this wrapper. It pins exact-head/clean-tree evidence, ordinary-token enforcement, stable volume-serial recording, command ordering, complete log capture and the absence of elevation or GitHub Actions shortcuts.

## Source preflight boundary (#191)

The cross-volume source preflight is an early UX/cost refusal only. It is identity-bound and non-authorizing, deliberately opens source evidence with ordinary read/write/delete sharing, and cannot mint `FileCrossVolumeMoveSourceDeleteAuthorization`. It prevents a predictable destination duplicate for source state that the current contract already knows cannot complete destructively.

For schema-v1 #185 it is scoped only to roots whose validated stable volume serials differ. Same-volume Move must bypass this cross-volume-only policy so identity-preserving rename is not accidentally restricted by cross-volume ADS/EA/ReadOnly rules.

The preflight currently checks the source for unsupported attributes (including ReadOnly while #190 remains open), named streams and EAs. It also applies the same `IFileDeleteProtectedLocationPolicy` eligibility used by the final source-delete provider. That protected-location result is still only an early refusal: the real source-delete provider independently re-evaluates both source root and source file when the destructive lease is acquired. No protected system location is used as a native mutation test target.

The later post-Copy source/destination fidelity proof remains mandatory. Early evidence is not a stability lease; state can change after the probe. The older direct-engine native refusals remain in the matrix specifically to prove that bypassing the early UX gate does not weaken the later destructive boundary.

## Matrix exercised by the dedicated two-volume gate

The `CrossVolumeMoveNative` category currently requires all of these boundaries:

1. **Successful composite Move** — the ordinary execution validator, real exclusive-create Copy primitive, composite SQLite journal, fidelity wrapper and identity-bound same-handle source-delete primitive complete one Move; the selected source entry disappears and destination bytes match.
2. **Pre-Copy source named-stream refusal** — the #191 preflight observes a source ADS before mutation. Execution must fail with `CrossVolumeMoveValidationBlocked`, the original source/ADS must remain, no destination path may appear and no composite history row may exist.
3. **Pre-Copy source extended-attribute refusal** — a real `FILE_FULL_EA_INFORMATION` entry is written to the source before execution. The #191 preflight must refuse before destination Copy/history. The matrix fails if the supplied source filesystem cannot create/query EAs; unsupported EA coverage is not silently skipped.
4. **Pre-Copy ReadOnly refusal** — while #190 remains open, a ReadOnly source must be rejected before destination Copy/history. The test restores the source attribute during cleanup rather than relying on a recursive delete to override ReadOnly.
5. **Post-Copy ADS fallback refusal** — a direct composite executor without the early preflight is still exercised. Copy may commit the main stream, but the first destructive fidelity/capability checkpoint must refuse the source ADS before `SourceDeleteStarted`; the source/ADS and committed destination Copy remain as an explicit safe duplicate.
6. **Post-Copy EA fallback refusal** — the same direct-engine defense-in-depth case for a source EA. The destination Copy may remain, but source deletion must be refused before the destructive barrier and history must settle as a safe retained duplicate.
7. **Post-Copy ReadOnly fallback refusal** — the direct engine still must reject ReadOnly at its destructive fidelity checkpoint before `SourceDeleteStarted`, retaining both paths without recovery ambiguity. This proves #191 did not become the sole safety boundary.
8. **Cancellation after real Copy** — a wrapper around the real Copy primitive requests cancellation while the Copy lease is still live. The executor must finish the durable destination commit and settle at `DestinationCommitted`, retaining both files and creating no source-delete authority.
9. **Selected-entry hard-link semantics** — the selected source path has another hard link on the source volume. Cross-volume Move removes only the selected directory entry, creates the destination copy, and leaves the other source-volume hard link valid with the original content.
10. **Post-barrier fidelity refusal with the real Windows lease** — real Copy and the real raw four-handle source-delete lease are used, while an injected evidence-only verifier accepts the first proof and rejects the second after `SourceDeleteStarted`. The raw delete mutation must not run, both paths must still be directly observable, and durable history must settle as `RecoveryRequired`.
11. **Real post-barrier ADS race** — the first proof uses the real Windows fidelity verifier. After `SourceDeleteStarted`, the test creates a source ADS and then delegates the second proof to the real verifier. The second proof must report `SourceNamedDataStreams`; no raw delete runs and history becomes `RecoveryRequired`.
12. **Real post-barrier EA race** — the first proof uses the real Windows fidelity verifier. After `SourceDeleteStarted`, the test writes a source EA through `NtSetEaFile` and then delegates the second proof to the real verifier. The second proof must report `SourceExtendedAttributes`; no raw delete runs and history becomes `RecoveryRequired`.
13. **Real post-barrier main-stream writer exclusion** — after `SourceDeleteStarted`, while the exact raw source DELETE lease is live, the test attempts a new `FILE_WRITE_DATA` open against the selected source stream. It must fail with `ERROR_SHARING_VIOLATION`; the test then runs the real second fidelity proof and requires the Move to complete successfully. This proves the live destructive lease protects the selected main stream during the final proof/source-disposition boundary rather than merely observing content twice.

For all post-barrier refusal cases, generic recovery history remains conservative: it may retain destination recovery evidence and prove that the earlier normal destination commit occurred, but `HasRetainedSourceDuplicates` remains false because a generic recovery record must not promise current source presence merely from historical evidence. These deterministic tests separately assert that both paths are directly observable at the time of the refusal.

Separate always-on Windows tests pin lower-level assumptions before the explicit two-volume run:

- `FileCrossVolumeMoveSourcePreflightPolicyTests` proves same-volume Move bypasses the cross-volume preflight, protected source eligibility blocks before the inner evidence probe, allowed protected-location policy delegates to the evidence probe, and the early policy result remains non-authorizing.
- `FileCrossVolumeMoveFidelityShareCompatibilityTests` proves that pre-existing main-stream writers, writable mappings and DELETE-capable handles block destructive-lease acquisition; the live lease blocks new main-stream writers and new DELETE-capable handles; attribute/EA access is not misrepresented as share-frozen; deleting one hard link leaves another source-volume link valid; and fidelity reopens remain compatible with the live DELETE-capable handle.
- `FileCrossVolumeMoveHardLinkPathBindingTests.CanonicalResolverPreservesTheSpecificOpenedHardLinkName` proves that resolving two names for one hard-linked file preserves the specific selected directory entry while reporting the same filesystem identity. If the current Windows/filesystem combination cannot preserve that path binding, selected-entry multi-link Move is not considered validated.
- `WindowsFileOperationVolumeRelationshipTests.TwoDirectoriesOnSameTempVolumeResolveToSameHandleBoundGuid` exercises the real handle-bound volume-GUID probe against exact identities returned by `WindowsFileOperationCanonicalPathResolver`; the probe must report `SameVolume` and the same volume-GUID name for both directories.
- `WindowsMoveOperationExecutionValidatorTests` pins the synthetic serial-collision cases: different serials never need the stronger probe, equal serial + different GUID is cross-volume/product-blocked, equal serial + unavailable stronger proof fails closed before namespace probing, and equal serial + same GUID may continue to the same-volume path.
- `FileCrossVolumeMoveMutationProofTests.FidelityWrapperRejectsInnerSuccessWithoutDispositionProof` proves the production fidelity wrapper cannot treat a provider return as destructive success unless the exact inner lease reports `SourceDeleteMutationPerformed`.
- `FileCrossVolumeMoveActionHistoryInvariantTests` plus `FileCrossVolumeMoveRecoveryEvidenceStoreTests` distinguish a safe durable destination commit from copy-barrier recovery observation and retain source-delete barrier uncertainty through recovery. Recovery-only destination identity/fingerprint evidence must not be upgraded into a known safe copied/source-retained state, including after SQLite reopen.
- `FileCrossVolumeMovePersistedRecoveryCorruptionTests` proves malformed source-delete recovery chronology and wrong-volume destination evidence are rejected during hydration rather than trusted merely because SQLite can structurally store them.

## Current disposition scope

The raw Windows source-delete primitive uses `FileDispositionInformationEx` on the exact opened source link with delete, POSIX-semantics and force-image-section-check flags. It intentionally does **not** use `FILE_DISPOSITION_ON_CLOSE`.

Draft #185 also intentionally does **not** use `FILE_DISPOSITION_IGNORE_READONLY_ATTRIBUTE`. ReadOnly is therefore excluded by both the early cross-volume source subset and the later Core preservation classifier. #190 owns later support; the later destructive checkpoint remains required even when early preflight has already refused the common predictable case.

Durable `Moved` is recorded only after the exact source-delete capability/evidence lease has released. A disposition or release failure after `SourceDeleteStarted` remains recovery-sensitive; historical recovery evidence is not automatic retry/delete authority.

## Security-policy validation (#186)

The selected product contract is Windows-style destination-default/inherited security, not preservation of the source security descriptor. The cross-volume fidelity verifier must not require `ACCESS_SYSTEM_SECURITY`, SACL reads, or source/destination descriptor equality.

`WindowsFileCopyMutationSecurityPolicyTests.ReviewedCopyCreatesDestinationWithDefaultSecurityInsteadOfCloningSourceNullDacl` is the executable ordinary-token proof for that contract. Before #186 is closed, record a successful run through `tools/test-cross-volume-move-security.ps1` on the exact final head. Do not substitute an elevated run and do not interpret a failed privileged security read as evidence.

## Evidence to record

For PR #185 / issues #184, #186, #187 and #191, record:

- exact git commit SHA;
- Windows version;
- .NET SDK version;
- confirmation that both dedicated scripts ran under an ordinary unelevated token;
- source and destination two-volume test roots;
- confirmation that the test validator observed different source/destination volume serials;
- confirmation that the always-on identity-bound volume-GUID regression passed;
- confirmation that the equal-serial collision/unavailable-proof validator regressions passed;
- confirmation that the always-on hard-link canonical-path binding regression passed rather than being inconclusive;
- confirmation that source ADS, source EA and ReadOnly preflight cases each produced no destination Copy and no composite history row;
- confirmation that the direct-engine ADS/EA/ReadOnly fallback cases still refused before `SourceDeleteStarted` without recovery-sensitive history;
- confirmation that the post-barrier ADS and post-barrier EA tests both reached `SourceDeleteStarted`, refused the second real fidelity proof, retained both paths, and wrote `RecoveryRequired` history;
- confirmation that the post-barrier main-stream writer attempt was rejected with `ERROR_SHARING_VIOLATION` and the same transaction then completed through the real second proof and source disposition;
- complete `tools/test-local.ps1` result;
- complete `tools/test-cross-volume-move-security.ps1` result;
- complete `tools/test-cross-volume-move-native.ps1` result;
- any skipped/inconclusive tests from the ordinary suite.

The one-command batch wrapper writes `environment.txt`, `commands.txt`, the three complete numbered logs and `PASS.txt` into its evidence directory when all three required commands succeed. Those files are intended to make the exact-head evidence easy to attach or summarize later; they are not a substitute for reviewing failures or inconclusive tests.

Do not mark #185 ready merely because the source/model verifiers are green. Production `WindowsMoveOperationExecutionValidator` must remain fail-closed for different-volume Move until this exact-head native evidence is complete.
