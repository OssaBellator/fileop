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
4. compares the opened source's volume/file identity and final path with the fresh validation item, then captures the supported basic metadata from that same source handle;
5. creates the destination **relative to the destination-directory handle** with `NtCreateFile`, `FILE_CREATE`, `FILE_WRITE_THROUGH` and `FILE_OPEN_REPARSE_POINT`;
6. never uses an overwrite/open-if disposition;
7. copies bytes through the bound handles and flushes destination data;
8. applies the supported basic metadata through the already-open destination handle with `SetFileInformationByHandle(FileBasicInfo)`, then requires another `FlushFileBuffers`;
9. captures the destination's stable identity and final path;
10. returns an `IFileCopyMutationLease` that retains the source directory, destination directory, source file and created destination file handles through durable `CommitCopy` and progress reporting.

`FILE_CREATE` is the exclusive-create barrier: if another process creates the leaf after validation, the mutation fails rather than opening or replacing it. Once the directory handle is acquired, later renames/replacement of the textual parent path cannot redirect the relative create. The parent handles also omit delete sharing while the lease is alive.

The destination file handle does not share write or delete access while the lease is alive. This keeps the created object from being replaced, renamed or deleted before the executor has persisted the destination identity and reported progress.

## Source consistency

The source is reopened relative to its verified canonical parent with the narrow `FILE_READ_DATA | FILE_READ_ATTRIBUTES | SYNCHRONIZE` access set and no write/delete sharing. A process that already has an incompatible writer prevents the Copy from starting; after the source handle is acquired, new incompatible writers cannot open it through normal Windows sharing semantics. The primitive verifies the source final path and stable identity again after the handle is acquired, so replacing the validated source path with a different file is rejected.

## Basic metadata fidelity

`WindowsFileCopyBasicMetadata` captures metadata from the same already-validated source file handle and applies it to the same exclusively created destination handle. It does not reopen either textual path.

The supported subset is intentionally narrow:

- creation time;
- last-access time;
- last-write time;
- read-only;
- hidden;
- system;
- archive;
- not-content-indexed.

`ChangeTime` is not copied. Storage-state attributes that need separate filesystem semantics are also excluded: reparse-point, sparse, compressed, encrypted, offline and temporary state. ACLs, alternate data streams and extended attributes remain separate future boundaries rather than being implied by a byte copy.

## Durability and failure semantics

Destination creation requests `FILE_WRITE_THROUGH`. After all bytes are written, `FlushFileBuffers` must succeed before metadata is changed. The basic metadata is then applied through the destination handle and a second, final `FlushFileBuffers` must succeed before a lease is returned. The destination handle's generic-write access includes the attribute-write permission required by `FileBasicInfo`.

The executor has already persisted `MutationStarted` before invoking the primitive, so any exception after destination creation is conservatively settled as recovery-sensitive. The primitive does not guess whether a partial destination should be deleted.

A successful primitive return does not by itself mean the operation is committed. The destination handle remains locked in the lease until the executor durably records `CommitCopy(destination identity)` and reports entry progress.

## Validation without GitHub Actions

For the pure standard-library property models, run:

```powershell
pwsh -File tools/test-copy-executor-local.ps1
```

That gate needs Python but no .NET SDK. It includes the executor state/lease model, the Windows namespace/identity model and `verify_copy_basic_metadata.py`. The metadata verifier fuzzes the preserved-attribute mask, rejects unsupported storage-state flags, requires handle-only metadata APIs, and guards the ordering `capture -> copy -> data flush -> metadata apply -> metadata flush -> destination identity validation`.

For the focused real Windows compiler/native gate, run on Windows with .NET 10:

```powershell
pwsh -File tools/test-windows-copy-local.ps1
```

That script first runs the zero-Actions property models, then builds `FileOp.Core` and `FileOp.Windows` in Release and runs only `FileOperationActionHistoryTests`, `FileCopyOperationExecutorTests` and classes whose names contain `WindowsFileCopyMutationPrimitiveTests`. Use `-SkipOfflineModels` when the Python gate has already been run. Neither script invokes GitHub Actions.

Real Windows regression tests cover content copying, exclusive collision refusal, source-file replacement, source-root replacement, destination-root replacement, invalid root identity, lease-held destination deletion, lease-held parent-directory rename blocking, basic timestamp/attribute round-tripping, unsupported attribute filtering, and metadata preservation through the concrete mutation primitive.

## Next boundary

Run the focused Windows compiler/native gate successfully before exposing Run/Execute in the Files UI. Further fidelity work should treat ACLs, alternate streams, extended attributes and special storage states as explicit separately reviewed semantics rather than assuming Explorer-style equivalence. Directory Copy, Move and Undo remain separate later slices.
