# Read-only Copy recovery destination inspection

## Purpose

Durable Copy recovery history can contain three distinct evidence classes: the created destination file identity, its post-Copy SHA-256, and—on new histories—the validated source/destination root identities captured when action history began. These values are **recovery evidence**, **not deletion authorization**.

`FileOperationRecoveryInspector` is the metadata-only namespace/object stage. It uses `IFileOperationCanonicalPathResolver`; on Windows the resolver opens paths with desired access `0` and does not hash or request target-file write access.

## Destination-root observation

New histories persist the destination-root `FileIdentity`. The inspector exposes a separate `DestinationDirectory` observation:

- `NoVerifiedIdentity` — legacy history has no durable root identity;
- `Missing`;
- `SameObject` — canonical directory location and stable identity match;
- `DifferentObject`;
- `Redirected`;
- `ReparsePoint`;
- `UnexpectedType` — the recorded directory now resolves to a file;
- `Inaccessible`;
- `Error`.

Legacy history is intentionally not upgraded from a current directory observation: when durable root identity is absent, the inspector returns `NoVerifiedIdentity` and does not resolve the root for proof. Leaf diagnostics may still run.

This allows FileOp to represent a subtle namespace change: a replacement directory can contain the **same file object** moved back to the same textual path. In that state the root is `DifferentObject` while the leaf may still be `SameObject`.

## Recovery entries and leaf classifications

Only `MutationStarted` and `RecoveryRequired` entries are leaf-inspected. Leaf statuses remain:

- `NoVerifiedIdentity`;
- `Missing`;
- `SameObject`;
- `DifferentObject`;
- `Redirected`;
- `ReparsePoint`;
- `UnexpectedType`;
- `Inaccessible`;
- `Error`.

`SameObject` is **identity evidence** at inspection time: the regular non-reparse leaf resolves to the recorded canonical location and stable `FileIdentity`. `Redirected` and `ReparsePoint` remain conservative even if an identity happens to match.

## Relationship to SHA-256 evidence

The inspector itself does not read content. `FileOperationRecoveryContentVerifier` proceeds only when durable SHA-256 exists **and** both destination-root and leaf observations are `SameObject`. A missing/changed root yields `DestinationRootNotVerified` and no content reader call.

On Windows, the later `WindowsFileContentFingerprintReader` independently re-proves leaf path/type/reparse/identity under its actual stable read handle and compares the main-stream SHA-256. A matching stream is still **not sufficient** to establish a complete no-user-change proof: metadata, ACLs, alternate data streams, EAs, hard-link topology and other state remain outside the comparison.

The root observation is also point-in-time; its handle is not retained through the later read. A final destructive authorization protocol must reopen the parent and leaf and hold the binding handles through the actual relative mutation.

## Safety boundary

The inspector exposes no `CanDelete`, `CanUndo`, delete candidate, replacement or mutation authority. `RecoveryRequired` keeps `UndoKind.None` even if root identity, leaf identity and primary-stream SHA-256 all match.

The canonical resolver remains metadata-only. Root/leaf observations are read-only evidence, not namespace locks or recovery authorization.

## Validation without hosted Actions

```powershell
python tools/verify_file_operation_recovery_inspection.py --repo-root . --cases 50000
python tools/verify_recovery_root_identity.py --repo-root . --cases 50000
python tools/verify_recovery_main_stream.py --repo-root . --cases 50000
python tools/test-copy-executor-local.py --repo-root .
```

The Windows/.NET compiler/native checks can be batched later through:

```bat
tools\test-windows-copy-local.cmd
```

None of these gates require GitHub Actions.

## Still out of scope

- complete **no-user-change** policy across non-main-stream state;
- final handle-bound destructive authorization;
- actual Undo or deletion;
- destructive recovery actions;
- directory Copy recovery;
- Move recovery;
- Files UI recovery/Undo wiring.