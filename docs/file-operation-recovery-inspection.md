# Read-only Copy recovery destination inspection

## Purpose

Durable Copy history can retain the exact destination `FileIdentity` when a mutation receipt was validated but `CommitCopyAsync` failed. New Copy receipts also carry a SHA-256 fingerprint of the logical bytes FileOp wrote, and the validated-receipt/failed-commit path persists the identity + fingerprint pair together.

Those values are recovery evidence, **not deletion authorization**. `FileOperationRecoveryInspector` remains the conservative namespace/object boundary: it consumes `FileOperationActionHistory` plus `IFileOperationCanonicalPathResolver`; on Windows, `WindowsFileOperationCanonicalPathResolver` performs metadata-only handle resolution with desired access `0`.

The inspector does not hash, delete, replace, move, open for write, or otherwise mutate a queued destination.

## Entries inspected

Only entries in these durable states are inspected:

- `MutationStarted` — mutation crossed the durable barrier, but no verified destination identity/fingerprint evidence is trusted;
- `RecoveryRequired` — mutation is explicitly recovery-sensitive and may contain paired destination identity + SHA-256 evidence from the validated-receipt / failed-commit path.

Committed, skipped, pending and pre-mutation failed entries are ignored by this recovery inspector.

## Destination classifications

Each inspected entry receives one read-only observation:

- `NoVerifiedIdentity` — a file exists at the recorded canonical location, but durable history and current resolution do not provide enough trusted identity evidence to correlate it;
- `Missing` — the destination path is currently missing;
- `SameObject` — the destination is a regular file, is not a reparse leaf, resolves to the recorded canonical location, and its current stable `FileIdentity` equals the identity stored in recovery history;
- `DifferentObject` — the recorded canonical path exists as a file but has a different stable identity;
- `Redirected` — the requested destination now resolves to a different canonical location;
- `ReparsePoint` — the destination leaf is currently a reparse point;
- `UnexpectedType` — the recorded file destination currently resolves to a directory;
- `Inaccessible` — metadata inspection was denied;
- `Error` — the resolver could not inspect the destination reliably.

`ReparsePoint` and `Redirected` are deliberately conservative even if an identity happens to match. A changed namespace path is not collapsed into a same-object result.

## Relationship to content fingerprint evidence

`SameObject` proves only canonical location + stable object identity at inspection time. The durable `DestinationContentFingerprint` records FileOp's post-Copy main-stream SHA-256, but this inspector deliberately does **not** compare that value with current file bytes.

The next read-only boundary should open the already-inspected object through a race-resistant identity-bound read handle, hash the current main data stream, and compare it with durable fingerprint evidence. Until that exists, FileOp cannot claim a complete no-user-change proof.

Even a future content match will not automatically settle metadata, ACL, alternate-data-stream, extended-attribute, compression/encryption/sparse or other non-main-stream policy. Those concerns and explicit user authorization remain prerequisites for destructive recovery.

## Safety boundary

The inspector exposes no `CanDelete`, `CanUndo`, delete candidate, replacement, or mutation authority. A `RecoveryRequired` entry remains `UndoKind.None` even when the inspector observes `SameObject` and durable SHA-256 evidence exists.

No new native filesystem API is introduced here. The existing Windows canonical resolver opens existing paths with metadata-only desired access `0`, then uses `GetFinalPathNameByHandleW` and `GetFileInformationByHandle` to resolve canonical location and stable identity. Missing leaves are resolved through their existing canonical parent without creating the leaf.

## Validation without hosted Actions

Run the recovery-inspection model/source guard:

```powershell
python tools/verify_file_operation_recovery_inspection.py --repo-root . --cases 50000
```

Run the fingerprint evidence model/source guard:

```powershell
python tools/verify_copy_content_fingerprint.py --repo-root . --cases 20000
```

Run the complete Copy offline gate:

```powershell
python tools/test-copy-executor-local.py --repo-root .
```

On Windows with Python and .NET 10:

```bat
tools\test-windows-copy-local.cmd
```

None of these gates require GitHub Actions.

## Still out of scope

- current-file fingerprint verification;
- complete no-user-change proof;
- actual Undo or deletion;
- destructive recovery actions;
- directory Copy recovery;
- Move recovery;
- Files UI recovery/Undo wiring.
