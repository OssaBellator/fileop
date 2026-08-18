# FileOp.FilesystemSafety

`FileOp.FilesystemSafety` is a Windows-only, read-only filesystem-safety evidence provider extracted from FileOp for external capability-block consumers.

Version `1.0.0` exposes the `FileOp.FilesystemSafety.V1` contract for stable filesystem identity, handle-resolved canonical path evidence, protected-location and action-time operation review, collision/strategy evidence, and deterministic recovery/history assessment.

The provider never grants mutation authority. Its public interface contains no Copy, Move, Delete, recovery mutation, or arbitrary filesystem-write operation. Future execution capability IDs are mapped as non-executable and require independent consumer-boundary validation before any separately versioned executable contract can be introduced.

Consumers must retain their own confinement, authorization/policy, writer lease, transactional mutation journal, exact target identity binding, final mutation semantics, and fail-closed unknown-outcome recovery.

License: Apache-2.0.
