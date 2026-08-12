# Same-handle file-delete mutation and durable settlement

FileOp's first destructive file-delete boundary consumes the exact final Windows DELETE-capability lease that survived authorization, read-only stability preparation, final reacquisition, and the durable `Pending -> MutationStarted` barrier.

The mutation does **not reopen the pathname**. Core transfers private ownership of the live final lease out of `FileDeleteOperationMutationBarrierScope`, mints a one-operation authorization bound to that exact barrier/final evidence, invokes the destructive facet on that same provider-owned lease, releases the lease, and only then attempts identity-bound durable settlement with `CommitDeletedAsync`.

This slice is file-only. It does not add generic `FileOperationKind.Delete`, App/Files/Storage cleanup UI, directory deletion, recycle-bin semantics, batch execution, protocol changes, or privileged disk administration.

## Ordering

For one authorized ordinal:

1. caller cancellation is honored while the public barrier scope still owns the final lease;
2. Core requires the exact non-terminal `MutationStarted` history entry plus the exact live final evidence and DELETE capability;
3. the barrier scope serializes destructive transfer against disposal, performs one final cancellation check, mints `FileDeleteOperationMutationAuthorization`, and clears its public lease ownership;
4. after transfer, cancellation no longer interrupts mutation/release/history settlement;
5. the provider rechecks protected-location policy and revalidates the already-held root/file handles for exact path, type, non-reparse state, and `FileIdentity`;
6. the provider applies the destructive disposition to the already-held file handle exactly once;
7. Core releases that same final lease;
8. Core calls `CommitDeletedAsync(operationId, ordinal, exactSourceIdentity, CancellationToken.None)`;
9. a direct commit result is validated against the exact barrier provenance, or an exception/invalid result is inspected through `GetAsync` before any conclusion is reported.

A successful `FileDeleteOperationMutationCommitResult` is value-only evidence. It proves that FileOp observed the same-handle mutation, final-lease release, and exact durable `Committed` entry. It exposes no reusable mutation authorization or operating-system handle.

The selected entry can become `Committed` while the containing operation remains non-terminal. Generic multi-entry orchestration and `CompleteAsync` are intentionally later work.

## Native disposition

The Windows final lease uses `NtSetInformationFile` with `FileDispositionInformationEx` on the same private file handle acquired with DELETE access. The only disposition flags in this slice are:

```text
FILE_DISPOSITION_DELETE | FILE_DISPOSITION_POSIX_SEMANTICS | FILE_DISPOSITION_FORCE_IMAGE_SECTION_CHECK
```

No pathname is reopened for mutation. There is no `DeleteFileW`, `File.Delete`, `Directory.Delete`, rename, move, truncate, recycle-bin path, read-only-attribute bypass, or disposition-on-close acquisition flag.

### Why POSIX disposition is required

The final lease intentionally permits an already-open reader when that reader advertised delete sharing. With ordinary non-POSIX delete disposition, closing only FileOp's deleting handle can leave the file merely delete-pending until every other open handle closes. Durable `Committed` history at that point would overstate what happened to the authorized pathname.

`FILE_DISPOSITION_POSIX_SEMANTICS` removes the authorized namespace link when FileOp closes its successful mutation handle while compatible pre-existing handles may continue accessing the underlying streams. Native regression coverage keeps a delete-sharing reader open, performs the full barrier/mutation/commit pipeline, requires the pathname to be absent, and then proves the earlier reader can still consume its already-open stream.

`FILE_DISPOSITION_FORCE_IMAGE_SECTION_CHECK` deliberately keeps mapped executable/image sections conservative under POSIX semantics. Without that flag, Windows can allow the namespace link to be removed even while an image section is active. FileOp prefers the mutation to fail and enter its recovery-sensitive failure path rather than aggressively unlink an in-use mapped image.

This is namespace-link deletion evidence for the exact authorized path. If the same physical file has other hard-link names, those other namespace links are not implied to be removed.

## One-shot authority

`FileDeleteOperationMutationAuthorization` can be constructed only inside Core from a live `FileDeleteOperationMutationBarrierScope`. It binds by reference and value to the exact final evidence, authorization receipt, ordinal, canonical path, and source identity.

The Windows final lease implements `IFileDeleteOperationSameLeaseMutation` privately alongside the non-authorizing final-lease contract. The destructive method requires the exact Core-minted authorization and rejects a second mutation attempt for the same lease. Raw root/file handles remain private.

The provider's earlier acquisition path is still non-mutating: `AcquireAsync` only reacquires and validates DELETE capability. The native disposition exists only in the privately held lease facet and is unreachable through the acquisition request/value evidence alone.

## Failure and recovery semantics

Once the barrier lease is destructively transferred, FileOp never retries the filesystem mutation automatically in that call.

- If the native disposition throws, Core attempts `RecoveryRequired` and releases the lease.
- If the disposition succeeds but lease release fails, Core attempts `RecoveryRequired`, does not call `CommitDeletedAsync`, and throws `FileDeleteOperationFinalLeaseReleaseException`, which retains cleanup-only ownership so release can be retried without mutation authority.
- If `CommitDeletedAsync` throws or returns untrusted evidence after the lease was released, Core reads current history with `CancellationToken.None`.
- If inspection proves the exact entry is already `Committed`, the operation reports success without replaying mutation.
- If inspection proves the exact prior `MutationStarted` entry remains, Core attempts `RecoveryRequired` and reports failure rather than retrying deletion.
- If inspection itself fails, history disappears, or another unrecognized durable state appears, FileOp reports explicit ambiguity. Durable `MutationStarted`, `Committed`, or `RecoveryRequired` history remains the restart-time authority.

A post-barrier failure is conservatively recovery-sensitive even when the native call appears to have failed before visible namespace change. The durable barrier already states that mutation could have started, so later code must not infer safety from the exception type alone.

## Validation

`FileDeleteOperationMutationCommitTests` uses fake final leases/history stores to pin one-shot mutation, non-cancellable post-transfer calls, cleanup ownership, exact identity settlement, and commit-before/after-persistence ambiguity handling.

`WindowsFileDeleteOperationSameHandleMutationTests` exercises the real Windows provider, protected-location recheck, SQLite action history, and complete authorization/preparation/final-lease/barrier/mutation flow on temporary files. The held-reader regression distinguishes POSIX namespace removal from an ordinary delete-pending state.

`tools/verify_file_delete_same_handle_mutation.py` independently models mutation, release, commit, inspection, recovery, and cleanup ownership across randomized states. Its repository guards require the Core-only authorization, destructive transfer under the barrier ownership gate, exact `DELETE | POSIX | FORCE_IMAGE_SECTION_CHECK` native flags, same-handle/no-reopen implementation, native/fake lifecycle tests, no App/Indexer consumers, generic Delete still absent, and protocol v8 unchanged.

The verifier is part of `tools/test-local.ps1 -OfflineOnly`. Because this slice introduces the first real production filesystem mutation API, the complete Windows local gate is mandatory before the pull request can leave draft or merge.
