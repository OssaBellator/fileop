# Read-only Copy recovery destination inspection

## Purpose

Durable Copy history can now retain the exact destination `FileIdentity` when a mutation receipt was validated but `CommitCopyAsync` failed. That identity is useful recovery evidence, but it is **not deletion authorization** and does not establish that the file has remained unchanged since Copy.

`FileOperationRecoveryInspector` adds the next conservative boundary: read-only inspection of recovery-sensitive Copy-file destinations. It consumes `FileOperationActionHistory` and the existing `IFileOperationCanonicalPathResolver`; on Windows, `WindowsFileOperationCanonicalPathResolver` performs metadata-only handle resolution with desired access `0`.

This slice does not delete, replace, move, open for write, or otherwise mutate a queued destination.

## Entries inspected

Only entries in these durable states are inspected:

- `MutationStarted` — mutation crossed the durable barrier, but no verified destination identity is available;
- `RecoveryRequired` — mutation is explicitly recovery-sensitive and may contain a verified destination identity from the validated-receipt / failed-commit path.

Committed, skipped, pending and pre-mutation failed entries are ignored by the recovery inspector.

## Destination classifications

Each inspected entry receives one read-only observation:

- `NoVerifiedIdentity` — a file exists at the recorded canonical location, but durable history and current resolution do not provide enough identity evidence to correlate it;
- `Missing` — the destination path is currently missing;
- `SameObject` — the destination is a regular file, is not a reparse leaf, resolves to the recorded canonical location, and its current stable `FileIdentity` equals the identity stored in recovery history;
- `DifferentObject` — the recorded canonical path exists as a file but has a different stable identity;
- `Redirected` — the requested destination now resolves to a different canonical location;
- `ReparsePoint` — the destination leaf is currently a reparse point;
- `UnexpectedType` — the recorded file destination currently resolves to a directory;
- `Inaccessible` — metadata inspection was denied;
- `Error` — the resolver could not inspect the destination reliably.

`ReparsePoint` and `Redirected` are deliberately conservative even if an identity happens to match. A changed namespace path is not collapsed into a same-object recovery result.

## Safety boundary

`SameObject` means only that two read-only proofs agree at inspection time: canonical location and stable `FileIdentity`. It is **identity evidence only**.

It does not prove that the user or another application has not changed the file's contents or metadata since Copy. A future destructive recovery or Undo path therefore still needs an explicit **no-user-change** proof and user-facing authorization before it may delete or modify anything.

The inspector exposes no `CanDelete`, `CanUndo`, delete candidate, replacement, or mutation authority. A `RecoveryRequired` entry remains `UndoKind.None` even when the inspector observes `SameObject`.

## Windows resolver reuse

No new native filesystem API is introduced. The existing `WindowsFileOperationCanonicalPathResolver` is reused. It opens existing paths with metadata-only desired access `0`, then uses `GetFinalPathNameByHandleW` and `GetFileInformationByHandle` to resolve canonical location and stable identity. Missing leaves are resolved through their existing canonical parent without creating the leaf.

## Validation without hosted Actions

Run the dedicated model/source guard:

```powershell
python tools/verify_file_operation_recovery_inspection.py --repo-root . --cases 50000
```

Run the complete Copy offline gate:

```powershell
python tools/test-copy-executor-local.py --repo-root .
```

On Windows with Python and .NET 10, the focused compiler/native gate also runs `FileOperationRecoveryInspectionTests`:

```bat
tools\test-windows-copy-local.cmd
```

None of these gates require GitHub Actions.

## Still out of scope

- actual Undo or deletion;
- destructive recovery actions;
- no-user-change fingerprinting or proof;
- directory Copy recovery;
- Move recovery;
- Files UI recovery/Undo wiring.
