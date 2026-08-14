# Directory Copy and Move boundary

Directory Copy/Move remains disabled in the Files UI. This is deliberate rather than missing recursion code.

A recursive filesystem operation can silently lose semantics that a regular-file copy does not represent. Before a directory tree can enter any future mutation executor, FileOp requires complete recursive evidence and refuses trees containing any currently unsupported fidelity feature:

- reparse points;
- alternate data streams;
- non-default security descriptors;
- extended attributes;
- sparse files;
- compressed files;
- encrypted files;
- per-directory case-sensitive namespaces;
- hard-link topology.

`DirectoryOperationFidelityClassifier` makes that refusal machine-checkable. Incomplete recursive enumeration or metadata inspection is also non-executable.

A plain tree classified as `EligibleForFuturePlainTreeExecutor` is still **not authorized for mutation**. Directory Copy requires a recursive durable-history model with per-entry mutation barriers and crash settlement. Directory Move must additionally distinguish same-volume namespace rename from cross-volume copy-plus-source-delete and define subtree recovery before source removal.

The current beta should keep directory operations disabled rather than offer a partial-fidelity feature that can silently alter data or security semantics. Native Windows testing can expand the supported fidelity set only when each feature has an explicit preserve/refuse contract.
