# Read-only recovery main-stream verification

## Purpose

Durable Copy history can carry the SHA-256 of the primary file data stream that FileOp successfully wrote. Metadata-only recovery inspection can separately establish that a recovery-sensitive destination still resolves to the recorded canonical location and stable `FileIdentity`.

`FileOperationRecoveryContentVerifier` combines those two pieces conservatively. It never reads file content unless the earlier `FileOperationRecoveryInspector` result is already `SameObject` and the history entry contains both recorded destination identity and post-Copy SHA-256 evidence.

A successful result is named **`MatchesRecordedMainStream`**, not "unchanged file". It is evidence only and is **not deletion authorization**.

## Two-stage proof

The verification sequence is deliberately split:

```text
durable RecoveryRequired entry
  + recorded destination FileIdentity
  + recorded post-Copy SHA-256
        ↓
metadata-only FileOperationRecoveryInspector
        ↓ must be SameObject
WindowsFileContentFingerprintReader
        ↓ stable read handle, FileShare.Read only
        ↓ FILE_FLAG_OPEN_REPARSE_POINT
        ↓ final canonical path check
        ↓ ordinary-file / non-reparse check
        ↓ exact FileIdentity check
        ↓ SHA-256 primary stream through same handle
        ↓ post-read identity / size / last-write / path check
FileOperationRecoveryContentVerifier
        ↓
MatchesRecordedMainStream or a fail-closed status
```

The second stage re-proves namespace and identity under the actual content-read handle. The earlier metadata inspection therefore cannot become a TOCTOU authorization shortcut. Core also revalidates the shape of a reader-reported `Success` result instead of treating a pluggable reader's status as authority by itself.

## Windows stable-read boundary

`WindowsFileContentFingerprintReader` requests only file read-data, read-attributes and synchronize access. It opens with `FileShare.Read` and intentionally omits write and delete sharing for the lifetime of validation and hashing. FileOp does not request target-file write/delete access and calls no target-file write, delete, move or replacement API in this verifier.

A sharing violation is reported as `Busy` rather than weakened into a proof. This can be caused by an existing writer/delete handle **or** by an otherwise read-only handle whose sharing mode does not permit the verifier's read access. `Busy` therefore means only that existing sharing constraints are incompatible with the stable read proof; it is not evidence that another process is writing.

The final component is opened with `FILE_FLAG_OPEN_REPARSE_POINT`. Before hashing, FileOp verifies:

- final canonical path still equals the recorded canonical destination;
- the handle is an ordinary file, not a directory;
- the leaf is not a reparse point;
- the handle's `FileIdentity` exactly equals durable recovery history.

The SHA-256 is then computed from the primary file data stream through that same handle. After the read completes, FileOp checks the same handle again and rejects the proof if identity, size, last-write timestamp or final path changed during the read.

This is a **read-access** boundary, not a promise that the underlying filesystem will leave every metadata field untouched. Depending on filesystem, mount and provider behavior, reading may itself cause filesystem-managed effects such as a last-access update, cache/recall activity or cloud-file hydration. A later no-user-change policy therefore must not naively treat a post-verification last-access difference as proof of an external user change.

## Result states

The Core verifier exposes conservative states:

- `NoRecordedFingerprint` — legacy or weaker durable history has no post-Copy SHA-256;
- `NotSameRecordedObject` — metadata-only recovery inspection did not first prove `SameObject`;
- `MatchesRecordedMainStream` — stable-handle SHA-256 equals the durable post-Copy primary-stream SHA-256;
- `DifferentMainStream` — the same identity-bound object produced a different primary-stream SHA-256;
- `Missing` — the destination disappeared before stable content verification;
- `DifferentObject` — the stable read handle has a different `FileIdentity`;
- `Redirected` — the read handle resolves to a different canonical location;
- `ReparsePoint` — the final component is now a reparse point;
- `UnexpectedType` — the recorded file destination is now a directory/non-file object;
- `Busy` — existing sharing constraints prevent a stable read proof;
- `Inaccessible` — read access is denied;
- `Error` — content verification could not complete reliably.

Only `MatchesRecordedMainStream` means the current primary stream matched the durable post-Copy bytes at verification time. No status exposes `CanDelete`, `CanUndo` or mutation authority.

## What this does not prove

This slice proves only the **main data stream** under the stable read-handle protocol above. It does not prove equality of:

- timestamps or other basic metadata;
- ACL/security descriptors;
- alternate data streams;
- extended attributes;
- compression, encryption, sparse or integrity state;
- hard-link count or the absence/presence of other names for the same file object;
- the identity of the destination's parent directory at verification time;
- other filesystem state not represented by the primary data stream SHA-256.

A matching stream also does not prove that no temporary modification occurred and was later restored to the same bytes. SHA-256 establishes equality of the observed primary-stream content with the recorded post-Copy content at verification time; it is not a historical audit log.

It also does not attempt to turn a matching stream into destructive recovery policy. A later slice must explicitly define which non-main-stream changes matter, how parent/namespace evidence participates, how a final race-safe authorization boundary works, and how the user approves any destructive action.

## Validation without hosted Actions

Run the dedicated standard-library model/source guard:

```powershell
python tools/verify_recovery_main_stream.py --repo-root . --cases 50000
```

The normal Copy wrappers run it automatically:

```powershell
python tools/test-copy-executor-local.py --repo-root .
pwsh -File tools/test-copy-executor-local.ps1
```

The Windows/.NET compiler/native tests are included in the existing batch gate and can be run later together with the stacked fingerprint work:

```bat
tools\test-windows-copy-local.cmd
```

No hosted GitHub Actions run is required.

## Safety boundary

This implementation contains no explicit target-file write/delete access and no target-file write, delete, move, replacement or recovery mutation API. The content read itself may still have filesystem-managed read side effects as described above. `MatchesRecordedMainStream` is recovery evidence only. Directory Copy, Move, actual Undo, destructive recovery and Files UI execution/Undo wiring remain out of scope.
