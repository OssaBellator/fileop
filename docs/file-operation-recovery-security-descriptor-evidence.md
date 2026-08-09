# Copy recovery owner/group/DACL security evidence

## Purpose

The merged recovery evidence now covers destination-root identity, leaf identity, primary-stream SHA-256, hard-link count and basic metadata. Those observations still do not describe discretionary security state: the same file object can keep the same bytes and basic metadata while its owner, primary group or DACL changes.

This slice adds **security-descriptor evidence only**. It does not define deletion or Undo policy.

## Queried security scope

`FileSecurityDescriptorEvidence` is fixed to the Windows security-information mask `0x00000007`:

- `OWNER_SECURITY_INFORMATION` (`0x1`);
- `GROUP_SECURITY_INFORMATION` (`0x2`);
- `DACL_SECURITY_INFORMATION` (`0x4`).

The Windows handle requests `READ_CONTROL` and does not request `ACCESS_SYSTEM_SECURITY`.

SACL/audit evidence is deliberately excluded. This slice also does not query mandatory-label, resource-attribute, scope, process-trust-label or other privileged security-information classes.

## What is persisted

Windows `GetKernelObjectSecurity` returns the requested security descriptor in self-relative form. FileOp hashes exactly the returned descriptor bytes with SHA-256 and persists only:

- the queried security-information mask (`0x7`);
- the 64-character SHA-256 hex digest.

Raw security-descriptor bytes, SIDs, ACEs and ACL contents are never written to action history.

The mask is stored with the digest so a future scope expansion cannot silently compare evidence produced from different queried descriptor subsets.

## Equality semantics

`SameQueriedDescriptorBytes` means only:

> the exact self-relative bytes returned for owner + primary group + DACL matched the recorded SHA-256 digest under the same `0x7` query mask.

It is **not semantic ACL equivalence**. Two descriptors that Windows would treat equivalently but encode differently may compare different. Conversely, equality is only an observation at two points in time; it cannot prove that temporary changes did not occur and later get restored.

Security comparison is reported separately from:

- main-stream SHA-256;
- hard-link count;
- basic metadata;
- root/leaf identity.

No combined status is named "unchanged file".

## Commit-bound observation

`WindowsRootBoundFileCommitSecurityDescriptorEvidenceSource` runs while the Copy executor still owns its mutation lease. It:

1. opens and verifies the recorded destination root;
2. opens the direct destination leaf relative to that root with `NtCreateFile(RootDirectory=...)`;
3. requests `READ_CONTROL | FILE_READ_ATTRIBUTES | SYNCHRONIZE` for the leaf;
4. samples identity/path/type, hard-link count and stable basic metadata;
5. queries owner/group/DACL and hashes the returned descriptor bytes;
6. repeats the stable metadata and descriptor observation;
7. succeeds only if root/leaf provenance, topology, stable basic metadata and descriptor digest remain stable during the observation.

The commit-time leaf uses `FileShare.ReadWrite` so it can coexist with FileOp's trusted still-live mutation handle, while delete sharing remains omitted.

This is point-in-time evidence, not a security/topology lock.

## Atomic persistence

`SqliteFileOperationActionHistorySecurityDescriptorEvidenceStore` adds schema-v1 side table:

```text
file_operation_action_entry_security_descriptor_evidence
  operation_id
  ordinal
  security_information
  sha256_hex_digest
```

There is no schema-version increment or migration. Existing histories may have no security row and remain readable.

A security-aware strong commit/recovery transition writes in one SQLite transaction:

1. action-entry state + destination identity;
2. primary-stream SHA-256;
3. hard-link count;
4. basic metadata;
5. owner/group/DACL digest.

Failure of the final security insert must roll the complete transition back. `RecoveryRequired` continues to use `UndoKind.None`; evidence does not become deletion authority.

## Existing executor bridge

`WindowsFileOperationActionHistorySecurityDescriptorEvidenceStore` preserves the current `FileCopyOperationExecutor` call shape.

During the existing `CommitCopyAsync` barrier it collects one combined security-aware observation and caches the exact destination identity + content fingerprint + observation for that attempt. If durable commit fails, only an immediate recovery transition with the exact same identity/fingerprint may reuse that cached observation.

If evidence collection never succeeded, or recovery supplies different identity/fingerprint evidence, recovery falls back to the evidence-insufficient legacy transition rather than persisting a mixed proof.

A process crash discards the memory-only observation; durable `MutationStarted` remains conservative.

## Current recovery observation

`WindowsRootBoundFileSecurityDescriptorEvidenceReader` performs another read-only root-bound observation:

- destination root path/type/reparse/identity verified;
- leaf opened relative to the held root;
- leaf requests `READ_CONTROL | FILE_READ_ATTRIBUTES | SYNCHRONIZE`;
- normal write/delete sharing is not granted by the verification handle;
- leaf path/type/reparse/identity verified;
- owner/group/DACL digest sampled twice and required to remain stable;
- root and leaf provenance rechecked before success.

`FileOperationRecoverySecurityDescriptorVerifier` first reloads durable history and proves the supplied recovery inspection still represents that durable operation/root/entry before it trusts either the security side-table row or filesystem reader. Reader-reported success is independently revalidated again in Core.

## Timing limitation

The security observation is a separate handle observation from the main-stream and basic-metadata verifiers. The resulting evidence stages are not one atomic snapshot, and the handles are released afterward.

That is acceptable only because this slice remains non-destructive evidence. A future destructive recovery boundary must revalidate the final policy under fresh handles that remain alive through the authorized relative mutation.

## Safety boundary

This slice does not:

- set or change `UndoKind`;
- add `CanDelete` or `CanUndo`;
- modify owner, group, DACL or any other security descriptor field;
- request/query SACL audit state or enable security privileges;
- delete, replace, move or otherwise mutate the destination;
- enumerate or compare alternate data streams;
- compare extended attributes;
- prove complete hard-link-name topology;
- prove historical absence of temporary security changes;
- authorize Files UI execution or Undo.

Final destructive recovery still requires explicit user authorization and fresh handle-bound revalidation held through the actual relative operation.
