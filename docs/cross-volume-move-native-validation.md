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

The test code resolves the roots through the real Windows execution validator and fails if their stable filesystem volume serials are equal. Drive-letter text is not treated as proof of a cross-volume relationship.

## Matrix exercised by the dedicated two-volume gate

The `CrossVolumeMoveNative` category currently requires all of these cases:

1. **Successful composite Move** — the ordinary execution validator, real exclusive-create Copy primitive, composite SQLite journal, fidelity wrapper and identity-bound same-handle source-delete primitive complete one Move; the selected source entry disappears and destination bytes match.
2. **Source named stream refusal** — a source ADS exists before execution. Copy may commit the main stream, but destructive completion must fail before `SourceDeleteStarted`; the original source including its ADS and the committed destination Copy both remain, with safe retained-duplicate history rather than recovery ambiguity.
3. **Source extended-attribute refusal** — a real `FILE_FULL_EA_INFORMATION` entry is written to the source before execution. The committed destination main-stream Copy may remain, but source deletion must be refused before the destructive barrier and history must settle as a safe retained duplicate. The matrix fails if the supplied source filesystem cannot create/query EAs; unsupported EA coverage is not silently skipped.
4. **Cancellation after real Copy** — a wrapper around the real Copy primitive requests cancellation while the Copy lease is still live. The executor must finish the durable destination commit and settle at `DestinationCommitted`, retaining both files and creating no source-delete authority.
5. **Selected-entry hard-link semantics** — the selected source path has another hard link on the source volume. Cross-volume Move removes only the selected directory entry, creates the destination copy, and leaves the other source-volume hard link valid with the original content.
6. **Post-barrier fidelity refusal with the real Windows lease** — real Copy and the real raw four-handle source-delete lease are used, while an injected evidence-only verifier accepts the first proof and rejects the second after `SourceDeleteStarted`. The raw delete mutation must not run, both paths must still be directly observable, and durable history must settle as `RecoveryRequired`. Generic recovery history remains conservative and does not claim a known retained duplicate merely because this deterministic test can directly observe one.

Separate always-on Windows tests pin lower-level assumptions before the explicit two-volume run:

- `FileCrossVolumeMoveFidelityShareCompatibilityTests` proves that pre-existing main-stream writers and writable mappings block destructive-lease acquisition, the live lease blocks new main-stream writers, attribute/EA access is not misrepresented as share-frozen, deleting one hard link leaves another source-volume link valid, and fidelity reopens remain compatible with the live DELETE-capable handle.
- `FileCrossVolumeMoveHardLinkPathBindingTests.CanonicalResolverPreservesTheSpecificOpenedHardLinkName` proves that resolving two names for one hard-linked file preserves the specific selected directory entry while reporting the same filesystem identity. If the current Windows/filesystem combination cannot preserve that path binding, selected-entry multi-link Move is not considered validated.

## Security-policy validation (#186)

The selected product contract is Windows-style destination-default/inherited security, not preservation of the source security descriptor. The cross-volume fidelity verifier must not require `ACCESS_SYSTEM_SECURITY`, SACL reads, or source/destination descriptor equality.

`WindowsFileCopyMutationSecurityPolicyTests.ReviewedCopyCreatesDestinationWithDefaultSecurityInsteadOfCloningSourceNullDacl` is the executable ordinary-token proof for that contract. Before #186 is closed, record a successful run through `tools/test-cross-volume-move-security.ps1` on the exact final head. Do not substitute an elevated run and do not interpret a failed privileged security read as evidence.

## Evidence to record

For PR #185 / issues #184, #186 and #187, record:

- exact git commit SHA;
- Windows version;
- .NET SDK version;
- confirmation that `tools/test-cross-volume-move-security.ps1` ran under an ordinary unelevated token;
- source and destination two-volume test roots;
- confirmation that the test validator observed different source/destination volume serials;
- confirmation that the always-on hard-link canonical-path binding regression passed rather than being inconclusive;
- complete `tools/test-local.ps1` result;
- complete `tools/test-cross-volume-move-security.ps1` result;
- complete `tools/test-cross-volume-move-native.ps1` result;
- any skipped/inconclusive tests from the ordinary suite.

Do not mark #185 ready merely because the source/model verifiers are green. Production `WindowsMoveOperationExecutionValidator` must remain fail-closed for different-volume Move until this exact-head native evidence is complete.
