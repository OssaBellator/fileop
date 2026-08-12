# File delete pre-mutation preparation

This slice closes the gap between #127's value-only history binding and a provider-acquired read-only stability lease. It still does **not** authorize or perform deletion.

`FileDeleteOperationPreMutationPreparation.PrepareAsync(...)` takes one exact session-only delete authorization receipt, an ordinal, the configured `IFileDeleteOperationStabilityLeaseProvider`, and the configured `IFileDeleteOperationActionHistoryStore`.

The preparation order is deliberate:

1. reject pre-cancelled work before any acquisition;
2. construct the exact `FileDeleteOperationStabilityLeaseRequest` for the receipt + ordinal;
3. acquire the existing read-only stability lease through the provider itself;
4. reject a missing, mutation-authorizing, or differently-bound provider result;
5. while that lease is held, read the current history snapshot through the history-store abstraction;
6. validate #127's exact receipt/stability/history binding against that provider-returned evidence;
7. return a scope that privately owns the acquired lease.

The scope never exposes the underlying `IFileDeleteOperationStabilityLease`. It exposes only the validated binding, authorization, stability evidence, history snapshot and ordinal. `StabilityLeaseHeld` remains true until scope disposal, and disposal transfers to the privately owned lease exactly once.

If preparation fails after acquisition — including missing/stale history, substituted evidence, cancellation during the history read, or another binding failure — the acquired lease is released before the failure is returned. If both preparation and lease cleanup fail, both exceptions are preserved in an `AggregateException` rather than silently hiding either failure.

## What the scope proves

The preparation scope records that this layer itself invoked the configured stability provider and read the configured history store while owning the returned read-only lease. It therefore exposes:

- `StabilityLeaseProviderAcquisitionObserved == true`;
- `HistoryStoreReadObserved == true`;
- `StabilityLeaseHeld == true` until disposal.

Those statements are intentionally narrower than mutation authority. An arbitrary interface implementation is not thereby proven to be the concrete Windows provider or SQLite store; production composition remains responsible for choosing reviewed implementations.

The earlier `FileDeleteOperationHistoryBindingEvidence` correctly keeps `StabilityLeaseAcquisitionProven == false` because that value object can still be constructed from public stability evidence without invoking a provider. The new preparation scope is the layer that performs the provider call and owns its returned lease.

## Mutation boundary remains closed

The scope hard-codes:

- `DeleteMutationAuthorized == false`;
- `MutationBarrierSatisfied == false`.

Preparation does not call `MarkMutationStartedAsync`, `CommitDeletedAsync`, `MarkMutationRecoveryRequiredAsync`, or another action-history transition. It does not request `DELETE` access and does not expose a filesystem handle.

A future reviewed mutation path must still perform the action-history store's exact `Pending -> MutationStarted` transition immediately before mutation. The history snapshot in this preparation can become stale concurrently; it is not a lock on SQLite state and is not a substitute for the exact-row barrier predicate.

The existing Windows stability provider remains read-only: it holds canonical root/direct-child identity handles, requests metadata/traverse access only, omits `DELETE` desired access, and exposes no mutation primitive. This preparation simply gives Core an ownership lifetime around that already-reviewed lease.

## Still out of scope

No `FileOperationKind.Delete`, generic executor/state-machine integration, delete-capable handle, `File.Delete`, `Directory.Delete`, `DeleteFileW`, `SetFileInformationByHandle`, recycle-bin operation, directory recursion, Storage cleanup action UI, App/Indexer production consumer, automatic restart/retry, or indexing protocol change is added. Protocol remains v8.

## Validation

`tools/verify_file_delete_pre_mutation_preparation.py` is standard-library-only. Its randomized model checks acquisition/read ordering, fail-closed provider/history/binding cases, cleanup after acquisition failures, pre-cancellation, held-scope lifetime, idempotent disposal and a permanently untouched mutation barrier. Source guards require provider acquisition before history read, private lease ownership, exact #127 binding reuse, no action-history mutation calls or filesystem mutation APIs, no production consumer, Copy/Move-only generic operations and protocol v8.

Focused .NET contract tests use fake provider/store implementations and fail if preparation calls any action-history write method. They cover exact preparation/disposal, substituted provider evidence, mutation-authorizing lease refusal, missing/non-pending/terminal history, history-read failure cleanup and pre-cancellation.
