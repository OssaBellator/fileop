# Read-only recovery main-stream verification

## Purpose

Durable Copy history can carry the SHA-256 of the primary file data stream FileOp successfully wrote, the created destination `FileIdentity`, and—on new histories—the validated destination-root `FileIdentity`. `FileOperationRecoveryInspector` first observes root and leaf namespace evidence. `FileOperationRecoveryContentVerifier` then requires a second, stronger read boundary that reopens and holds the recorded destination root while reading the leaf.

A successful result is named **`MatchesRecordedMainStream`**, not "unchanged file". It is evidence only and is **not deletion authorization**.

## Evidence gate

The root-bound content reader runs only when all three prerequisites are true:

```text
durable post-Copy SHA-256 exists
        AND
destination root inspection == SameObject
        AND
destination file inspection == SameObject
        ↓
FileContentFingerprintReadRequest
  recorded root canonical path + FileIdentity
  recorded leaf canonical path + FileIdentity
        ↓
WindowsRootBoundFileContentFingerprintReader
        ↓ CreateFileW recorded root, metadata/traverse access
        ↓ FileShare.ReadWrite (delete sharing omitted)
        ↓ FILE_FLAG_OPEN_REPARSE_POINT
        ↓ verify root path / directory / non-reparse / FileIdentity
        ↓ NtCreateFile leaf relative to root handle (RootDirectory)
        ↓ FileShare.Read; FILE_OPEN_REPARSE_POINT; non-directory
        ↓ verify leaf path / ordinary-file / non-reparse / FileIdentity
        ↓ SHA-256 main data stream while both handles remain alive
        ↓ recheck root path/type/identity
        ↓ recheck leaf path/identity/size/last-write
        ↓
MatchesRecordedMainStream or a fail-closed status
```

Legacy histories without durable destination-root identity remain readable but return `DestinationRootNotVerified`; they are not silently upgraded from current observations and the content reader is not invoked.

The earlier root observation is still point-in-time evidence, but it is no longer trusted as a namespace lock for hashing. The root-bound reader independently reopens the recorded canonical root, verifies its durable `FileIdentity`, and keeps that directory handle alive through the leaf read. If the root changes after inspection but before the read boundary, the result is `DestinationRootChanged` rather than a content match.

Core also revalidates a reader-reported `Success`: both returned objects must match the requested root/leaf canonical paths, types, non-reparse state and stable identities, and the digest must be SHA-256. A pluggable reader's success status is not authority by itself.

## Windows root-bound read boundary

`WindowsRootBoundFileContentFingerprintReader` opens the recorded destination directory first. It requests only traverse/read-attributes/synchronize access and uses `FileShare.ReadWrite`, deliberately omitting delete sharing. Under normal Windows share semantics, a conflicting delete/rename binding prevents this stable namespace open rather than allowing FileOp to observe a moving root.

After verifying the directory handle's final canonical path, ordinary-directory type, non-reparse state and exact `FileIdentity`, the reader derives the one-component leaf name from the recorded canonical leaf path. It then uses `NtCreateFile` with `OBJECT_ATTRIBUTES.RootDirectory` set to the verified directory handle. There is **no full-path leaf `CreateFileW` fallback** in the root-bound reader.

The leaf open requests only read-data/read-attributes/synchronize access, uses `FileShare.Read`, sets `FILE_OPEN_REPARSE_POINT`, requires a non-directory file, and remains alive together with the root handle for the entire SHA-256 read and post-read validation.

A sharing violation becomes `Busy`. This can result from a writer/delete handle or an otherwise restrictive sharing mode; `Busy` therefore means only that the required stable proof cannot be obtained, not that another process is necessarily writing.

This is a read-access boundary. FileOp makes **no explicit target-file write/delete access** request and calls no target-file write, delete, move or replacement API in this verifier. Filesystem-managed read behavior can still include a **last-access** update, cache/recall work or cloud hydration, so later no-user-change policy must not naively interpret those effects as external user modification.

## Result states

The Core verifier exposes conservative states including:

- `NoRecordedFingerprint` — no durable post-Copy SHA-256;
- `DestinationRootNotVerified` — the earlier durable/current root observation was insufficient, so no content read was attempted;
- `DestinationRootChanged` — the root-bound read boundary could not re-establish or retain the recorded parent namespace object;
- `NotSameRecordedObject` — the earlier leaf inspection did not prove the recorded file object;
- `MatchesRecordedMainStream` — the current primary-stream SHA-256 matches the durable post-Copy value under the root-bound read protocol;
- `DifferentMainStream` — the same identity-bound leaf under the verified root has different primary-stream bytes;
- `Missing`, `DifferentObject`, `Redirected`, `ReparsePoint`, `UnexpectedType`, `Busy`, `Inaccessible`, `Error` — fail-closed root/leaf reader outcomes.

No status exposes `CanDelete`, `CanUndo` or mutation authority.

## Compatibility boundary

The older `IFileContentFingerprintReader` / `WindowsFileContentFingerprintReader` leaf-only contract remains source-compatible for its historical focused tests, but `FileOperationRecoveryContentVerifier` no longer accepts it. Recovery verification depends on `IRootBoundFileContentFingerprintReader`, preventing a path-only implementation from silently satisfying the stronger recovery proof.

## What this does not prove

Even with root identity continuously held during the hash, leaf identity and matching SHA-256, this is not a complete no-user-change proof. It does not prove equality of:

- timestamps or other basic **metadata**;
- ACL/security descriptors;
- **alternate data streams**;
- extended attributes;
- compression, encryption, sparse or integrity state;
- **hard-link count** or the absence/presence of other names for the same file object;
- namespace continuity after the read handles are released and before some future destructive action;
- other filesystem state not represented by primary-stream SHA-256.

A matching digest also cannot show whether bytes were temporarily changed and later restored. It is **not a historical audit log**; it establishes equality of the observed main stream with the recorded post-Copy bytes at verification time.

The future destructive boundary must perform its own final root/leaf revalidation under handles that remain alive through the actual relative mutation, define which non-main-stream changes invalidate recovery, and require explicit user authorization. This read-only proof must not be reused as a later deletion lease.

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
