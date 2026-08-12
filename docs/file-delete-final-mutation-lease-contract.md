# Final file-delete mutation lease contract

This contract defines the portable release/reacquire boundary between the earlier read-only delete stability lease and a final provider capability. It does **not** itself perform deletion or make cleanup reachable from the product UI.

The Core contract was introduced before any production Windows provider existed. The later reviewed Windows provider now implements this interface by reacquiring exact root/file handles with a `DELETE`-capable leaf handle, while the contract itself remains pre-barrier and non-mutating.

## Why the read-only lease cannot simply become the final lease

The pre-mutation preparation scope owns the read-only stability lease introduced by the earlier delete-validation chain. That lease is deliberately a namespace/identity observation and does not carry final delete access.

The Core handoff therefore follows this order:

1. start with one live `FileDeleteOperationPreMutationPreparationScope` for the exact session-only authorization receipt and ordinal;
2. successfully dispose that exact read-only preparation scope;
3. verify the read-only scope no longer reports its stability lease held;
4. only then does Core construct `FileDeleteOperationFinalMutationLeaseRequest` and call `IFileDeleteOperationFinalMutationLeaseProvider.AcquireAsync`;
5. require the returned live lease to report its delete-access capability held while still reporting `DeleteMutationAuthorized == false`;
6. require its final root/file evidence to match the exact authorization receipt, ordinal, canonical paths, and `FileIdentity` values; and
7. return `FileDeleteOperationFinalMutationLeaseScope`, which privately owns the final lease and still stops before the durable mutation barrier.

If the read-only scope cannot be released, the final provider is never invoked. If cancellation is already requested, the read-only scope is left held. If cancellation arrives after successful read-only release but before final acquisition, no final lease is acquired; a later attempt must re-enter the reviewed preparation path rather than reuse a remembered authorization as if release had been proven.

## Coordinator-only request capability

`FileDeleteOperationFinalMutationLeaseRequest` is a public type because it appears in the provider interface, but its constructor is **internal to FileOp.Core**. External callers therefore cannot mint a final provider request directly from a remembered authorization receipt.

The request is not recoverable from returned value evidence or the final scope. `FileDeleteOperationFinalMutationLeaseEvidence` keeps the request private and exposes only the already-bound authorization, ordinal, entry, canonical paths, and identities. `FileDeleteOperationFinalMutationLeaseScope` exposes neither the request nor the underlying final lease.

The production Windows provider can inspect the request while Core invokes `AcquireAsync`, but ordinary callers do not receive that request back as a reusable capability.

## Value evidence versus live capability

`FileDeleteOperationFinalMutationLeaseEvidence` remains deliberately value-only. Even when its path and identity fields are valid, it reports all of these as false:

- `ProviderAcquisitionProven`;
- `DeleteAccessCapabilityProven`;
- `LeaseLivenessProven`;
- `DeleteMutationAuthorized`.

Constructing evidence is not proof that an operating-system handle was opened. The live interface remains the capability boundary:

```text
IFileDeleteOperationFinalMutationLease
    Evidence
    DeleteAccessCapabilityHeld
    DeleteMutationAuthorized
    DisposeAsync
```

Core accepts a provider result only while the live lease reports its capability held, remains non-authorizing, and returns exact value evidence bound to the coordinator-issued request.

## Reviewed Windows provider

`WindowsFileDeleteOperationFinalMutationLeaseProvider` is the one reviewed production implementation of the provider interface.

It reacquires the exact authorized root and direct-child file after the read-only lease has been released. The file is opened root-relative with a successful native request for:

```text
DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE
```

The provider then validates the final handle path, file-vs-directory type, non-reparse state, and exact `FileIdentity`, and revalidates the still-held root after the relative leaf open.

The provider has no mutation method. It does not call a file-disposition API, delete-on-close option, path delete API, or action-history settlement method. Its private handles only establish that Windows granted the requested final capability and keep the exact binding live until disposal or later internal ownership transfer.

See `docs/windows-file-delete-final-mutation-lease-provider.md` for the native access/sharing and test boundary.

## Release/reacquisition gap

Releasing the earlier read-only lease necessarily creates a handoff interval before a final provider can reacquire the object. The contract does **not** pretend that the old evidence remains stable across that interval.

The final provider must reacquire and revalidate the exact authorized root/file identities after release. Native tests exercise file and root replacement during that handoff and require the concrete provider to reject the stale authorization evidence.

A future destructive primitive must consume the same live final capability. Reopening the pathname after final validation would reopen the namespace race and is not an acceptable fallback.

## Scope lifetime

`FileDeleteOperationFinalMutationLeaseScope` privately owns the final lease. It exposes no raw handle, underlying lease object, or reusable request.

The scope distinguishes capability from authorization:

- `FinalLeaseHeld` — Core still owns the provider-returned final lease;
- `DeleteAccessCapabilityHeld` — the live lease currently reports its final capability held;
- `PriorReadOnlyLeaseReleaseObserved` — Core successfully completed the earlier read-only release before provider acquisition;
- `FinalLeaseProviderAcquisitionObserved` — Core invoked and accepted the final provider;
- `DeleteMutationAuthorized` — false while this scope remains pre-barrier;
- `MutationBarrierSatisfied` — false while this scope remains pre-barrier;
- `DeleteMutationPerformed` — false.

Disposal is serialized. Ownership is cleared only after final-lease disposal succeeds; if disposal throws, the portable scope continues to report the lease held so a later disposal attempt can retry instead of falsely claiming release.

The later mutation-barrier slice transfers this private ownership internally under the same disposal gate before it writes `Pending -> MutationStarted`. Durable history alone still never recreates the live lease.

## Still out of scope

Even with the reviewed Windows provider, this contract/provider chain adds no:

- `SetFileInformationByHandle`, `NtSetInformationFile`, `DeleteFileW`, managed file/directory delete, recycle-bin, move, replacement, or delete-on-close production action;
- final same-handle mutation primitive;
- `CommitDeletedAsync` settlement after a filesystem mutation;
- automatic recovery/retry after restart;
- generic `FileOperationKind.Delete` / `IFileOperationExecutor` integration;
- App, Indexer, Files, or Storage production consumer;
- raw `SafeFileHandle` exposure;
- indexing-helper operation or protocol change.

Protocol remains v8.

The next native boundary can define the same-handle file-only disposition primitive that consumes live barrier authority. That primitive must be tested on the full Windows local gate before any cleanup UI or generic Delete operation is wired.
