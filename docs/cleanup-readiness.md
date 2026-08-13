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
- add a cleanup-readiness Indexer operation or use the helper as a file-content/current-file metadata oracle;
- change protocol v8;
- claim that `CurrentEvidenceConsistent` means safe to delete.

The coordinator **does** use the existing protocol-v8 `GetVolumes` catalog before and after the direct desktop-user file check to prove that the indexed source identity/root/checkpoint behind the cached review remains current. Those catalog reads provide source provenance only; the readiness service still obtains current file evidence directly under the desktop user's filesystem access.

If the user later chooses permanent deletion, they must select the file in Files and independently pass the Files delete session's fresh preflight, recovery checks, canonical identity/protected-location validation and explicit confirmation. Readiness evidence is not reusable consent or mutation authority.

The Core readiness detail uses the same current wording: a consistent preview says explicitly that it does not authorize deletion and that later permanent deletion must start again from Files. It no longer claims the reviewed delete recovery/history boundary is unimplemented.

## Cross-volume lifecycle

Readiness is rebound to the exact candidate in the current cached native known-location review and serialized through the existing Storage gate. The snapshot preserves the active primary root/`VolumeIdentity`, and every available location also preserves the `VolumeIdentity` of the indexed volume that produced its evidence.

Before the direct current-path read, the coordinator reads the current native volume catalog through the engine's existing search/native gate order. It requires one unique descriptor for the active primary and for the matched location's filesystem root, requires both identities/roots to match the cached review, and requires both descriptors to still expose a checkpoint. A missing checkpoint, temporary busy descriptor, duplicate root descriptor, same-drive-letter replacement, or secondary-volume replacement therefore invalidates the cached review evidence before the readiness service is called.

For a cross-volume candidate, the readiness service then performs its separate current canonical/path/file-identity checks directly against that candidate and known-location root. Files handoff can remain unavailable for that candidate without disabling readiness.

The coordinator retains the exact review object while the Windows read-only check is in flight. Before rendering, it again requires that object to be current, rechecks the active primary identity/root, and repeats the catalog binding for the matched location's original indexed source. A source replacement or checkpoint loss during the current-file read therefore prevents old review evidence from repainting as current merely because the path, size or timestamp still looks plausible.

Any new known-location review loading/unavailable/completed state clears the previous readiness result as current evidence.

## Validation

`tools/verify_cleanup_readiness.py` and `tools/verify_known_location_source_identity.py` are wired into `tools/test-local.ps1 -OfflineOnly`.

The cleanup-readiness deterministic/randomized model covers current/changed/blocked/unavailable states, reparse and canonical-escape boundaries, volume mismatch, two-current-read identity stability, indexed size/time drift, overlapping known-location rows, and same-root primary-volume identity replacement. The overlap model requires a unique full path/root/provenance/rule match and rejects duplicate exact bindings.

The focused source-identity model additionally covers per-location source identity persistence, secondary replacement after review capture, duplicate source descriptors, missing/lost checkpoints, same-volume source binding and unavailable-location handling. Repository guards pin that same-volume and cross-volume producers persist `SourceVolumeIdentity`, final snapshot publication revalidates all available location sources, and readiness revalidates the matched owning source before and after its direct current-file read.

Repository guards also pin zero-access metadata reads, the existing canonical resolver, exact UI-row/coordinator binding, snapshot primary-volume identity persistence, post-read review-object freshness, protocol v8, absence of mutation APIs in the readiness path, `CleanupMutationAuthorized == false`, current delete-boundary wording, and the separate Files delete authorization/orchestration boundary.

Focused .NET tests cover Core readiness semantics plus a Windows-only current-handle metadata reader. The complete Windows local gate remains mandatory before merging changes to this boundary.
