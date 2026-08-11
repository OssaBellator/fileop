# Known-location cleanup readiness preview

## Purpose

Known-location review evidence can identify an old, large package/archive or current-user Temp candidate, but provenance, age, extension and size do not make deletion safe.

This slice adds an explicit **Check readiness** action that revalidates current path evidence without creating or authorizing a cleanup operation.

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

FileOp's current durable action-history/recovery path supports the existing Copy workflow; it does not yet provide a reviewed delete executor with durable delete recovery/undo semantics.

Accordingly this preview does not:

- add `FileOperationKind.Delete`;
- create a Copy/Move plan as a proxy for deletion;
- run a deletion executor;
- queue a cleanup action;
- expose a delete button;
- call the indexing helper or change protocol v8;
- claim that `CurrentEvidenceConsistent` means safe to delete.

A later cleanup implementation must add a separately reviewed destructive-operation contract with action-time identity/content revalidation, recovery/history semantics, authorization and explicit user intent.

## Lifecycle

Readiness is rebound to the exact candidate in the current cached native known-location review and serialized through the existing Storage gate. The review/root/source is checked again before the result is rendered.

Any new known-location review loading/unavailable/completed state clears the previous readiness result as current evidence.

## Validation

`tools/verify_cleanup_readiness.py` is wired into `tools/test-local.ps1 -OfflineOnly`.

Its deterministic model covers current/changed/blocked/unavailable states, reparse and canonical-escape boundaries, volume mismatch, two-current-read identity stability and indexed size/time drift. Repository guards pin zero-access metadata reads, the existing canonical resolver, protocol v8, absence of delete/mutation APIs, `CleanupMutationAuthorized == false`, and the absence of `FileOperationKind.Delete`.

Focused .NET tests cover Core readiness semantics plus a Windows-only current-handle metadata reader. Native Windows/.NET/WinUI execution is not claimed in this sandbox.
