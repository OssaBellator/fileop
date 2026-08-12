# Windows final file-delete capability lease provider

`WindowsFileDeleteOperationFinalMutationLeaseProvider` is the concrete Windows implementation of the final lease contract defined by #136. Its **acquisition path remains non-mutating**: `AcquireAsync` only reacquires and validates the exact authorized root/file under a live DELETE-capable handle set.

The private final lease now also implements the separately reviewed #144 same-lease mutation facet. That destructive facet is not reachable from the acquisition request or value evidence. Core can invoke it only after the exact lease has crossed the durable mutation barrier and Core has minted `FileDeleteOperationMutationAuthorization`.

## What successful acquisition proves

The provider receives a Core-minted `FileDeleteOperationFinalMutationLeaseRequest` only after the earlier read-only preparation scope has been successfully released.

For the exact authorized file it:

1. revalidates that the request is file-only, explicitly user-authorized, and still non-authorizing for mutation;
2. requires the canonical file to remain a direct child of the exact canonical source root;
3. rechecks the Windows protected-location policy for both root and file;
4. opens the canonical root as a non-reparse directory with metadata/traverse access and without delete sharing;
5. validates the root's final handle path, directory/non-reparse state, and exact `FileIdentity`;
6. opens the exact leaf root-relative with `DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE`, `FILE_OPEN`, `FILE_NON_DIRECTORY_FILE`, and `FILE_OPEN_REPARSE_POINT`;
7. validates the leaf's final handle path, file/non-reparse state, and exact `FileIdentity`;
8. revalidates the still-held root after the relative leaf open; and
9. returns a private-handle final lease with value-only final evidence.

The native capability proof is the successful `NtCreateFile` request containing `DELETE` in `desiredAccess`. If Windows rejects that requested access or any evidence check fails, no final lease is returned.

The value-only `FileDeleteOperationFinalMutationLeaseEvidence` still reports provider acquisition, delete-access proof, and liveness proof as false. Those properties deliberately cannot substitute for the live provider object or mint mutation authority.

## Handle and sharing boundary

The root is opened with traverse/read-attributes/synchronize access and `FileShare.ReadWrite`, deliberately omitting delete sharing so renaming/deleting that directory is incompatible while it remains held.

The leaf is opened root-relative with:

```text
DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE
```

and `FileShare.Read`. This keeps independent write/delete namespace operations incompatible while the lease is held. Because Windows sharing checks are symmetric, an already-open reader must itself have advertised delete sharing before this final DELETE-capability handle can coexist with it.

There is no create/open-if disposition, overwrite/truncate behavior, content read, or absolute-path fallback for the leaf.

Both `SafeFileHandle` objects remain private. No raw handle is exposed through Core evidence, the final scope, mutation authorization, result evidence, or any App-facing type. `DeleteAccessCapabilityHeld` is true only while both handles remain live. `DeleteMutationAuthorized` remains false on the provider lease itself.

## Release/reacquire race protection

The earlier read-only stability lease must be released before Core can mint the final provider request. The final provider therefore does not trust pre-release path observations across that gap.

It reacquires the root and file, requires final handle path/type/reparse/identity evidence to match the exact session authorization, opens the leaf relative to the held root using `OBJECT_ATTRIBUTES.RootDirectory`, and validates the root again after the leaf opens.

Native tests deliberately replace the file and the entire source root during this handoff. Both replacements must be rejected by final-provider identity validation.

## Destructive facet after #144

Acquisition still performs no mutation. Only the private `FinalMutationLease` also implements `IFileDeleteOperationSameLeaseMutation`.

Immediately before mutation that facet requires the exact Core-minted post-barrier authorization, rechecks protected-location policy for root and file, revalidates both already-held handles, and enforces one mutation attempt per lease. It then uses `NtSetInformationFile(FileDispositionInformationEx)` on the already-held file handle with exactly:

```text
FILE_DISPOSITION_DELETE | FILE_DISPOSITION_POSIX_SEMANTICS | FILE_DISPOSITION_FORCE_IMAGE_SECTION_CHECK
```

It does not reopen the pathname. It does not use `DeleteFileW`, `File.Delete`, `Directory.Delete`, rename/move/truncate, read-only-attribute bypass, or disposition-on-close acquisition flags.

The POSIX flag is intentional: if an older read handle that shared delete remains open, ordinary disposition could leave the authorized path merely delete-pending after FileOp closes its handle. POSIX disposition removes the namespace link when FileOp's successful mutation handle closes while the older compatible handle can continue accessing its already-open stream.

`FILE_DISPOSITION_FORCE_IMAGE_SECTION_CHECK` keeps this behavior conservative for mapped executable/image sections. Without the flag, POSIX disposition can permit unlinking an active image. FileOp instead requires Windows to reject that case so Core enters its recovery-sensitive failure path rather than forcing removal of an in-use image. See `docs/file-delete-same-handle-mutation.md` for the complete mutation/settlement/recovery contract.

## Still outside this provider

The provider does not call `MarkMutationStartedAsync`, `CommitDeletedAsync`, or any action-history store. It does not create the durable barrier or decide durable settlement. Those remain Core-owned ordering decisions.

There is still no generic `FileOperationKind.Delete` / executor integration, App/Indexer/Files/Storage cleanup wiring, directory delete, recycle-bin behavior, batch orchestration, or protocol change.

## Validation

`WindowsFileDeleteOperationFinalMutationLeaseProviderTests` continues to pin acquisition: exact authorization/evidence, live capability state, native sharing exclusion, handoff replacement refusal, protected-location reevaluation, and cancellation.

`tools/verify_windows_file_delete_final_mutation_lease_provider.py` continues to model acquisition and now explicitly checks that the acquisition section itself contains no disposition call. The separately reviewed nested mutation facet is guarded by `tools/verify_file_delete_same_handle_mutation.py`, `FileDeleteOperationMutationCommitTests`, and `WindowsFileDeleteOperationSameHandleMutationTests`.

All portable verifiers are part of `tools/test-local.ps1 -OfflineOnly`. The complete Windows local gate remains mandatory before the #144 mutation slice can leave draft or merge.
