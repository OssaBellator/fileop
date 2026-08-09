# Copy recovery hard-link count evidence

## Purpose

A destination file can acquire another hard-link name without changing its `FileIdentity`, canonical destination path or primary-stream bytes. Root/leaf identity plus SHA-256 therefore cannot by themselves describe every namespace-topology change relevant to later recovery policy.

This slice records and re-observes the destination handle's **hard-link count** as a separate piece of recovery evidence. It deliberately does not decide whether any count is safe to delete.

## Durable evidence

New topology-aware Copy commits persist a positive `DestinationHardLinkCount` together with the destination identity and post-Copy SHA-256. The validated-receipt / failed-commit recovery path persists the same evidence triple while keeping `UndoKind.None`.

The count lives in additive schema-v1 evidence storage. Legacy histories remain readable with:

```text
DestinationHardLinkCount = null
```

A null recorded count means FileOp has no durable hard-link-count evidence for that entry; it is not treated as an observed count of one.

The stronger SQLite store capability writes entry state/identity, SHA-256 and hard-link count in one transaction. A hard-link evidence insert failure rolls the transition and fingerprint insert back rather than producing a committed entry with only part of the evidence.

## Stable current observation

`WindowsRootBoundFileContentFingerprintReader` reads `BY_HANDLE_FILE_INFORMATION.NumberOfLinks` from the same root-relative destination handle used for SHA-256 verification.

A successful root-bound read requires:

- the pre-read hard-link count is greater than zero;
- the post-read count is greater than zero;
- the count did not change while the primary stream was being hashed;
- the existing root/path/type/reparse/identity/size/last-write checks also pass.

Only then is `CurrentDestinationHardLinkCount` returned with the successful SHA-256 evidence. Unsafe reader outcomes expose no current count.

## Separate topology status

`FileOperationRecoveryContentVerificationItem` exposes hard-link evidence separately from content status:

- `NoRecordedCount` — legacy/weaker history has no durable count;
- `SameCount` — current stable count equals the recorded post-Copy count;
- `DifferentCount` — current stable count differs from the recorded post-Copy count;
- `Unavailable` — a recorded count exists but a stable current count could not be obtained.

A `DifferentCount` does **not** become `DifferentMainStream`. The bytes can still match exactly while the observed namespace topology differs.

Likewise, `SameCount` means only **the same observed count**. It does not prove the same hard-link names or complete topology. For example, one hard link can be removed and a different hard link added while the total count remains unchanged.

## Compatibility boundary

Hard-link evidence is added through body properties/capability interfaces rather than changing established positional record constructors. Legacy read-result four-argument construction and four-value deconstruction remain available, and existing public enum ordinals remain unchanged.

Existing `IFileOperationActionHistoryStore` implementers remain source-compatible. New topology-aware Copy execution must use `IFileOperationActionHistoryHardLinkEvidenceStore` before crossing the mutation boundary so a new trusted hard-link count cannot be silently discarded.

## Safety boundary

Hard-link count is **evidence only**. Neither `SameCount` nor `DifferentCount`:

- changes `UndoKind`;
- creates a delete candidate;
- exposes `CanDelete` or `CanUndo`;
- authorizes deletion, replacement or move;
- identifies the other hard-link names;
- proves complete namespace topology;
- settles ACL, ADS, EA, metadata or other non-main-stream policy.

A later destructive action still needs a final handle-bound authorization protocol and explicit user approval. It must also decide how hard-link evidence participates in policy; this slice intentionally stops before that decision.

## Validation without hosted Actions

The dedicated standard-library model/source guard is:

```powershell
python tools/verify_recovery_hard_link_evidence.py --repo-root . --cases 50000
```

The Windows/native hard-link regression is part of the shared deferred batch:

```bat
tools\test-windows-copy-local.cmd
```

No GitHub Actions execution is required.
