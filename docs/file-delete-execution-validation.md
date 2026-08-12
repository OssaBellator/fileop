# File-only delete canonical execution validation

This layer advances the read-only file delete preflight from `file-delete-preflight.md` with a **fresh handle-resolved validation pass** and conservative protected-location policy. It still does not authorize or perform deletion.

The existing Copy/Move `FileOperationExecutionValidation` contract remains unchanged. Delete stays destination-less and continues to use the separate `FileDeleteOperationPlan` introduced by #120.

## Fresh canonical identity evidence

`WindowsFileDeleteOperationExecutionValidator` reuses the existing `WindowsFileOperationCanonicalPathResolver` already used by Copy/Move execution validation. That resolver opens paths with zero desired access, obtains final handle-resolved paths, and captures stable volume/file-index identity evidence.

The delete validator resolves the captured source directory and then each eligible file again at validation time. A result can be `ReadyForAuthorizationReview` only when:

- the current source directory is a directory;
- the source directory is not a leaf reparse point;
- the source directory has stable handle-resolved identity evidence;
- the source directory passes protected-location policy;
- the captured file is still an exact direct child of the captured requested source directory;
- the captured leaf name still matches and is not alternate-data-stream syntax;
- the entry is a file, not a directory;
- the current source resolves as a file with stable identity evidence;
- the source leaf is not a reparse point;
- the current canonical file parent exactly matches the current canonical source directory;
- the canonical source file passes protected-location policy.

Canonical-parent mismatch blocks even when the requested path still appears to be under the captured source directory. This prevents a requested-path alias/reparse change from being treated as current containment evidence.

## Exact result provenance

`FileDeleteOperationExecutionValidationResult` also binds its evidence back to the exact immutable `FileDeleteOperationPlan`; a later authorization-review layer must not be able to combine a plan with independently substituted validation rows.

The Core result requires:

- the canonical source-directory observation's requested path to match the plan's captured source directory;
- either zero item rows for a root-level block, or exactly one row for every captured plan entry;
- non-empty item evidence to preserve the plan's original entry order and exact `FileOperationEntry` values;
- each item's canonical source observation to carry the requested path of that same captured entry.

Partial, reordered, duplicated/substituted, cross-entry and cross-plan evidence therefore cannot construct a valid authorization-review result. A protected or otherwise invalid root may still fail before entry resolution and return an itemless blocked result, but even that root observation must belong to the plan it reports about.

## Protected-location policy

`WindowsFileDeleteProtectedLocationPolicy` blocks review of:

- filesystem volume/share roots;
- Windows and System trees;
- Program Files and Program Files (x86) trees;
- ProgramData / CommonApplicationData;
- root-managed `$Recycle.Bin`, `System Volume Information`, `Recovery`, `Boot`, and `EFI` trees;
- residual extended/device namespace paths such as volume-GUID or device paths that the shared canonical resolver intentionally preserves instead of converting to DOS/UNC form.

The production parameterless policy derives the Windows-managed roots from `Environment.SpecialFolder`. An explicit protected-root constructor exists for deterministic composition/tests.

The shared canonical resolver normally normalizes ordinary `\\?\C:\...` and `\\?\UNC\...` results to DOS/UNC form. If a final path remains in an extended/device namespace, this delete policy fails closed rather than comparing that representation against DOS-path protected trees.

This is intentionally conservative. In particular, a file in Windows Temp or ProgramData is blocked by this first policy even if a future product might eventually support a narrower, specifically authorized cleanup lane there. The safe default is to require a later reviewed exception mechanism rather than silently broadening deletion scope.

Ordinary user-space descendants are not categorically blocked by this policy. Eligibility still does not imply deletion authorization.

## Non-authorization and TOCTOU boundary

`FileDeleteOperationExecutionValidationResult.DeleteMutationAuthorized` is always `false`.

A green result exposes only `CanRequestAuthorizationReview`. It is not a mutation lease and is not sufficient to call a delete primitive. The file can still change after validation; a future mutation lane must bind fresh user authorization to a mutation-time identity/handle lease and re-check the expected file identity immediately before mutation.

This slice therefore still does **not** add:

- `FileOperationKind.Delete`;
- `IFileOperationExecutor` integration;
- a delete state machine;
- fresh user delete authorization;
- a mutation-bound delete lease;
- delete history/recovery or recycle-bin semantics;
- directory recursion;
- Storage cleanup action UI;
- any delete primitive;
- indexing-helper protocol changes.

Protocol remains v8.

## Relationship to #120 preflight

#120's preflight remains a cheaper read-only source-shape check. This execution-validation layer does not reinterpret its `ReadyForFurtherReview` state as authorization. Instead it independently resolves fresh canonical source identities from the same immutable `FileDeleteOperationPlan`.

Storage cleanup readiness also remains non-authorizing and is not consumed by this validator.

## Validation

`tools/verify_file_delete_execution_validation.py` models canonical containment, identity requirements, exact plan/result provenance, file-only scope, protected locations, residual extended/device path blocking and immutable non-authorization. At 50,000 randomized states it performs **456,090 assertions** after the provenance hardening.

The existing Copy/Move execution-validation model contributes **150,009 assertions** at the same case count, for **606,099 directly composed execution-validation assertions** before source checks. Source guards require reuse of the existing canonical resolver, exact result provenance regressions and absence of executor wiring plus filesystem mutation primitives.

The existing `tools/verify_file_operation_execution_validation.py` imports and runs the delete child, so `tools/test-local.ps1 -OfflineOnly` keeps one direct execution-validation gate entry for the existing Copy/Move validation plus this delete-specific read-only authorization-review boundary.

Native Windows/.NET execution remains a local release-validation requirement.
