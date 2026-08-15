# Mutation filesystem identity boundary

Issue #193 tracks a beta-safety limitation in FileOp's mutation identity model. The current Windows canonical resolver constructs `FileIdentity` from the volume serial and the 64-bit `FileIndexHigh`/`FileIndexLow` fields returned by `BY_HANDLE_FILE_INFORMATION`. That identity model is suitable for the reviewed NTFS mutation scope, but it must not be presented as a full-width ReFS identity: ReFS can expose 128-bit file identifiers.

## Beta policy: NTFS-only mutation

Until FileOp adopts a reviewed full-width Windows file identity model, filesystem mutation fails closed unless the relevant freshly validated mutation roots are proven **NTFS**.

This restriction applies to the production Files mutation paths that currently depend on `FileIdentity` for authority/evidence:

- regular-file Copy: source and destination roots must both pass when at least one entry is mutation-ready;
- same-volume regular-file Move: source and destination roots must both pass when at least one entry is mutation-ready;
- permanent regular-file Delete: the source root must pass;
- future cross-volume Move enablement must preserve the same boundary while the current identity schema remains in use.

A Copy/Move validation containing only explicit `Skip` entries is allowed to settle without filesystem-capability proof because it cannot attempt a filesystem mutation. This keeps #193 scoped to mutation authority rather than unnecessarily narrowing no-op collision settlement.

ReFS and any other filesystem name are `UnsupportedFilesystem` for the current mutation model. Failure to obtain trustworthy filesystem evidence is `Unavailable`. Both states block before mutation/history or delete authorization review in the normal product validation path.

`GetVolumeInformationByHandleW` is a Windows volume-management API and is not supported for SMB shares. Therefore a remote-share root that cannot provide this exact handle-bound filesystem proof is `Unavailable` and mutation fails closed; FileOp does not substitute a server/path guess that the backing filesystem is NTFS.

Browsing, Search and indexing are not mutation authority and are intentionally **not** narrowed by this policy. Read-only queue preflight also remains separate from execution-grade mutation eligibility.

## Handle-bound proof

`WindowsMutationFilesystemCapabilityProbe` does not call path-based `GetVolumeInformationW` on a drive letter. Instead it:

1. opens the exact canonical mutation root with zero desired data access, ordinary read/write/delete sharing, backup-semantics and open-reparse-point flags;
2. requires the opened object to remain a non-reparse directory;
3. re-reads `BY_HANDLE_FILE_INFORMATION` and requires its current `FileIdentity` to match the exact identity returned by fresh execution validation;
4. resolves the final path from that same handle and requires it to remain the expected canonical root;
5. calls `GetVolumeInformationByHandleW` on that same handle;
6. admits the current mutation identity model only when the returned filesystem name is `NTFS`.

A stale identity, path change, reparse conversion, open/query failure or unavailable filesystem-name query therefore fails closed rather than falling back to a path guess.

Capability evidence is checked again by every consuming guard. A provider result must report `SupportedNtfs`, name the filesystem `NTFS`, and be bound to the exact canonical path and expected root `FileIdentity`. Missing, contradictory, wrong-path or wrong-identity evidence is blocked rather than trusted merely because a provider returned successfully.

The probe does not itself grant mutation authority.

## Two product defenses

`MutationExecutionValidatorAliases.cs` centralizes production Files composition without modifying browsing components.

### 1. Normal early validation boundary

The normal product path refuses unsupported filesystems before mutation authority/history:

- `WindowsFileOperationExecutionValidator` references inside `FileOp.App` resolve to `WindowsNtfsMutationExecutionValidator` for Copy;
- `WindowsMoveOperationExecutionValidator` resolves to `WindowsNtfsMoveOperationExecutionValidator`, which retains the existing Move namespace/volume relationship policy and then applies the NTFS guard to a mutation-ready result;
- `WindowsFileDeleteOperationExecutionValidator` resolves to `WindowsNtfsFileDeleteOperationExecutionValidator`.

Copy and same-volume Move consumers check the guarded validation before their durable operation histories begin. Delete checks it before user authorization review and delete history.

### 2. Raw mutation-provider defense-in-depth

The same product alias file also routes the last Windows mutation providers through wrappers that repeat exact-bound NTFS proof immediately before delegating to the existing raw provider:

