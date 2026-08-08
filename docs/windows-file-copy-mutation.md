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
6. disables automatic last-access/last-write timestamp updates for subsequent I/O on that destination handle;
7. copies bytes through the bound handles and flushes destination data;
8. reads the destination's current basic attributes, merges the destination-owned settable subset with the supported source attributes, applies timestamps/attributes through `SetFileInformationByHandle(FileBasicInfo)`, then requires another `FlushFileBuffers`;
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

Windows can defer automatic access/write timestamp changes until later I/O or handle close. Immediately after creating the destination handle, FileOp therefore sets its `LastAccessTime` and `LastWriteTime` fields to `-1` through `FileBasicInfo`. Windows defines that value as suppressing automatic updates for subsequent operations on the same handle. FileOp never sends `-2`, which is the value that re-enables those automatic updates, so the explicit source timestamps applied after the byte copy remain stable through the final flush and lease disposal.

That suppression is handle-local. The mutation handle intentionally permits read sharing, so a separate process that reads the destination can legitimately advance its last-access time; the fidelity guarantee covers FileOp's own mutation I/O rather than concurrent external access.

A zero timestamp has special Windows basic-information semantics: it means “do not change this field.” Therefore a source filesystem that reports a timestamp as zero/unsupported cannot have that zero value reproduced literally through `FileBasicInfo`; FileOp leaves the corresponding destination timestamp filesystem-owned in that case. Explicit nonzero source timestamps are applied.

`ChangeTime` is not copied. Source storage-state attributes that need separate filesystem semantics are also not copied. For the ordinary attribute bits FileOp manages here, read-only, hidden, system, archive, temporary, offline and not-content-indexed are the meaningful settable states. `FILE_ATTRIBUTE_NORMAL` is different: when used alone it explicitly clears the other attribute flags, while a `FileAttributes` value of `0` means “do not change attributes.” FileOp therefore overlays its supported source fidelity bits while retaining the destination's current `Temporary` and `Offline` values, then emits `Normal` only when that merged set is otherwise empty. Sparse, compressed, encrypted and integrity-stream state are not copied through this basic-attribute merge; FileOp deliberately omits those flags from the input and Windows leaves their existing storage semantics untouched. Reparse-point state is excluded by the ordinary-file creation/validation boundary.

Windows can also apply destination-owned defaults at creation—for example, files created in compressed or encrypted directories can inherit those states from the destination directory—and this slice deliberately does not clear or normalize them. ACLs, alternate data streams and extended attributes remain separate future boundaries rather than being implied by a byte copy.

## Durability and failure semantics

Destination creation requests `FILE_WRITE_THROUGH`. Automatic destination access/write timestamp changes are suppressed before the first data write. After all bytes are written, `FlushFileBuffers` must succeed before metadata is changed. The captured basic metadata is then applied through the destination handle and a second, final `FlushFileBuffers` must succeed before a lease is returned. The destination handle requests generic write for data/attribute mutation **and** explicit `FILE_READ_ATTRIBUTES` for the pre-apply destination metadata query; generic write alone does not include read-attribute access.

The executor has already persisted `MutationStarted` before invoking the primitive, so any exception after destination creation is conservatively settled as recovery-sensitive. The primitive does not guess whether a partial destination should be deleted.

A successful primitive return does not by itself mean the operation is committed. The destination handle remains locked in the lease until the executor durably records `CommitCopy(destination identity)` and reports entry progress.

## Validation without GitHub Actions

For the full cross-platform offline gate using Python only, run:

```text
python tools/test-copy-executor-local.py --repo-root .
```

That entry point requires neither PowerShell nor the .NET SDK. It runs the existing offline FileOp verifiers plus the Copy executor, Windows handle-binding model, basic-metadata model and basic-metadata interop/ABI source guard in one process-independent sequence.

The existing PowerShell wrapper remains available and runs the same Copy-focused verification on top of the established offline gate:

```powershell
pwsh -File tools/test-copy-executor-local.ps1
```

The offline gate includes the executor state/lease model, the Windows namespace/identity model, `verify_copy_basic_metadata.py` and `verify_copy_basic_metadata_abi.py`. The metadata verifier fuzzes both the preserved source-attribute mask and destination-owned settable-attribute merge, rejects unsupported source storage-state flags, requires non-settable storage flags to stay out of the `FileBasicInfo` input, requires explicit destination read-attribute access, requires handle-only metadata APIs, requires `-1` timestamp suppression without `-2` re-enable, guards the ordering `capture -> destination create/read access -> suppress automatic timestamps -> copy -> data flush -> metadata apply -> metadata flush -> destination identity validation`, and requires the concrete regression to exercise every advertised safe attribute with its expected metadata established after validation.

The ABI verifier independently models the unmanaged layout, guards the C# field order, `FileBasicInfo = 0`, `DllImport`/`BOOL` marshalling signatures, timestamp sentinel buffers and inclusion of the .NET reflection regression in the Windows gate. If Clang is available, the same verifier can additionally compile static ABI assertions for both x64 and x86 Windows COFF targets:

```text
python tools/verify_copy_basic_metadata_abi.py --repo-root . --clang
```

A separate package-free probe closes more of the gap on any Linux, macOS or Windows machine that already has a .NET 10 SDK. It copies the actual `WindowsFileCopyBasicMetadata.cs` into a temporary `net10.0` project with no package references, compiles it, and uses the .NET runtime marshaller/reflection APIs to verify structure sizes/offsets, the `FileBasicInfo` enum value and P/Invoke signatures without invoking `kernel32`:

```text
python tools/verify_copy_basic_metadata_dotnet.py --repo-root .
```

The focused Windows gate runs that package-free compiler/interop probe first, then performs the real Windows build and native regressions. Run it on Windows with Python and the .NET 10 SDK. PowerShell 7 remains supported:

```powershell
pwsh -File tools/test-windows-copy-local.ps1
```

PowerShell 7 is not required. From a stock Windows 10/11 Command Prompt, use the checked-in Windows PowerShell launcher:

```bat
tools\test-windows-copy-local.cmd
```

The `.cmd` launcher runs `powershell.exe` with a process-local execution-policy override and delegates to the same `.ps1` gate. The gate first runs the zero-Actions property/source models unless `-SkipOfflineModels` is supplied, runs the package-free .NET interop probe, builds `FileOp.Core` and `FileOp.Windows` in Release, and runs the focused action-history, Copy-executor, mutation-primitive, metadata and interop-reflection test classes. None of these commands invoke GitHub Actions.

Real Windows regression tests cover content copying, exclusive collision refusal, source-file replacement, source-root replacement, destination-root replacement, invalid root identity, lease-held destination deletion, lease-held parent-directory rename blocking, basic timestamp/attribute round-tripping, unsupported source-attribute filtering, destination-owned settable-attribute merging, destination `Temporary` preservation through the real Win32 helper, omission of non-settable storage flags from `FileBasicInfo`, .NET/native interop layout/signature checks, and metadata preservation through the concrete mutation primitive after the mutation lease is disposed.

## Next boundary

Run the focused Windows compiler/native gate successfully before exposing Run/Execute in the Files UI. Further fidelity work should treat ACLs, alternate streams, extended attributes and special storage states as explicit separately reviewed semantics rather than assuming Explorer-style equivalence. Directory Copy, Move and Undo remain separate later slices.