# Read-only recovery main-stream verification

## Purpose

Durable Copy history can carry the SHA-256 of the primary file data stream FileOp successfully wrote, the created destination `FileIdentity`, and—on new histories—the validated destination-root `FileIdentity`. `FileOperationRecoveryInspector` observes root and leaf namespace evidence before `FileOperationRecoveryContentVerifier` considers reading bytes.

A successful result is named **`MatchesRecordedMainStream`**, not "unchanged file". It is evidence only and is **not deletion authorization**.

## Evidence gate

The content reader runs only when all three prerequisites are true:

```text
durable post-Copy SHA-256 exists
        AND
destination root inspection == SameObject
        AND
destination file inspection == SameObject
        ↓
WindowsFileContentFingerprintReader
        ↓ stable read handle, FileShare.Read only
        ↓ FILE_FLAG_OPEN_REPARSE_POINT
        ↓ final canonical path / ordinary-file / non-reparse / FileIdentity checks
        ↓ SHA-256 main data stream through that same handle
        ↓ post-read identity / size / last-write / path checks
        ↓
MatchesRecordedMainStream or a fail-closed status
```

Legacy histories without durable destination-root identity are readable but return `DestinationRootNotVerified`; they are not silently upgraded from current observations and the content reader is not invoked. The root observation is point-in-time evidence, not a held parent-directory lock, so it still cannot serve as a final destructive authorization boundary.

Core also revalidates a reader-reported `Success` result: the returned object must still be an ordinary non-reparse file at the recorded canonical path, with the expected identity and a SHA-256 fingerprint. A pluggable reader's status is not authority by itself.

## Windows stable-read boundary

`WindowsFileContentFingerprintReader` requests only read-data, read-attributes and synchronize access. It opens with `FileShare.Read` and omits write and delete sharing for the lifetime of validation and hashing. FileOp makes **no explicit target-file write/delete access** request and calls no target-file write, delete, move or replacement API in this verifier.

A sharing violation becomes `Busy`. It can be caused by an existing writer/delete handle or by an otherwise read-only handle whose sharing mode rejects the verifier's read. `Busy` therefore means only that existing sharing constraints are incompatible with the stable proof; it is not evidence that another process is writing.

The final component is opened with `FILE_FLAG_OPEN_REPARSE_POINT`. Before hashing, the reader verifies final path, ordinary-file type, non-reparse state and exact `FileIdentity`. It hashes the **main data stream** through that handle, then rechecks identity, size, last-write timestamp and final path.

This is a read-access boundary, not a promise that every metadata field remains untouched. Filesystem-managed behavior may include a **last-access** update, cache/recall work or cloud hydration. A later no-user-change policy must not naively interpret those read effects as an external user modification.

## Result states

The Core verifier exposes conservative states including:

- `NoRecordedFingerprint` — no durable post-Copy SHA-256;
- `DestinationRootNotVerified` — durable/current parent namespace evidence is insufficient or mismatched;
- `NotSameRecordedObject` — the leaf inspection did not prove the recorded file object;
- `MatchesRecordedMainStream` — current primary-stream SHA-256 matches the durable post-Copy value;
- `DifferentMainStream` — the same identity-bound object has different primary-stream bytes;
- `Missing`, `DifferentObject`, `Redirected`, `ReparsePoint`, `UnexpectedType`, `Busy`, `Inaccessible`, `Error` — fail-closed reader outcomes.

No status exposes `CanDelete`, `CanUndo` or mutation authority.

## What this does not prove

Even with destination-root identity, leaf identity and matching SHA-256, this is not a complete no-user-change proof. It does not prove equality of:

- timestamps or other basic **metadata**;
- ACL/security descriptors;
- **alternate data streams**;
- extended attributes;
- compression, encryption, sparse or integrity state;
- **hard-link count** or the absence/presence of other names for the same file object;
- continuous identity of the **parent directory** between the point-in-time root inspection and a future action;
- other filesystem state not represented by primary-stream SHA-256.

A matching digest also cannot show whether bytes were temporarily changed and later restored. It is **not a historical audit log**; it establishes equality of the observed main stream with the recorded post-Copy bytes at verification time.

The future destructive boundary must reopen/revalidate the destination parent and leaf under handles that remain alive through the actual relative operation, define which non-main-stream changes invalidate recovery, and require explicit user authorization.

## Validation without hosted Actions

```powershell
python tools/verify_recovery_main_stream.py --repo-root . --cases 50000
python tools/verify_recovery_root_identity.py --repo-root . --cases 50000
python tools/test-copy-executor-local.py --repo-root .
```

Windows/.NET compiler/native tests remain in the shared deferred batch:

```bat
tools\test-windows-copy-local.cmd
```

## Safety boundary

This implementation has no explicit target-file write/delete access and no target-file write, delete, move, replacement or recovery mutation API. Reads can still have filesystem-managed side effects as described above. Root/leaf identity and `MatchesRecordedMainStream` remain recovery evidence only. Directory Copy, Move, actual Undo, destructive recovery and Files UI execution/Undo wiring remain out of scope.