- `WindowsFileCopyMutationPrimitive` resolves to `WindowsNtfsFileCopyMutationPrimitive`;
- `WindowsFileSameVolumeMoveMutationPrimitive` resolves to `WindowsNtfsFileSameVolumeMoveMutationPrimitive`;
- `WindowsFileDeleteOperationFinalMutationLeaseProvider` resolves to `WindowsNtfsFileDeleteOperationFinalMutationLeaseProvider`.

The wrappers do **not** replace or weaken the raw providers' existing canonical-path, object-identity, collision and exact-handle checks. They add one prerequisite: the exact source/destination roots needed by that provider must still have exact-bound NTFS evidence.

This second layer is intentionally defensive. In the normal product route it should be redundant because the same fresh execution validator already admitted NTFS immediately before the operation's mutation boundary. If a future composition bug bypasses the guarded validator, the raw provider wrapper still refuses non-NTFS rather than delegating. For Copy and same-volume Move, such a defense-in-depth refusal can occur after their durable `MutationStarted` barrier and therefore inherits the existing conservative post-barrier recovery semantics even though the inner raw mutation provider was never invoked. The early validator remains the required path for ordinary user-facing refusal. Final Delete lease acquisition occurs before its destructive mutation barrier, so its repeated NTFS refusal remains pre-barrier.

The original Windows validators and raw mutation providers remain directly testable and reusable as lower-level components. The aliases are intentionally product-level policy wiring, not a claim that canonical path resolution itself is NTFS-only.

`tools/verify_mutation_filesystem_product_wiring.py` recursively scans FileOp.App C# source (excluding generated `bin`/`obj`) and permits all six guarded lower-level names only at the exact reviewed Copy/Move/Delete call-site paths. Fully qualified lower-level construction, a local alias override or a new unrelated App use fails the offline gate, preventing a future call site from silently bypassing either layer.

## Why not truncate or hash a 128-bit ID?

A wider filesystem identifier must remain a wider identity. Hashing or truncating a 128-bit identifier into the existing 64-bit `FileReferenceNumber` would reintroduce collision risk into destructive authorization. If broader ReFS mutation support is later required, the reviewed route is to adopt the full-width Windows file ID (for example through `FILE_ID_INFO`) together with stable volume identity and migrate all persisted/runtime contracts that currently assume the 64-bit shape.

That future redesign must include schema/versioning and recovery migration tests; this beta guard deliberately avoids pretending that work has already happened.

## Validation

The #193 source/model and product-wiring checks are part of the repository-authoritative offline inventory. `tools/test-local.ps1 -OfflineOnly` already invokes `tools/verify_file_operation_execution_validation.py`; that canonical verifier runs the 50,000-case mutation-filesystem model, the #193 repository source contract and the project-wide App wiring scan alongside the existing Copy/Move/Delete execution-validation checks.

Run the normal authoritative portable gate with:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-local.ps1 -OfflineOnly
```

For a narrower #193-only iteration, the dedicated gate remains available:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-mutation-filesystem-identity.ps1 -OfflineOnly
```

Both portable paths pin the handle-bound source ordering, exact capability binding, skip-only behavior, null-provider fail-closed behavior, both product defense layers, recursive App alias wiring, NTFS-only decision policy and current 64-bit identity construction. The dedicated gate runs 50,000 deterministic randomized capability combinations; the authoritative canonical execution-validation verifier runs that same model as part of its broader inventory.

On Windows, run the targeted compiled gate:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-mutation-filesystem-identity.ps1
```

The compiled filter covers all current `WindowsMutationFilesystemCapability*` test classes: validation boundary, exact-binding, skip/null policy and raw-provider guard regressions. The real integration regression resolves a unique temporary directory through `WindowsFileOperationCanonicalPathResolver`, queries the filesystem through the exact identity-bound handle, accepts it only if it reports NTFS, and proves a deliberately stale expected identity becomes `Unavailable`.

The targeted gate then builds the x64 Indexer prerequisite and `FileOp.App` itself. That build is required because the product policy is composed through App global aliases and is not compiled merely by running the Windows test project.

Before merging the #193 implementation, also run the repository-authoritative full Windows gate on the exact final head:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-local.ps1
```

The targeted gate does not replace `tools/test-local.ps1`; it makes the #193 boundary independently reproducible while Windows testing is batched.
