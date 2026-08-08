# Windows file Copy mutation primitive

## Scope

`WindowsFileCopyMutationPrimitive` is the first concrete target-filesystem mutation implementation for FileOp. It is deliberately limited to one already-validated regular file whose destination is still expected to be missing. It is **not wired into the Files UI** in this slice.

The primitive exists behind `IFileCopyMutationPrimitive`, so the existing `FileCopyOperationExecutor` still owns durable `MutationStarted` / `CommitCopy` ordering, progress and recovery settlement.

## Validation root binding

`FileOperationExecutionValidationResult` now binds every returned validation item to the canonical source/destination root paths and stable root identities used to produce that result. The binding is attached outside the record's value/equality fields and is preserved by the record copy constructor, so existing validation-item construction/equality semantics remain unchanged.

The mutation primitive refuses an unbound item. After the executor's fresh validation and durable `MutationStarted` barrier, it reopens both canonical parent directories and requires each handle to match the bound canonical path **and** the bound directory `FileIdentity`. A root object swapped between validation and handle acquisition is therefore rejected before either leaf is opened or created.

## Handle-bound namespace operations

A path-only `File.Copy` would reopen the source and destination namespace after canonical validation and could follow a changed junction, symbolic link or renamed directory. This implementation instead:

1. opens the canonical source and destination parent directories and keeps both handles alive;
2. rejects a parent handle if it is a reparse point, its final handle-resolved path changes, or its stable identity differs from the fresh validation root binding;
3. opens the source **relative to the source-directory handle** with `NtCreateFile` and `FILE_OPEN_REPARSE_POINT`;
4. compares the opened source's volume/file identity and final path with the fresh validation result;
5. creates the destination **relative to the destination-directory handle** with `NtCreateFile`, `FILE_CREATE` and `FILE_OPEN_REPARSE_POINT`;
6. never uses an overwrite/open-if disposition;
7. copies bytes through the bound handles, flushes the destination with `FlushFileBuffers`, then captures its stable identity and final path;
8. returns an `IFileCopyMutationLease` that retains the source directory, destination directory, source file and created destination file handles through durable `CommitCopy` and progress reporting.

`FILE_CREATE` is the exclusive-create barrier: if another process creates the leaf after validation, the mutation fails rather than opening or replacing it. The directory handle is the namespace anchor, so once acquired, later renames or replacement of the textual parent path cannot redirect the relative create.

The destination file handle does not share write or delete access while the lease is alive. The parent-directory handles do not share delete access. This keeps the created object and its anchored directory from being renamed/deleted before the executor has persisted the destination identity and reported progress.

## Source consistency

The source is reopened relative to its canonical parent with no write/delete sharing. A process that already has an incompatible writer prevents the Copy from starting; after the source handle is acquired, new writers cannot open it through normal Windows sharing semantics. The primitive verifies the source identity again after the handle is acquired, so replacing the validated source path with a different file is rejected.

This slice copies file contents only. Metadata/ACL/alternate-stream fidelity is intentionally not claimed yet.

## Durability and failure semantics

After all bytes are written, `FlushFileBuffers` is required to succeed before a lease is returned. The destination is opened with generic write access as required by the Win32 flush contract. The executor has already persisted `MutationStarted` before invoking the primitive, so any exception after destination creation is conservatively settled as recovery-sensitive. The primitive does not guess whether a partial destination should be deleted.

A successful return does not by itself mean the operation is committed. The destination handle remains locked in the lease until the executor durably records `CommitCopy(destination identity)` and reports entry progress.

## Validation without GitHub Actions

The focused zero-Actions gate is:

```powershell
pwsh -File tools/test-copy-executor-local.ps1
```

It now includes `verify_windows_file_copy_mutation.py`. The Python model exercises both sides of the namespace boundary: a parent-object replacement **before** handle acquisition must be detected by identity mismatch, while a rename/replacement **after** handle acquisition cannot redirect the directory-relative exclusive create. Source guards require validation root binding, `NtCreateFile` relative roots, `FILE_CREATE`, reparse-point bypass, handle identity/final-path checks, destination flushing and absence of high-level overwrite/move/delete APIs.

Real Windows regression tests are included for content copying, exclusive collision refusal, source-identity replacement, destination-root replacement and lease-held delete/rename sharing. They require the Windows/.NET test environment. Because this environment cannot run those Windows-specific tests, this branch remains an isolated mutation slice and does not add Files UI execution wiring.

## Next boundary

Before exposing Run/Execute in the Files UI, run the included Windows regression suite on a local Windows machine with .NET 10 and then add metadata-fidelity decisions (timestamps/attributes/ACLs/alternate streams) explicitly rather than assuming byte-copy equivalence is a complete Explorer-style Copy contract. Directory Copy, Move and Undo remain separate later slices.
