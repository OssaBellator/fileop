# File delete read-only stability lease

This layer follows the explicit user-authorization receipt from `file-delete-user-authorization.md` with a per-entry **read-only stability lease**. It proves and temporarily holds the namespace/file identity that the user reviewed, but it still does not authorize or perform deletion.

## One authorized ordinal at a time

`FileDeleteOperationStabilityLeaseRequest` binds one exact `FileDeleteOperationUserAuthorizationReceipt` to one captured plan ordinal. The request rejects:

- absent/non-positive authorization semantics;
- any receipt that claims mutation authority;
- an out-of-range ordinal;
- incomplete plan/authorization evidence;
- a substituted/reordered entry;
- directory entries.

The provider therefore holds at most one file leaf plus its parent per lease rather than opening every candidate in a potentially large cleanup plan at once.

`FileDeleteOperationStabilityLeaseEvidence` must exactly match the canonical root path/identity and canonical file path/identity captured by the user-authorization receipt. `IsBoundTo(...)` requires the same receipt object and ordinal. Request, evidence, and live lease all expose `DeleteMutationAuthorized == false`.

## Windows handle binding

`WindowsFileDeleteOperationStabilityLeaseProvider` reuses the same reviewed Windows handle strategy used by the Copy mutation primitive without reusing Copy's write/create authority:

1. classify the authorized canonical root/file again through `WindowsFileDeleteProtectedLocationPolicy`;
2. open the canonical parent with `CreateFileW` using metadata/traverse access only (`FILE_TRAVERSE | FILE_READ_ATTRIBUTES | SYNCHRONIZE`), `OPEN_EXISTING`, backup-semantics + open-reparse-point flags, and read/write sharing **without delete sharing**;
3. verify parent type, non-reparse state, final handle path, and stable `FileIdentity` against the consent receipt;
4. open the exact one-component leaf relative to the held parent with `NtCreateFile(RootDirectory=...)`;
5. request only `FILE_READ_ATTRIBUTES | SYNCHRONIZE` for the leaf, use `FILE_OPEN`, `FILE_NON_DIRECTORY_FILE`, synchronous non-alert semantics, and `FILE_OPEN_REPARSE_POINT`;
6. verify leaf type, non-reparse state, final handle path, and stable `FileIdentity` against the consent receipt;
7. revalidate the held parent again after the relative leaf open;
8. return a lease retaining both handles until disposal.

The leaf uses `FileShare.Read`. It therefore **denies write and delete sharing** while the lease is alive. An incompatible existing writer causes lease acquisition to fail closed, and a newly opened writer/delete/rename cannot become compatible after acquisition. The parent handle likewise omits delete sharing so the authorized parent itself cannot be renamed/deleted while held.

The provider does not read file contents. It does not request `FILE_READ_DATA`, write access, generic write/all access, or `DELETE` access. It uses no create/open-if disposition.

## What this lease does and does not freeze

A successful lease proves that the exact canonical root/file identities reviewed by the user were reacquired and remain held under the sharing contract above.

Because the leaf denies write sharing, a new ordinary incompatible writer cannot begin while the lease is held. This is stronger than point-in-time identity evidence, but it is still not a historical proof that bytes/metadata were unchanged before acquisition. Existing cleanup/readiness evidence can already be stale before this lease begins and must not be promoted automatically into deletion permission.

Provider-managed filesystem effects outside this handle/share model are also not claimed away.

## Not a mutation lease

This is deliberately **not a mutation lease**. The held leaf does not request `DELETE` access and the interface exposes no handle or mutation method. Consequently a later delete primitive cannot safely release this lease and reopen by path—the release/reopen gap would recreate the race this work is meant to close.

The next destructive design step must therefore be reviewed together with durable delete execution/history semantics. Only after a durable pre-mutation barrier exists should the Windows leaf acquisition be explicitly upgraded to include the minimum delete right and a same-handle relative mutation path that remains bound through durable settlement.

This slice does **not** add:

- `FileOperationKind.Delete`;
- generic `IFileOperationExecutor` integration or a delete state machine;
- delete action-history schema/transitions;
- `DELETE` desired access;
- `File.Delete`, `Directory.Delete`, `DeleteFileW`, `SetFileInformationByHandle`, shell recycle, or another mutation API;
- directory recursion;
- Storage cleanup action UI;
- App/Indexer wiring;
- indexing-helper protocol changes.

Protocol remains v8.

## Validation

`tools/verify_file_delete_stability_lease.py` models authorization, ordinal/root/file/policy/writer gates, exact identity evidence, live write/delete/root-rename holds, disposal, and immutable non-mutation authority. At its standalone seed it performs **539,045 assertions across 50,000 randomized states**.

When reached through the existing authorization parent, the deterministic nested seed contributes **540,077 stability assertions**. Combined with **456,643** authorization assertions, **456,090** canonical delete-validation assertions, and **150,009** existing Copy/Move execution-validation assertions, the repository's existing single direct execution-validation gate now carries **1,602,819 randomized assertions across 50,000 cases** before source checks.

Source guards require metadata-only access, root-relative `NtCreateFile`, exact final-path/identity checks, post-leaf root revalidation, protected-location reuse, `FileShare.Read` on the leaf, no `DELETE`/write/content-read/create/mutation API, no generic executor/history integration, unchanged Copy/Move operation enum, cleanup non-authorization, and protocol v8.

Focused Windows regressions exercise real sharing/identity behavior. They are included automatically by the repository's full `FileOp.Windows.Tests` local Windows gate. Native Windows/.NET execution is not claimed in the current sandbox.
