# Cleanup physical-release evidence

## Purpose

A known-location candidate can pass the read-only cleanup-readiness checks and still fail to release physical storage when one path is removed. Hard links are the important example: multiple names can reference one physical file object.

This slice therefore extends the existing **Check readiness** action with current allocation and hard-link evidence. It remains evidence only and does not authorize deletion.

## Current-handle evidence

The second cleanup-readiness metadata handle now reads both:

- `BY_HANDLE_FILE_INFORMATION` for current file identity, file attributes, logical length, hard-link count and last-write FILETIME; and
- `FILE_STANDARD_INFO` through `GetFileInformationByHandleEx` for current allocation size, end-of-file, hard-link count and delete-pending state.

The handle uses zero desired access, read/write/delete sharing, `FILE_FLAG_BACKUP_SEMANTICS`, and `FILE_FLAG_OPEN_REPARSE_POINT`. A leaf reparse point introduced between the two current checks is therefore opened as the reparse point instead of being followed.

FileOp fails closed when:

- the two handle-level logical lengths disagree;
- the two hard-link counts disagree or either is zero;
- allocation is negative;
- the file is already delete-pending;
- the handle reports a directory;
- the existing canonical/current identity, path, size or last-write checks fail.

No pathname allocation query is used.

## Per-path physical-release upper bound

For `CurrentEvidenceConsistent` only:

- **one current hard link:** current allocation is exposed as the current per-path physical-release upper bound;
- **more than one current hard link:** the upper bound for deleting this one name is **0 bytes**, because another link would still reference the physical file object.

Changed, blocked or unavailable readiness states always expose a zero physical-release upper bound.

This is deliberately narrower than the duplicate-content physical-reclaim model: it concerns one reviewed path and asks whether removing that one name could currently release its file allocation.

## Evidence lifetime and limitations

Allocation and hard-link evidence is current at the time of the readiness check and can become stale immediately afterward.

The indexed known-location review still does not retain a prior physical identity, so this evidence does not prove object continuity since indexing. It also does not prove that a later deletion will succeed or that Windows will make every reported allocated byte immediately available.

## Mutation boundary

Physical-release evidence does not authorize deletion.

`StorageCleanupReadinessPreview.CleanupMutationAuthorized` remains always `false`. This slice does not add a delete executor, `FileOperationKind.Delete`, a delete button, a cleanup queue, recovery semantics, protocol work, or any write/delete/control API.

A later destructive workflow still needs durable delete recovery/history, explicit user intent, action-time canonical/identity/content revalidation and final authorization.

## UI

When readiness is consistent, the known-location view shows current allocation, hard-link count, and the per-path physical-release upper bound. Multi-link files explicitly show a zero-byte upper bound for deleting the one reviewed name.

The number is never rendered for changed/blocked/unavailable evidence as though it were current reclaim.

## Validation

`tools/verify_cleanup_physical_release.py` is wired into `tools/test-local.ps1 -OfflineOnly`.

Its deterministic model covers singleton and multi-link evidence, invalid allocation/link evidence, non-current readiness states and the permanent non-authorizing boundary across 50,000 randomized states.

Focused .NET tests cover portable physical-release semantics. The existing Windows current-file reader regression now also requires nonnegative current allocation and a positive hard-link count. Native Windows/.NET/WinUI execution is not claimed in this sandbox.
