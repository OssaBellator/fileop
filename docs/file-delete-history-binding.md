# File delete history binding

This slice binds the already-reviewed delete prerequisites without adding a mutation capability.

`FileDeleteOperationHistoryBinding.Validate(...)` accepts one exact session-only `FileDeleteOperationUserAuthorizationReceipt`, one `FileDeleteOperationStabilityLeaseEvidence`, one current `FileDeleteOperationActionHistory` snapshot, and an ordinal. It succeeds only when all three evidence sources describe the same pending file and the same source root.

The binding fails closed unless:

- the stability evidence is reference-bound to the exact authorization receipt and ordinal;
- durable operation ID matches the authorization plan ID;
- durable authorization ID matches the receipt authorization ID;
- queued/validated/authorized timestamps match the exact authorization provenance;
- source pane and tab match the captured plan;
- canonical source root path and `FileIdentity` agree across authorization, stability evidence and durable history;
- the authorization/history entry counts and ordinal order agree;
- the exact `FileOperationEntry`, canonical file path and `FileIdentity` agree across all evidence;
- the durable operation is non-terminal;
- the selected durable entry is still `Pending`.

The returned `FileDeleteOperationHistoryBindingEvidence` is deliberately **snapshot evidence only**. `FileDeleteOperationStabilityLeaseEvidence` is itself a value-validation object with a public constructor, so this binding does not prove that a stability-lease provider actually acquired handles and does not prove that any such lease is still alive. It also does not imply that `MutationStarted` was persisted and does not authorize deletion. The following properties are therefore hard-coded false:

- `DeleteMutationAuthorized`;
- `MutationBarrierSatisfied`;
- `StabilityLeaseAcquisitionProven`;
- `StabilityLeaseLivenessProven`.

A future mutation executor must still acquire/revalidate its separately reviewed same-handle mutation binding and perform the exact durable `Pending -> MutationStarted` transition immediately before mutation. If the durable state changes concurrently, the history store's exact-row predicate must refuse that transition; this binding is not a concurrency substitute.

## Still out of scope

This slice adds no `DELETE` desired access, Windows mutation handle, `File.Delete`, `Directory.Delete`, `DeleteFileW`, `SetFileInformationByHandle`, recycle-bin operation, directory recursion, `FileOperationKind.Delete`, generic executor/state-machine wiring, production App/Windows/Indexer consumer, automatic recovery/retry, Storage cleanup action UI, or indexing protocol change. Protocol remains v8.

## Validation

`tools/verify_file_delete_history_binding.py` is a standard-library-only verifier. Its randomized model accepts only exact binding evidence, rejects receipt/operation/authorization/ordinal/state/path/identity/timing substitutions, and confirms canonical Windows path comparisons remain case-insensitive. Source guards keep the binding non-mutating, unwired from production, Copy/Move-only at the generic operation layer, and protocol v8.

Focused .NET contract tests cover the exact binding case and fail-closed substitutions for receipts, durable IDs, non-pending/terminal history, canonical root/file identity/path evidence, source pane/tab, and timing provenance. No Windows native API is needed by the binding contract itself.
