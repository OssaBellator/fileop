# Canonical file-operation execution validation

## Purpose

Queued plans and read-only preflight intentionally use captured/lexical paths. That is not enough to authorize mutation on Windows because a path that appears to remain under the captured source or destination can traverse a junction, symbolic link, mount point, or other reparse ancestor.

`IFileOperationExecutionValidator` is the final read-only safety boundary that a future executor must run immediately before mutation. This slice adds the validation contract and Windows implementation only. It does **not** add a concrete `IFileOperationExecutor`, a Run/Execute UI action, or any file/directory mutation API.

## Handle-resolved Windows paths

`WindowsFileOperationCanonicalPathResolver` opens an existing file or directory with `CreateFileW` using:

- desired access `0` (metadata/identity resolution only);
- `FileShare.ReadWrite | FileShare.Delete`;
- `OPEN_EXISTING`;
- `FILE_FLAG_BACKUP_SEMANTICS` so directory handles can be opened.

It then uses `GetFinalPathNameByHandleW` to obtain the final handle-resolved path and `GetFileInformationByHandle` to capture the volume serial + file index as a stable `FileIdentity` for that opened object.

The resolver converts `\\?\C:\...` and `\\?\UNC\...` results back to ordinary DOS/UNC presentation for comparison, while preserving volume-GUID/device-style extended paths so an absolute kernel path is never turned into a relative-looking string. The handle itself follows reparse ancestors, so the returned path represents the actual resolved location rather than only `Path.GetFullPath` lexical normalization.

For a destination leaf that does not yet exist, the resolver requires its parent directory to exist, resolves that parent through a handle, and combines the captured leaf name with the parent's canonical path. It does not create the missing leaf.

## Validation rules

Before any future mutation can begin, `WindowsFileOperationExecutionValidator` requires:

- source and destination roots to resolve to existing directories;
- captured roots that are themselves reparse points to remain blocked;
- source and destination roots to differ by both canonical path and stable identity;
- each captured source to remain a lexical direct child of the captured source root;
- the source leaf name/type to remain unchanged;
- alternate-data-stream names to remain blocked;
- a source leaf that is itself a reparse point to remain blocked;
- each source's **canonical parent** to equal the canonical source root;
- each destination's **canonical parent** to equal the canonical destination root;
- existing destination leaves that are themselves reparse points to remain blocked;
- directory destinations not to resolve to the source directory or a descendant;
- an existing destination not to have the same `FileIdentity` as the source, which catches hard-link aliases to the same file.

Collision policy is then re-evaluated at the canonical path:

```text
Destination missing       -> Ready
Destination exists + Ask  -> NeedsDecision
Destination exists + Skip -> Skip
Destination exists + Stop -> Blocked
```

A plan is `Ready` only when every non-skipped item is ready and no item still needs a decision.

## Safety and TOCTOU boundary

Validation is read-only. The implementation does not call `File.Copy`, `File.Move`, `File.Delete`, `Directory.Move`, `Directory.Delete`, file creation, or file-write APIs.

A successful validation result is still a point-in-time observation. Filesystem namespace state can change after the handles used for validation are closed. A future executor must therefore consume this boundary immediately before each mutation and either keep the validated object identity/handle relationship bound to the operation or repeat canonical validation at the entry-safe mutation boundary.

This contract is deliberately separate from queue preflight. Preflight remains a fast conservative metadata check and may reject root reparse points because it does not resolve canonical ancestry. Execution validation exists to close the ancestor-alias and hard-link identity gaps immediately before mutation.

## Validation without hosted Actions

Run the standard-library execution-validation model directly:

```powershell
python tools/verify_file_operation_execution_validation.py --repo-root . --cases 50000
```

The verifier models canonical-root aliasing, reparse leaves, source escape through an aliasing ancestor, canonical directory recursion, same-object hard-link destinations, collision policy and inaccessible/error targets. Repository mode also guards the handle-resolution APIs and asserts that no filesystem mutation API was added.

The full zero-Actions verifier suite remains:

```powershell
pwsh -File tools/test-local.ps1 -OfflineOnly
```

The normal Windows local gate additionally compiles `FileOp.Core` / `FileOp.Windows`, runs the MSTest regression suite, builds WinUI and checks the bundled helper without using GitHub Actions.

## Next boundary

Before actual Copy is enabled, FileOp still needs durable action-history/undo records with clear commit semantics for each completed entry. Move should remain later because cross-volume Move combines Copy success with source-deletion failure modes.
