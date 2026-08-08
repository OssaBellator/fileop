# Windows file Copy mutation primitive

## Scope

`WindowsFileCopyMutationPrimitive` is the first concrete target-filesystem mutation implementation for FileOp. It is deliberately limited to one freshly validated regular file whose destination is still expected to be missing. It is **not wired into the Files UI** in this slice.

The primitive exists behind `IFileCopyMutationPrimitive`, so `FileCopyOperationExecutor` still owns the durable `MutationStarted` / `CommitCopy` ordering, progress and recovery settlement.

## Explicit fresh-root request

`FileCopyMutationRequest` carries the fresh validation item together with the exact fresh source and destination canonical root objects. That gives the mutation primitive both root paths and root `FileIdentity` values without hidden mutable state or a second lookup.

After the executor persists `MutationStarted`, the Windows primitive reopens both canonical parent directories and requires each handle to match the request's canonical path **and** stable directory identity before either leaf is opened or created. A root object swapped between fresh validation and mutation-handle acquisition is therefore rejected.

## Handle-bound namespace operations

A path-only `File.Copy` would reopen the source and destination namespace after canonical validation and could follow a changed junction, symbolic link or renamed directory. This implementation instead:

1. opens the canonical source and destination parent directories and keeps both handles alive;
2. rejects a parent handle if it is a reparse point, its final handle-resolved path changes, or its stable identity differs from the fresh request;
3. opens the source **relative to the source-directory handle** with `NtCreateFile` and `FILE_OPEN_REPARSE_POINT`;
4. compares the opened source's volume/file identity and final path with the fresh validation item;
5. creates the destination **relative to the destination-directory handle** with `NtCreateFile`, `FILE_CREATE`, `FILE_WRITE_THROUGH` and `FILE_OPEN_REPARSE_POINT`;
6. never uses an overwrite/open-if disposition;
7. copies bytes through the bound handles, requires a final `FlushFileBuffers`, then captures the destination's stable identity and final path;
8. returns an `IFileCopyMutationLease` that retains the source directory, destination directory, source file and created destination file handles through durable `CommitCopy` and progress reporting.

`FILE_CREATE` is the exclusive-create barrier: if another process creates the leaf after validation, the mutation fails rather than opening or replacing it. Once the directory handle is acquired, later renames/replacement of the textual parent path cannot redirect the relative create. The parent handles also omit delete sharing while the lease is alive.

The destination file handle does not share write or delete access while the lease is alive. This keeps the created object from being replaced, renamed or deleted before the executor has persisted the destination identity and reported progress.

## Source consistency

The source is reopened relative to its verified canonical parent with the narrow `FILE_READ_DATA | FILE_READ_ATTRIBUTES | SYNCHRONIZE` access set and no write/delete sharing. A process that already has an incompatible writer prevents the Copy from starting; after the source handle is acquired, new incompatible writers cannot open it through normal Windows sharing semantics. The primitive verifies the source final path and stable identity again after the handle is acquired, so replacing the validated source path with a different file is rejected.

This slice copies file contents only. Metadata/ACL/alternate-stream fidelity is intentionally not claimed yet.

## Durability and failure semantics

Destination creation requests `FILE_WRITE_THROUGH`, and after all bytes are written `FlushFileBuffers` is still required to succeed before a lease is returned. The destination handle includes generic write access for that explicit flush. This intentionally layers per-write write-through behavior with a final flush before the durable action-history commit barrier.

The executor has already persisted `MutationStarted` before invoking the primitive, so any exception after destination creation is conservatively settled as recovery-sensitive. The primitive does not guess whether a partial destination should be deleted.

A successful primitive return does not by itself mean the operation is committed. The destination handle remains locked in the lease until the executor durably records `CommitCopy(destination identity)` and reports entry progress.

## Validation without GitHub Actions

The focused zero-Actions gate is:

```powershell
pwsh -File tools/test-copy-executor-local.ps1
```

It includes `verify_windows_file_copy_mutation.py`. The Python model exercises both sides of the namespace boundary: a parent-object replacement **before** handle acquisition changes the expected object identity, while a rename/replacement **after** handle acquisition cannot redirect a directory-relative exclusive create. It also checks source identity stability, collision refusal and destination flush ordering. Source guards require explicit fresh roots, `NtCreateFile` relative roots, `FILE_CREATE`, `FILE_WRITE_THROUGH`, reparse-point bypass, handle identity/final-path checks, destination flushing and absence of high-level overwrite/move/delete APIs.

Real Windows regression tests cover content copying, exclusive collision refusal, source-identity replacement, destination-root replacement, invalid root identity and lease-held delete/rename sharing. They require the Windows/.NET test environment. Because this environment cannot run those native tests, this branch intentionally does not add Files UI execution wiring.

## Next boundary

Before exposing Run/Execute in the Files UI, run the included Windows regression suite on a local Windows machine with .NET 10 and make explicit metadata-fidelity decisions (timestamps/attributes/ACLs/alternate streams) rather than assuming byte-copy equivalence is a complete Explorer-style Copy contract. Directory Copy, Move and Undo remain separate later slices.
