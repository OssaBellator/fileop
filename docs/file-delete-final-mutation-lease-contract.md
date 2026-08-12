# Final file-delete mutation lease contract

This slice defines the last portable lease boundary before a future file-delete executor can cross the durable mutation barrier. It does **not** implement Windows DELETE access, perform deletion, or make cleanup reachable from the product UI.

## Why the #130 read-only lease cannot simply become the mutation lease

The existing pre-mutation preparation scope owns the read-only stability lease introduced by #123. That lease is deliberately a read-only namespace/identity observation. The final destructive boundary needs a separately reviewed capability whose provider can later acquire the minimum delete access and keep the final root/file binding alive through mutation.

The Core handoff therefore follows this order:

1. start with one live `FileDeleteOperationPreMutationPreparationScope` for the exact session-only authorization receipt and ordinal;
2. successfully dispose that exact read-only preparation scope;
3. verify the read-only scope no longer reports its stability lease held;
4. only then does Core construct `FileDeleteOperationFinalMutationLeaseRequest` and call `IFileDeleteOperationFinalMutationLeaseProvider.AcquireAsync`;
5. require the returned live lease to report its delete-access capability held while still reporting `DeleteMutationAuthorized == false`;
6. require its final root/file evidence to match the exact authorization receipt, ordinal, canonical paths, and `FileIdentity` values;
7. return `FileDeleteOperationFinalMutationLeaseScope`, which privately owns the final lease and still stops before `Pending -> MutationStarted`.

If the #130 scope cannot be released, the final provider is never invoked. If cancellation is already requested, the read-only scope is left held. If cancellation arrives after successful read-only release but before final acquisition, no final lease is acquired; a later attempt must re-enter the reviewed preparation path rather than reusing a forged release token.

## Coordinator-only request capability

`FileDeleteOperationFinalMutationLeaseRequest` is a public type because it appears in the provider interface, but its constructor is **internal to FileOp.Core**. External callers therefore cannot mint a final provider request directly from a remembered authorization receipt.

The request is also not recoverable from returned value evidence or the final scope. `FileDeleteOperationFinalMutationLeaseEvidence` keeps the request private and exposes only the already-bound authorization, ordinal, entry, canonical paths, and identities. `FileDeleteOperationFinalMutationLeaseScope` exposes neither the request nor the underlying final lease.

This means the only production path that can create a request is the Core release/reacquire coordinator after it has observed successful release of the exact #130 scope. A future provider may of course inspect the request it is handed during `AcquireAsync`, but the request is not a reusable public capability handed back to ordinary callers.

## Value evidence versus live capability

`FileDeleteOperationFinalMutationLeaseEvidence` is deliberately value-only. Even when its path and identity fields are valid, it reports all of these as false:

- `ProviderAcquisitionProven`;
- `DeleteAccessCapabilityProven`;
- `LeaseLivenessProven`;
- `DeleteMutationAuthorized`.

Providers can construct evidence only from the coordinator-supplied request they receive. Constructing that evidence is still not proof that an operating-system handle was opened.

The live interface is separate:

```text
IFileDeleteOperationFinalMutationLease
    Evidence
    DeleteAccessCapabilityHeld
    DeleteMutationAuthorized
    DisposeAsync
```

A provider must return a live lease whose capability is held. Core validates that contract before accepting the lease, but this portable slice does not claim that a concrete Windows DELETE-capable handle exists because no production Windows provider is added here.

## Release/reacquisition gap

Releasing the earlier read-only lease necessarily creates a handoff interval before a final provider can reacquire the object. This slice does **not** pretend that the old evidence remains stable across that interval.

The purpose of the final provider is to reacquire and revalidate the exact authorized root/file identities after that release. The final evidence must still equal the session authorization, and the future Windows implementation must prove that those observations come from the same delete-capable handles retained through the later destructive operation.

A future destructive primitive must consume the same live final lease. Reopening the pathname after final validation would reopen the namespace race and is not an acceptable fallback.

## Scope lifetime

`FileDeleteOperationFinalMutationLeaseScope` privately owns the final lease. It exposes no raw handle, underlying lease object, or reusable request.

The scope distinguishes capability from authorization:

- `FinalLeaseHeld` — Core still owns the provider-returned final lease;
- `DeleteAccessCapabilityHeld` — the live lease contract currently reports that its final capability is held;
- `PriorReadOnlyLeaseReleaseObserved` — this coordinator successfully completed the #130 release before provider acquisition;
- `FinalLeaseProviderAcquisitionObserved` — this coordinator invoked and accepted the final provider;
- `DeleteMutationAuthorized` — always false in this slice;
- `MutationBarrierSatisfied` — always false in this slice;
- `DeleteMutationPerformed` — always false in this slice.

Disposal is serialized. Ownership is cleared only after final-lease disposal succeeds; if disposal throws, the scope continues to report the lease held so a later disposal attempt can retry instead of falsely claiming release.

## Still out of scope

This contract adds no:

- production `IFileDeleteOperationFinalMutationLeaseProvider` implementation;
- `DELETE` desired access constant or Windows delete-capable handle acquisition;
- `SetFileInformationByHandle`, `NtSetInformationFile`, `DeleteFileW`, `File.Delete`, `Directory.Delete`, recycle-bin, move, replacement, or path-only delete call;
- action-history `MarkMutationStartedAsync`, `CommitDeletedAsync`, recovery transition, or automatic retry;
- `FileOperationKind.Delete` generic-operation integration;
- generic `IFileOperationExecutor` integration;
- App, Windows, Indexer, Files, or Storage production consumer;
- raw `SafeFileHandle` exposure;
- indexing-helper operation or protocol change.

Protocol remains v8.

The next executor slice may combine this privately held final capability with the current durable `Pending -> MutationStarted` transition and recovery semantics. A later concrete Windows provider/primitive must then prove minimum DELETE access, root-relative identity binding, and same-handle deletion on a real Windows/.NET local gate before any cleanup UI is wired.
