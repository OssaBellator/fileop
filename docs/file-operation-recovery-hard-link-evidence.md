# Copy recovery hard-link count evidence

## Purpose

A destination file can acquire another hard-link name without changing its `FileIdentity`, canonical destination path or primary-stream bytes. Root/leaf identity plus SHA-256 therefore cannot describe every namespace-topology change relevant to later recovery policy.

This slice records and re-observes the destination object's **hard-link count** as separate recovery evidence. It does not decide whether any count is safe to delete.

## Durable evidence

New topology-aware Copy commits can persist a positive `DestinationHardLinkCount` together with destination identity and post-Copy SHA-256. The validated-receipt / failed-commit recovery path can persist the same evidence triple while keeping `UndoKind.None`.

The count lives in an additive schema-v1 side table. There is **no migration or schema-version increment**. Legacy histories remain readable with:

```text
DestinationHardLinkCount = null
```

Null means FileOp has no durable hard-link-count evidence for that entry; it is never treated as an observed count of one.

`SqliteFileOperationActionHistoryHardLinkEvidenceStore` implements the optional `IFileOperationActionHistoryHardLinkEvidenceStore` capability. Strong commit/recovery transitions update state/identity and insert SHA-256 + hard-link count in one SQLite transaction. A hard-link evidence insert failure rolls the state change and fingerprint insert back.

The established `IFileOperationActionHistoryStore` API remains intact for source compatibility. Legacy calls and legacy rows may remain count-less.

## Existing executor bridge

`FileCopyOperationExecutor` already keeps the mutation lease alive through `CommitCopyAsync` and, after a durable commit failure, through the immediate `MarkMutationRecoveryRequiredAsync` call. This slice uses that ordering instead of changing the executor or enlarging `FileCopyMutationReceipt`.

`WindowsFileOperationActionHistoryHardLinkEvidenceStore` wraps the stronger store. On the executor's existing `CommitCopyAsync` call it:

1. loads the active `MutationStarted` history;
2. asks `WindowsRootBoundFileHardLinkEvidenceSource` for a verified positive count;
3. caches that exact destination identity + SHA-256 + count observation in memory;
4. calls the atomic strong SQLite commit;
5. removes the cache after durable success.

If durable commit fails, the immediate recovery call can reuse the **same cached count** only when its destination identity and fingerprint match that commit attempt. If topology collection never succeeded or the evidence pair differs, recovery is persisted without destination identity, fingerprint or count rather than writing a partial proof.

A process crash loses the memory-only cache. The durable `MutationStarted` record then remains intentionally evidence-insufficient.

## Commit-bound count observation

`WindowsRootBoundFileHardLinkEvidenceSource` is metadata-only. It does not hash or read file data and requests no target write/delete access.

While the executor still owns the mutation lease, it:

- opens the recorded destination root and verifies canonical path, ordinary-directory type, non-reparse state and durable `FileIdentity`;
- opens the direct child destination leaf **relative to that root** with `NtCreateFile(RootDirectory=...)`;
- requests only read-attributes + synchronize on the leaf;
- uses `FileShare.ReadWrite` so the still-live trusted FileOp writer can coexist, while deliberately omitting delete sharing;
- verifies leaf canonical path, ordinary-file type, non-reparse state and the receipt's destination identity;
- requires `NumberOfLinks > 0` and samples it twice before returning;
- rechecks the recorded root before releasing the handles.

The count is therefore an identity-bound, root-relative observation at the durable commit boundary, but it is **not captured from the mutation primitive's original destination handle itself**. This compatibility tradeoff avoids changing established receipt constructor/deconstruction surfaces and avoids a second content hash.

Because the attributes-only handle must coexist with FileOp's still-live writer, it cannot use the stricter `FileShare.Read` content-verification sharing mode. Hard-link creation can also occur without ordinary file-data writes. The durable count is therefore **point-in-time evidence, not a topology lock**.

## Stable current recovery observation

Later, `WindowsRootBoundFileContentFingerprintReader` reads `BY_HANDLE_FILE_INFORMATION.NumberOfLinks` from the same root-relative destination handle used for SHA-256 verification.

Its production success path requires the pre/post count to be positive and unchanged while the primary stream is hashed, in addition to the existing root/path/type/reparse/identity/size/last-write checks. Only then does it return `CurrentDestinationHardLinkCount`.

The Core content verifier does not make this optional field a prerequisite for SHA-256 truth. A compatible root-bound reader can still produce valid content evidence without topology evidence; in that case topology status is `Unavailable`.

## Separate topology status

`FileOperationRecoveryContentVerificationItem` exposes:

- `NoRecordedCount` — legacy/weaker history has no durable count;
- `SameCount` — current stable count equals the recorded post-Copy count;
- `DifferentCount` — current stable count differs from the recorded post-Copy count;
- `Unavailable` — a recorded count exists but a stable current count was not obtained.

`DifferentCount` does **not** become `DifferentMainStream`. Bytes can match exactly while observed namespace topology differs.

Likewise, `SameCount` means only **the same observed count**. It does not prove the same hard-link names or complete topology. One hard link can be removed and another added while the total count remains unchanged.

## Compatibility boundary

Hard-link evidence uses body properties and optional capability/decorator interfaces rather than changing established positional record constructors. Legacy four-argument read-result construction and four-value deconstruction remain available, and existing public enum ordinals remain unchanged.

Existing `IFileOperationActionHistoryStore` implementers remain source-compatible. Topology-aware Windows composition opts in through the decorator + capability store rather than changing every store implementation.

## Safety boundary

Hard-link count is **evidence only**. Neither `SameCount` nor `DifferentCount`:

- changes `UndoKind`;
- creates a delete candidate;
- exposes `CanDelete` or `CanUndo`;
- authorizes deletion, replacement or move;
- identifies the other hard-link names;
- proves complete namespace topology;
- settles ACL, ADS, EA, metadata or other non-main-stream policy.

A future destructive action still needs a final handle-bound authorization protocol, a topology policy, and explicit user approval. This slice stops before that boundary.

## Validation without hosted Actions

Run the dedicated standard-library model/source guard:

```powershell
python tools/verify_recovery_hard_link_evidence.py --repo-root . --cases 50000
```

The shared Windows compiler/native batch remains:

```bat
tools\test-windows-copy-local.cmd
```

No GitHub Actions execution is required.
