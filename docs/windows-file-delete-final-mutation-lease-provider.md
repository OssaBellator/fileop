# Windows final file-delete capability lease provider

`WindowsFileDeleteOperationFinalMutationLeaseProvider` is the concrete Windows implementation of the final lease contract defined by #136 and consumed only through the Core release/reacquire coordinator.

It is deliberately a **capability-acquisition** component, not a delete implementation. It does not perform deletion.

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

The value-only `FileDeleteOperationFinalMutationLeaseEvidence` still reports provider acquisition, delete-access proof, and liveness proof as false. Those properties deliberately cannot substitute for the live provider object.

## Handle and sharing boundary

The root is opened with traverse/read-attributes/synchronize access and `FileShare.ReadWrite`, deliberately omitting delete sharing so renaming/deleting that directory is incompatible while it remains held.

The leaf is opened root-relative with the minimum final capability used by this slice:

```text
DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE
```

The leaf uses `FileShare.Read`. That keeps independent write/delete namespace operations incompatible while the lease is held. Because Windows sharing checks are symmetric, the fact that the existing final handle itself requested `DELETE` also means a later ordinary reader that does not advertise `FILE_SHARE_DELETE` can be rejected with a sharing violation. FileOp therefore treats this as a short-lived pre-mutation capability lease, not as a transparent long-lived read lease, and does not promise that unrelated opens can continue while it is held.

There is no create/open-if disposition, delete-on-close option, overwrite/truncate behavior, content read, or path fallback.

The provider stores both `SafeFileHandle` objects only inside its private lease implementation. No raw handle is exposed through Core evidence, the final scope, or any App-facing type.

`DeleteAccessCapabilityHeld` is true only while both private handles remain live. `DeleteMutationAuthorized` remains false for the provider lease. Disposal closes the leaf/root handles and makes the capability-held property false; disposal itself does not change the file.

## Release/reacquire race protection

The earlier read-only stability lease must be released before Core can mint the final provider request. That handoff necessarily creates a namespace race window.

The final provider does not trust the pre-release path observation across that gap. It reacquires the root and file and requires the final handle path/type/reparse/identity evidence to match the exact session authorization again.

Native tests deliberately replace the file and the entire source root **during that handoff**, immediately before the concrete final provider runs. Both replacements must be rejected by final-provider identity validation.

The leaf is opened through the held root using `OBJECT_ATTRIBUTES.RootDirectory`; the provider does not reopen the authorized leaf by an absolute pathname after validating the root. The root is checked again after the leaf opens.

## What this provider does not do

Production code in this slice contains no:

- `SetFileInformationByHandle`;
- `NtSetInformationFile`;
- `DeleteFileW`;
- `File.Delete` or `Directory.Delete`;
- file-disposition information class;
- delete-on-close option;
- rename, move, replacement, truncate, recycle-bin, or overwrite operation;
- `MarkMutationStartedAsync` or `CommitDeletedAsync` call;
- `FileDeleteOperationMutationBarrierScope` production consumer;
- generic `FileOperationKind.Delete` or executor integration;
- App/Indexer/Files/Storage wiring;
- protocol change.

The `DELETE` desired-access bit is a capability held on a live handle, not an action. A later separately reviewed primitive must consume the same live handle capability after the durable mutation barrier; it must not reopen the path and it must settle action history around the actual mutation.

## Validation

`WindowsFileDeleteOperationFinalMutationLeaseProviderTests` exercises the provider on real Windows temporary files. It checks exact authorization/evidence, live capability state, native delete/write sharing exclusion, parent-rename exclusion, release behavior, file replacement during the handoff, root replacement during the handoff, protected-location reevaluation, and pre-cancellation. Content is verified only after final-lease disposal because a real `DELETE` access handle can legitimately block an ordinary reader that does not share delete access.

`tools/verify_windows_file_delete_final_mutation_lease_provider.py` independently models native acquisition success/failure across randomized authorization, policy, identity, reparse/type, relative-leaf and open-result states. Its source guards pin the exact DELETE access mask, restrictive sharing, root-relative open, root-before/after validation, value-evidence construction, private live-handle lease, and absence of every mutation/disposition API listed above.

The existing #136 verifier is evolved only enough to permit this one reviewed Windows provider implementation; it still rejects any production call to `FileDeleteOperationFinalMutationLeasePreparation` and any unreviewed second provider.

Both verifiers are part of `tools/test-local.ps1 -OfflineOnly`. The complete Windows local gate remains mandatory before this provider can merge.
