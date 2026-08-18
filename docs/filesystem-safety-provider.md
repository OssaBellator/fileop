# FileOp filesystem-safety provider V1

`FileOp.FilesystemSafety` is the first independently versioned reusable FileOp filesystem-safety package. Version 1.0.0 is intentionally read-only and non-executable: it exposes Windows filesystem identity and operation-safety evidence without granting Copy, Move, Delete, Undo, recovery-mutation, or arbitrary filesystem-write authority.

The package is Apache-2.0, targets `net10.0-windows10.0.17763.0`, and has no project/package dependency on `FileOp.App`, `FileOp.Indexer`, `FileOp.Core`, `FileOp.Windows`, storage analytics, cleanup UI, or any mutation executor. The extraction is deliberately standalone so a consumer does not gain FileOp's broader public surface transitively.

## V1 contract

The public namespace is `FileOp.FilesystemSafety.V1`. The stable contract ID is `filesystem.safety.v1`, with a hard maximum of 256 entries per operation-evidence request.

The reusable read-only capability IDs are:

- `filesystem.identity.inspect.v1`
- `filesystem.operation.preflight.v1`
- `filesystem.recovery.assess.v1`

`IFileSystemSafetyProviderV1` has only three operations:

1. `InspectIdentityAsync` returns requested path, handle-resolved canonical path, object kind, leaf reparse-point evidence, and Windows stable identity (`VolumeSerialNumber` + `FileReferenceNumber`).
2. `InspectOperationAsync` performs fresh action-time evidence collection for bounded file Copy, same-volume Move, and permanent-Delete candidates. It validates direct-child and canonical-parent relationships, stable identity when the caller supplies an expected identity, reparse-point exclusion, protected locations, supported case-insensitive namespace semantics, collisions, and same-volume strategy evidence.
3. `AssessRecovery` deterministically classifies a complete set of caller-supplied durable-history/current-observation dimensions using FileOp's fail-closed precedence: changed evidence, then unavailable evidence, then incomplete evidence, then observed-subset match.

Every evidence result exposes `MutationAuthorized == false`. A `ReadyForIndependentPolicyReview` result is not a lease, journal entry, authorization decision, or promise that later mutation is safe. Consumers must reacquire fresh evidence and retain their own confinement, authorization, transactional journal, exact target identity, mutation lease, and unknown-outcome recovery boundaries.

## Extracted Windows safety semantics

V1 preserves the strongest reviewed behavior needed by external consumers while avoiding the desktop/index/storage product boundary:

- Existing paths are opened read-only and canonicalized with `GetFinalPathNameByHandleW`.
- Stable identity is captured from the same open handle with `GetFileInformationByHandle`.
- Missing destination leaves are canonicalized only through an existing, non-reparse parent directory.
- Source entries must remain direct children of the requested source directory and must resolve under the same canonical source directory.
- Existing destination leaves that are reparse points fail closed.
- Alternate data-stream leaf syntax is outside the V1 operation boundary.
- Per-directory case-sensitive NTFS namespaces fail closed because the V1 name model is intentionally case-insensitive.
- Windows, System, Program Files, Program Files (x86), ProgramData, filesystem roots, `$Recycle.Bin`, `System Volume Information`, `Recovery`, `Boot`, and `EFI` trees are protected for mutation-adjacent review. Residual device/extended namespace paths are unclassified and therefore fail closed when mutation-adjacent classification is required.
- Same-volume Move is only mapped when current source and destination-directory identities prove the same volume serial.
- V1 blocks recursive directory-operation strategy. Directory identity can be inspected, but directory Copy/Move/Delete execution mapping remains outside this contract.

The provider contains no Win32 write/delete/rename/copy API imports. Its filesystem handles request no write access; the namespace capability probe requests read-attributes access only.

## Collision and strategy evidence

Copy and same-volume Move report destination collision evidence as none, existing file, existing directory, existing reparse point, or unavailable. `Ask`, `Skip`, and `Stop` are classification inputs only:

- `Ask` produces `NeedsCallerDecision`.
- `Skip` produces `Skip`.
- `Stop` produces `Blocked`.

Strategy evidence can be `CopyCandidate`, `SameVolumeMoveCandidate`, `PermanentDeleteCandidate`, `CrossVolumeMoveUnsupported`, or `DirectoryOperationUnsupported`. A candidate value is descriptive evidence only; no matching execution entry point exists in V1.

## Recovery/history assessment

`AssessRecovery` requires exactly one state for every V1 recovery evidence dimension:

- destination root
- destination identity
- main stream
- hard-link count
- basic metadata
- owner/group/DACL
- named data streams

The caller owns history storage and correlation. FileOp.FilesystemSafety does not open an application database or import storage/index state. Even when all observed dimensions match, the result is `ObservedSubsetMatches`, not proof that the complete object is unchanged and not authority to mutate recovery state.

## Future execution capability map

V1 publishes names for three possible future capability contracts, all with `Executable == false`:

| Candidate capability | V1 state | Promotion requirement |
| --- | --- | --- |
| `filesystem.copy.execute.v1` | non-executable | Independent tests must prove consumer confinement, writer-lease binding, transactional journal integration, exact target identity, and fail-closed unknown-outcome recovery. |
| `filesystem.move.same-volume.execute.v1` | non-executable | The Copy requirements plus same-volume identity/lease equivalence must be independently demonstrated. |
| `filesystem.delete.permanent.execute.v1` | non-executable | Destructive tests must independently prove exact identity, same-handle final mutation, journaling, protected-location policy, and unknown-outcome recovery at an equivalent-or-stronger boundary. |

Promotion requires a new independently versioned executable contract. Adding an `Execute` method to the V1 provider is explicitly out of scope.

## Consumer integration boundary

A capability-block consumer such as Windows Tester should use V1 only to collect and classify evidence. It must continue to own workspace confinement, opaque writer leases, exact-request mutation journaling, authorization/policy, exact target identity binding, final mutation semantics, and recovery after unknown outcomes. FileOp provider evidence must never be interpreted as mutation authority.
