# FileOp — Case Study

## Problem

Windows file tools often separate search, storage analysis, and mutation into different authority models. That can make decisions stale: the metadata used to recommend an action may no longer describe the object that is actually mutated.

FileOp is designed around one reusable NTFS/USN-backed index while keeping evidence and mutation authority deliberately separate.

## Constraints

- Search, Files, and Storage should share indexed metadata rather than trigger unrelated rescans.
- Read-only evidence must never become implicit permission to mutate.
- File identity, canonical path, and recovery state can change between observation and execution.
- Elevated indexing must not turn the desktop process into a broadly privileged file-mutation service.
- Physical-storage evidence can be incomplete or time-sensitive and must stay distinguishable from logical size.
- Release claims must remain bounded by the real signing/package validation that has actually been performed.

## Design decisions

1. **One indexed source, multiple read surfaces.** Search, exact paged browsing, Storage, and Optimize consume the same durable metadata model where the required semantics are available.
2. **Evidence is non-authorizing.** A Storage recommendation, duplicate candidate, or selected row is not sufficient authority for deletion.
3. **Fresh validation at the mutation boundary.** File operations re-check identity, canonical path, protected-location rules, and recovery state before durable mutation.
4. **Narrow mutation capabilities.** The ordinary queue exposes reviewed regular-file Copy and same-volume Move. Permanent delete uses a separate, stricter authorization and recovery path.
5. **Bounded duplicate verification.** Same-size groups are only candidate evidence. Explicit SHA-256 verification is bounded and still does not grant deletion authority.
6. **Privilege separation.** Native indexing can use a helper, but the helper protocol does not expose generic cleanup or destructive filesystem commands.

## Implementation

Reviewer entry points:

- [`docs/architecture.md`](./docs/architecture.md) — system boundaries and lifecycle.
- [`docs/files-browser.md`](./docs/files-browser.md) — exact browsing plus Copy/Move execution rules.
- [`src/FileOp.Windows/Ntfs/NtfsIndexSynchronizer.cs`](./src/FileOp.Windows/Ntfs/NtfsIndexSynchronizer.cs) — native index synchronization.
- [`src/FileOp.Windows/IndexingService/IndexingServiceHelperTrustPolicy.cs`](./src/FileOp.Windows/IndexingService/IndexingServiceHelperTrustPolicy.cs) — helper identity/trust policy.
- [`tests/FileOp.Windows.Tests/FileDeleteOperationExecutionValidationTests.cs`](./tests/FileOp.Windows.Tests/FileDeleteOperationExecutionValidationTests.cs) — representative delete-boundary tests.
- [`docs/local-validation.md`](./docs/local-validation.md) — authoritative local validation model.

The permanent-delete path is intentionally separate from ordinary Copy/Move planning. It requires fresh preflight, canonical/protected-location validation, explicit confirmation for the exact paths, recovery-history checks, an authorization receipt, durable action history, and a reviewed final mutation boundary.

## Verification

The public proof card is based on **completed verifier stages observed on 2026-10-03**, not on an invented UI and not on a claim that the entire offline sweep completed.

Observed completed evidence included:

- 1.25 million storage-identity checks;
- 700,000 lifecycle checks;
- 700,000 publication-barrier checks;
- 500,000 search-lifetime checks;
- 800,000 threshold-policy assertions;
- 450,000 known-location assertions.

The repository also documents a broader local gate that covers offline verifiers, Core/Windows/Indexer builds, native regression/integration tests, the WinUI app, bundled-helper checks, and a real helper-process handshake.

## Limitations

- The observed full `-OfflineOnly` run later stalled at the known-location Files-handoff verifier. It was stopped rather than represented as a full green sweep.
- The proof card therefore demonstrates only the verifier stages that were observed completing successfully.
- Production signing/package validation still requires a real production-certificate dry run before a release should be described as production-ready.
- Duplicate and physical-reclaim evidence are advisory. They are not deletion consent.

## What this demonstrates for a client

This project demonstrates work on Windows-native indexing, durable state, privilege boundaries, mutation safety, identity revalidation, recovery-aware operations, bounded verification, and documentation that keeps incomplete validation visible instead of converting it into a stronger claim.