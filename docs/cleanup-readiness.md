# Known-location cleanup readiness preview

## Purpose

Known-location review evidence can identify an old, large package/archive or current-user Temp candidate, but provenance, age, extension and size do not make deletion safe.

**Check readiness** revalidates current path evidence without creating or authorizing a cleanup operation. It remains valid for a reviewed candidate on another indexed volume because the readiness service resolves the candidate/root directly and does not depend on Files' active browse volume.

## Exact review binding

The candidate list preserves the owning review root, provenance and rule ID for every displayed row. A readiness click passes **path + review root + provenance + rule ID** back to the coordinator.

The coordinator requires exactly one candidate in the current cached review to match that full tuple. This matters when known-location roots overlap: the same filesystem path can theoretically satisfy both a Downloads rule and the current-user Temp rule. A path-only lookup would silently pick whichever location appeared first. Exact tuple binding instead rejects missing or duplicate matches and never substitutes another provenance/root merely because the path string is the same.

This binding is still evidence selection only. It grants no mutation capability.

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

The Core readiness detail uses the same current wording: a consistent preview says explicitly that it does not authorize deletion and that later permanent deletion must start again from Files. It no longer claims the reviewed delete recovery/history boundary is unimplemented.

## Cross-volume lifecycle

Readiness is rebound to the exact candidate in the current cached native known-location review and serialized through the existing Storage gate. The review snapshot preserves both the active primary root and its `VolumeIdentity`; readiness requires both to match the current native source before starting its path checks.

For a cross-volume candidate, the known-location producer has already required a current checkpointed secondary native index. The readiness service then performs its own current path/identity checks directly against that candidate and known-location root. Files handoff can remain unavailable for that candidate without disabling readiness.

The coordinator retains the exact review object while the Windows read-only check is in flight. Before rendering, it requires that object still be the current cached review and rechecks the active primary root **and volume identity**. A same-drive-letter volume replacement therefore invalidates stale review evidence instead of passing merely because the root string is unchanged.

Any new known-location review loading/unavailable/completed state clears the previous readiness result as current evidence.

## Validation

`tools/verify_cleanup_readiness.py` is wired into `tools/test-local.ps1 -OfflineOnly`.

Its deterministic/randomized model covers current/changed/blocked/unavailable states, reparse and canonical-escape boundaries, volume mismatch, two-current-read identity stability, indexed size/time drift, overlapping known-location rows, and same-root primary-volume identity replacement. The overlap model requires a unique full path/root/provenance/rule match and rejects duplicate exact bindings.

Repository guards pin zero-access metadata reads, the existing canonical resolver, exact UI-row/coordinator binding, snapshot primary-volume identity persistence, pre/post-read source identity checks, post-read review-object freshness, protocol v8, absence of mutation APIs in the readiness path, `CleanupMutationAuthorized == false`, current delete-boundary wording, and the separate Files delete authorization/orchestration boundary.

Focused .NET tests cover Core readiness semantics plus a Windows-only current-handle metadata reader. The complete Windows local gate remains mandatory before merging changes to this boundary.
