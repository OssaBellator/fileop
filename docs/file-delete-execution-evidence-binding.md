# File delete execution evidence binding

This boundary connects the already-reviewed delete prerequisites without authorizing deletion.

For one file ordinal, `FileDeleteOperationExecutionEvidenceBinder` binds three current inputs:

- the exact in-memory `FileDeleteOperationUserAuthorizationReceipt` produced after explicit user confirmation;
- `FileDeleteOperationStabilityLeaseEvidence` acquired for that exact receipt object and ordinal while the read-only root/leaf stability handles are held;
- the current `FileDeleteOperationActionHistory` snapshot for the same plan and authorization attempt.

The result is `FileDeleteOperationExecutionEvidenceBinding`. It is **point-in-time evidence only** and keeps `DeleteMutationAuthorized` hard-coded `false`.

## Required agreement

Binding fails closed unless all of these remain true:

- the authorization still represents an explicit user-authorized attempt and none of the three inputs claims mutation authority;
- the ordinal exists in the authorization plan, authorization evidence, and durable history;
- authorization and history preserve the same complete ordered entry count;
- the stability evidence is bound by object reference to the exact authorization receipt and exact ordinal;
- history operation ID equals the authorized plan ID and history authorization ID equals the consent receipt authorization ID;
- history is non-terminal and the selected durable entry is still `Pending`;
- the selected planned, authorized, stability, and history `FileOperationEntry` evidence agrees in captured order;
- canonical source-directory path and `FileIdentity` agree across authorization, stability evidence, and history;
- canonical source-file path and `FileIdentity` agree across authorization, stability evidence, and history.

Case-insensitive comparison is used for already-canonical Windows paths; identities must match exactly.

## Why this is still not permission to delete

The binding does not call the history store and cannot claim the `Pending` row. Another actor or later orchestration step can change durable history after the binding object is created. Therefore a future executor must still use the history store's exact durable `Pending -> MutationStarted` transition as the concurrency/pre-mutation barrier immediately before any destructive primitive is attempted.

Likewise, the current stability lease is deliberately read-only. It requests no `DELETE` desired access and exposes no mutation primitive. A later, separately reviewed boundary must acquire the minimum delete right while preserving the exact root/leaf identity binding through the same-handle mutation.

The binding object must not be persisted or treated as a restart/resume capability. Durable authorization ID/time remain audit provenance, not reusable consent.

## Scope

This slice adds no:

- `FileOperationKind.Delete`;
- generic executor integration;
- `DELETE` desired access or delete-capable Windows handle provider;
- `File.Delete`, `Directory.Delete`, `DeleteFileW`, `SetFileInformationByHandle`, recycle-bin, move, or replacement call;
- automatic recovery/retry;
- Storage cleanup action or production UI consumer;
- indexing-helper operation or protocol change.

Protocol remains v8.
