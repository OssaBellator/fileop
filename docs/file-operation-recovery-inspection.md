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

`SameObject` is **identity evidence only**: it proves canonical location + stable object identity at inspection time. The durable `DestinationContentFingerprint` records FileOp's post-Copy main-stream SHA-256, but this inspector deliberately does **not** compare that value with current file bytes.

`FileOperationRecoveryContentVerifier` is the separate second read-only stage. It consumes this inspector's result and proceeds only for `SameObject` entries that also have durable SHA-256 evidence. On Windows, `WindowsFileContentFingerprintReader` then re-proves canonical path, ordinary-file type, non-reparse state and exact `FileIdentity` under a stable read handle before hashing the current main data stream.

A `MatchesRecordedMainStream` result means only that the same identity-bound object's primary data stream matched the durable post-Copy SHA-256 at verification time. It does not automatically settle metadata, ACL, alternate-data-stream, extended-attribute, compression/encryption/sparse or other non-main-stream policy.

## Safety boundary

The inspector and content verifier expose no `CanDelete`, `CanUndo`, delete candidate, replacement, or mutation authority. A `RecoveryRequired` entry remains `UndoKind.None` even when namespace identity and main-stream SHA-256 both match.

The canonical resolver itself still introduces no content-read access: it opens existing paths with metadata-only desired access `0`, then uses `GetFinalPathNameByHandleW` and `GetFileInformationByHandle` to resolve canonical location and stable identity. Missing leaves are resolved through their existing canonical parent without creating the leaf.

The later stable content-read stage is documented separately in `file-operation-recovery-content-verification.md` and remains read-only.

## Validation without hosted Actions

Run the recovery-inspection model/source guard:

```powershell
python tools/verify_file_operation_recovery_inspection.py --repo-root . --cases 50000
```

Run the fingerprint evidence model/source guard:

```powershell
python tools/verify_copy_content_fingerprint.py --repo-root . --cases 20000
```

Run the stable main-stream verification model/source guard:

```powershell
python tools/verify_recovery_main_stream.py --repo-root . --cases 50000
```

Run the complete Copy offline gate:

```powershell
python tools/test-copy-executor-local.py --repo-root .
```

On Windows with Python and .NET 10, the compiler/native checks can be batched later through:

```bat
tools\test-windows-copy-local.cmd
```

None of these gates require GitHub Actions.

## Still out of scope

- complete no-user-change policy across non-main-stream state;
- actual Undo or deletion;
- destructive recovery actions;
- directory Copy recovery;
- Move recovery;
- Files UI recovery/Undo wiring.
