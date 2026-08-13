# Known-location cleanup readiness preview

## Purpose

Known-location review evidence can identify an old, large package/archive or current-user Temp candidate, but provenance, age, extension and size do not make deletion safe.

**Check readiness** revalidates current path evidence without creating or authorizing a cleanup operation. It remains valid for a reviewed candidate on another indexed volume because the readiness service resolves the candidate/root directly and does not depend on Files' active browse volume.

## Evidence checked

For the candidate's known-location root and file, FileOp reuses the existing Files canonical path boundary through `WindowsFileOperationCanonicalPathResolver`.

The preview requires:

- the known-location root still resolves as a directory;
- the root and candidate are not leaf reparse points;
- the candidate still resolves as a regular file;
- the candidate's final canonical path stays inside the final canonical known-location root;
- root and candidate current identities are on the same volume;
- a second read-only current-handle metadata snapshot resolves to the same current final path and file identity as canonical validation;
- current logical length and last-write time match the indexed review candidate.

The second current snapshot uses zero desired access with read/write/delete sharing and reads final path, filesystem identity, size and last-write FILETIME. It performs no write/control/delete operation.

## What the preview cannot prove

The known-location index model does not retain the file's physical identity in the original review candidate. Therefore a matching current size/time plus a stable identity across the two current reads does **not** prove that this is the same physical file object that was indexed earlier.

The UI states that limitation explicitly.

## Result states

- `CurrentEvidenceConsistent`: current canonical/current-handle evidence is internally consistent with the indexed path, size and last-write evidence.
- `CandidateChanged`: the candidate/root disappeared, changed type, changed between current reads, or no longer matches indexed size/time.
- `Blocked`: canonical containment, reparse-point or volume boundaries are unsafe for cleanup progression.
- `Unavailable`: current path/metadata evidence cannot be resolved reliably.

`StorageCleanupReadinessPreview.CleanupMutationAuthorized` is always `false`.

## Delete/recovery boundary

FileOp now has a separately reviewed Files file-delete session with durable delete action history, recovery discovery, canonical execution validation, explicit user confirmation, and the identity-bound same-handle mutation pipeline.

Cleanup readiness still does **not** grant access to that mutation boundary. It does not:

- add `FileOperationKind.Delete`;
- create a Copy/Move plan as a proxy for deletion;
- issue a `FileDeleteOperationUserAuthorizationReceipt`;
- run `FileDeleteOperationOrchestrator`;
- queue a cleanup action;
- expose a Storage delete button;
- call the indexing helper or change protocol v8;
- claim that `CurrentEvidenceConsistent` means safe to delete.

If the user later chooses permanent deletion, they must select the file in Files and independently pass the Files delete session's fresh preflight, recovery checks, canonical identity/protected-location validation and explicit confirmation. Readiness evidence is not reusable consent or mutation authority.

## Cross-volume lifecycle

Readiness is rebound to the exact candidate in the current cached native known-location review and serialized through the existing Storage gate. The review's active-primary source is checked again before the result is rendered.

For a cross-volume candidate, the known-location producer has already required a current checkpointed secondary native index. The readiness service then performs its own current path/identity checks directly against that candidate and known-location root. Files handoff can remain unavailable for that candidate without disabling readiness.

Any new known-location review loading/unavailable/completed state clears the previous readiness result as current evidence.

## Validation

`tools/verify_cleanup_readiness.py` is wired into `tools/test-local.ps1 -OfflineOnly`.

Its deterministic model covers current/changed/blocked/unavailable states, reparse and canonical-escape boundaries, volume mismatch, two-current-read identity stability and indexed size/time drift. Repository guards pin zero-access metadata reads, the existing canonical resolver, protocol v8, absence of mutation APIs in the readiness path, `CleanupMutationAuthorized == false`, and the separate Files delete authorization/orchestration boundary.

Focused .NET tests cover Core readiness semantics plus a Windows-only current-handle metadata reader. The complete Windows local gate remains mandatory before merging changes to this boundary.
