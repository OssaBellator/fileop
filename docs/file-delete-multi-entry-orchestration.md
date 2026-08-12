# Multi-entry file-delete orchestration and operation completion

This slice sequences FileOp's reviewed one-file same-handle delete primitive across one exact session authorization receipt. It does not introduce another filesystem mutation API.

The orchestrator consumes the existing Core/Windows chain for each `Pending` ordinal:

```text
session authorization
  -> read-only stability preparation
  -> final DELETE-capability reacquisition
  -> durable Pending -> MutationStarted barrier
  -> same-handle disposition + lease release + identity-bound Committed settlement
```

Only after every entry is durably `Committed` or `Failed` does the orchestrator call `CompleteAsync` to settle the operation-level terminal state.

## Existing history is observation, not authority

`FileDeleteOperationOrchestrator` requires an action-history operation that was already begun from the exact authorization receipt. It never calls `BeginAsync` itself.

Before every ordinal it reloads history and validates operation/authorization IDs, timing/source provenance, root identity, entry order, canonical paths, and exact `FileIdentity` values against the original session authorization and the first observed history snapshot.

Persisted history never recreates any of these live capabilities:

- stability lease;
- final DELETE-capability lease;
- final provider request;
- mutation-barrier scope;
- `FileDeleteOperationMutationAuthorization`.

A `Pending` entry must traverse the complete reviewed live-provider chain again. An already `Committed` or `Failed` entry is observed and skipped without filesystem mutation.

## Recovery-sensitive stop rule

If any current entry is `MutationStarted` or `RecoveryRequired`, orchestration stops before acquiring another provider lease and throws `FileDeleteOperationOrchestrationRecoveryRequiredException` carrying value-only current history.

This rule is operation-wide, not only per-current-ordinal. FileOp will not continue deleting later files while an earlier or later entry already carries unresolved mutation evidence.

A terminal operation whose terminal state is `RecoveryRequired` is surfaced through the same exception rather than returned as ordinary success/failure evidence.

There is no automatic restart-time mutation replay.

## Cancellation boundary

Caller cancellation is honored:

- before initial history inspection;
- before each ordinal;
- during read-only preparation/final reacquisition;
- while `FileDeleteOperationMutationBarrier.ClaimAsync` is still before destructive ownership transfer.

Once `ClaimAsync` returns, `MutationStarted` is already durable. The orchestrator therefore invokes `FileDeleteOperationMutationCommit.ExecuteAsync` with `CancellationToken.None` so caller cancellation cannot strand an avoidable post-barrier ambiguity between barrier return and mutation ownership transfer.

After one ordinal commits, cancellation is checked again before the next ordinal. A later orchestration call observes the already-committed entry and does not replay mutation.

## Failure classification

This slice deliberately does not translate arbitrary pre-barrier exceptions into durable `Failed` entries.

A preparation, validation, or final-acquisition exception before the durable barrier leaves the entry `Pending` unless an existing lower layer has already persisted another state. This avoids claiming a clean pre-mutation failure when capability cleanup or provider outcome may itself be uncertain.

Already-durable `Failed` entries are safe terminal observations and are skipped. Future policy may define specific pre-barrier failures that can be durably classified as `Failed`, but that is not implicit here.

Post-barrier failures remain owned by the merged one-file primitive:

- mutation failure -> recovery-sensitive history;
- final lease release failure -> recovery-sensitive history plus cleanup-only `FileDeleteOperationFinalLeaseReleaseException` ownership;
- commit ambiguity -> inspection/recovery without mutation replay.

The orchestrator does not catch those failures to continue with another ordinal.

## Operation completion

After all entries are observed as `Committed` or `Failed`, the orchestrator calls:

```text
CompleteAsync(operationId, CancellationToken.None)
```

The returned history must preserve exact static provenance and be terminal:

- `Succeeded` only when every entry is `Committed`;
- `Failed` only when at least one entry is `Failed` and no entry is pending/recovery-sensitive.

If `CompleteAsync` throws or returns untrusted evidence, the orchestrator reads current history without caller cancellation. If exact terminal history is already durable, it accepts that state without replaying any mutation. If all entries remain durably `Committed`/`Failed` but the operation itself is still non-terminal, it reports completion ambiguity; a later orchestration call may retry only `CompleteAsync`, because all per-entry mutation states are already terminal and therefore skipped.

## Result evidence

`FileDeleteOperationOrchestrationResult` is value-only. It reports:

- the exact session authorization receipt;
- completed durable history;
- how many entries this call mutated;
- how many already-terminal entries this call observed;
- whether terminal completion was already present/observed through outcome inspection.

It exposes no live lease and reports no mutation authority.

## Scope boundary

This slice adds no:

- generic `FileOperationKind.Delete`;
- App/Files/Storage cleanup button or automatic cleanup action;
- Indexer mutation consumer;
- directory deletion;
- recycle-bin/undo behavior;
- protocol change;
- path-based delete fallback.

The next product step can build an explicit recovery/reconciliation experience and a user-facing cleanup action on top of this completed Core orchestration boundary, but those surfaces remain separately reviewed.

## Validation

`FileDeleteOperationOrchestratorTests` pins mixed states, one-shot per-ordinal mutation, cancellation between entries, recovery-stop behavior, completion-write ambiguity, and cleanup-owner retention.

`WindowsFileDeleteOperationOrchestratorTests` runs two real temporary files through the Windows stability/final providers, same-handle mutation, SQLite per-entry settlement, and operation `CompleteAsync`, requiring both namespaces to disappear and final history to be `Succeeded`.

`tools/verify_file_delete_multi_entry_orchestration.py` independently models randomized multi-entry state transitions and source-contract guards. It is wired into `tools/test-local.ps1 -OfflineOnly`.

The complete Windows local gate remains mandatory before this orchestration slice can merge.
