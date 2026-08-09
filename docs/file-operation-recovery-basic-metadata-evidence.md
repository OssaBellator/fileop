# Copy recovery basic-metadata evidence

## Purpose

Root identity, leaf identity, primary-stream SHA-256 and hard-link count still do not describe basic file metadata. A destination can keep the same object identity, bytes and observed link count while timestamps or attributes change.

This slice adds **basic-metadata evidence only**. It does not define deletion or Undo policy.

## Stable comparison contract

`FileBasicMetadataEvidence` records raw handle-observed:

- creation time (`FILETIME` bit pattern);
- last-access time (`FILETIME` bit pattern);
- last-write time (`FILETIME` bit pattern);
- raw file attributes.

The aggregate `SameStableMetadata` result intentionally compares only:

- creation time;
- last-write time;
- the Copy-owned safe attribute mask:
  - ReadOnly;
  - Hidden;
  - System;
  - Archive;
  - NotContentIndexed.

The mask is `0x00002027`, matching the handle-bound Copy metadata layer.

### Last-access is diagnostic only

Last-access equality is exposed as `LastAccessTimeMatchesDiagnostic`, but it never participates in the aggregate status. A filesystem read, provider recall/hydration, mount policy or FileOp's own verification can update last-access without representing an external user mutation.

Treating an atime difference as automatic user-change evidence would therefore create false positives.

### Other attribute bits are outside the aggregate

Provider/filesystem-managed or destination-owned bits such as Offline, Temporary, Sparse, Compressed, Encrypted, Reparse and integrity-related state are not collapsed into this basic comparison. Some have separate semantics or were explicitly outside the Copy metadata contract.

Raw attributes are retained in the snapshot for diagnostics/future policy, but only the five copied safe bits participate in `StableCopiedAttributesMatch`.

## Commit-bound durable observation

`WindowsRootBoundFileCommitBasicMetadataEvidenceSource` collects hard-link count and basic metadata from the **same root-relative leaf handle** while the Copy executor still owns its mutation lease.

It requests only read-attributes + synchronize for the leaf, verifies the recorded destination root and receipt destination identity, and does not read file data or hash the stream again.

The source samples twice and requires these to remain stable during collection:

- destination identity/path/type/non-reparse state;
- positive hard-link count;
- creation time;
- last-write time;
- the five copied safe attribute bits.

Last-access may differ between samples without invalidating the evidence.

As with #49's link-count collection, commit-time evidence must coexist with FileOp's still-live trusted writer, so the metadata handle uses `FileShare.ReadWrite` while omitting delete sharing. The resulting observation is **point-in-time evidence, not a metadata/topology lock**.

## Atomic persistence

`SqliteFileOperationActionHistoryBasicMetadataEvidenceStore` adds schema-v1 side table:

```text
file_operation_action_entry_basic_metadata_evidence
  operation_id
  ordinal
  creation_time_filetime
  last_access_time_filetime
  last_write_time_filetime
  file_attributes
```

There is no schema-version increment or migration. Legacy/hard-link-only history remains readable and returns no metadata evidence.

The combined strong commit/recovery transition writes, in one transaction:

1. action entry state + destination identity;
2. SHA-256 fingerprint;
3. hard-link count;
4. basic metadata snapshot.

A metadata insert failure rolls the entire transition back. Strong methods preload their return snapshot before the transaction and perform no database read after `transaction.Commit()`, avoiding a durable success being misreported because of a later read failure.

Metadata is retrieved through the optional stronger store capability rather than adding another positional field to `FileOperationActionEntry`.

## Existing executor bridge

`WindowsFileOperationActionHistoryBasicMetadataEvidenceStore` keeps the existing `FileCopyOperationExecutor` call shape unchanged.

On `CommitCopyAsync`, it collects one combined commit observation and caches the exact destination identity + SHA-256 + observation for that attempt. If durable commit fails, only that exact matching observation can flow into the immediate RecoveryRequired transition.

If evidence collection never succeeded or the recovery identity/fingerprint do not match the cached observation, recovery is downgraded to the legacy evidence-insufficient form rather than persisting a mixed/partial proof.

A process crash loses the memory-only cache; durable `MutationStarted` remains conservative.

## Current recovery observation

`WindowsRootBoundFileBasicMetadataEvidenceReader` performs a separate current root-bound metadata read:

- destination root opened and identity/path/type/reparse checked;
- leaf opened relative to the verified root with `NtCreateFile(RootDirectory=...)`;
- write/delete sharing denied for the observation;
- leaf identity/path/type/reparse checked;
- creation time, last-write time and safe copied attributes sampled twice;
- root rechecked before success.

Last-access does not need to remain stable.

`FileOperationRecoveryBasicMetadataVerifier` independently rechecks reader-reported success provenance before comparing current metadata with the durable snapshot.

### Timing limitation

This current metadata observation is a **second root-bound handle observation**, not the same leaf handle used by the #47 SHA-256 read. The two evidence stages therefore do not form one atomic snapshot, and metadata can theoretically change between them.

That is acceptable only because both stages remain non-destructive evidence. A future destructive authorization boundary must perform whatever final content/topology/metadata policy it adopts under fresh handles kept alive through the actual authorized relative operation.

## Status meaning

Basic metadata comparison reports:

- `NoRecordedEvidence` — legacy/weaker history has no durable metadata snapshot;
- `SameStableMetadata` — creation, last-write and copied safe attributes match;
- `DifferentStableMetadata` — at least one stable compared field differs;
- `Unavailable` — durable evidence exists but current strong observation could not be established.

The last-access diagnostic may differ even when status is `SameStableMetadata`.

This status is separate from:

- main-stream SHA-256 status;
- hard-link-count status;
- root/leaf identity status.

No one status is renamed "unchanged file".

## Safety boundary

Basic-metadata evidence does not:

- set or change `UndoKind`;
- create `CanDelete` or `CanUndo`;
- authorize delete, replace, move or recovery mutation;
- define ACL/security-descriptor policy;
- enumerate or compare alternate data streams;
- compare extended attributes;
- prove historical absence of temporary changes that were later restored;
- provide a held final authorization lock.

Final destructive recovery still requires explicit user authorization and final handle-bound revalidation.

## Validation without hosted Actions

The comparison model is:

```powershell
python tools/verify_recovery_basic_metadata_evidence.py --repo-root . --cases 50000
```

The persistence/native source guard is documented by the companion metadata persistence verifier on this branch. Windows compiler/native execution remains deferred to the shared Copy batch after the stacked slices are ready.
