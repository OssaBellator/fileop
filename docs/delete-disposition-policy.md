# Delete disposition policy

The current real delete path remains the reviewed **permanent regular-file** boundary: destructive-session lock, recovery scan, preflight, canonical/protected-location validation, explicit confirmation, second recovery scan, authorization receipt, durable history and same-handle mutation.

A future **Recycle Bin** command is intentionally a separate capability. It must not reuse the permanent-delete authorization receipt and it must not be implemented as an unreviewed path-only shell delete. Before FileOp exposes it, the implementation must define how the reviewed filesystem identity is bound to the shell/recycle operation, what success evidence is durable, how restore metadata is represented, and what crash/recovery state means.

`FileDeleteDispositionPolicy` encodes this split. `PermanentReviewed` can use the existing permanent boundary. `RecycleBin` currently reports `RequiresSeparateIdentitySafeRecycleContract` and remains non-executable.

This keeps the beta's destructive behavior narrow and explicit while reserving a safer mainstream deletion UX for a separately reviewed implementation rather than weakening the permanent-delete path.
