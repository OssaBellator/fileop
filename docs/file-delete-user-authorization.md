# File delete user authorization receipt

This layer follows the read-only delete preflight and fresh canonical authorization-review validation with a deliberately narrow representation of **explicit user confirmation** for one exact validated delete attempt.

It still does not authorize filesystem mutation.

## What a receipt means

`FileDeleteOperationUserAuthorizationReceipt` records that the interactive caller obtained explicit user confirmation to attempt the exact `FileDeleteOperationPlan` represented by one ready `FileDeleteOperationExecutionValidationResult`.

A receipt can be constructed only from a validation result that is already `ReadyForAuthorizationReview`, retains current non-reparse source-directory identity evidence, and contains complete ready file identity evidence for every captured plan entry in original order.

The receipt snapshots:

- a non-empty authorization ID;
- the exact delete plan and plan ID;
- the exact validation instance;
- validation and authorization UTC timestamps;
- canonical source-directory path and stable `FileIdentity` observed by validation;
- each captured `FileOperationEntry`, canonical file path, and observed stable `FileIdentity` in exact plan order.

`UserAuthorizedAttempt` is true because the receipt represents the positive user-confirmation outcome. A decline or cancellation creates **no receipt**; the absence of a receipt is the negative outcome.

## Issuer-owned nonce and time

Callers do not construct receipts directly. The receipt constructor is internal and `IFileDeleteOperationUserAuthorizationIssuer` exposes the deliberately named `IssueAfterExplicitUserConfirmation(...)` seam.

The production `FileDeleteOperationUserAuthorizationIssuer` owns the consent observation metadata: its default path uses `Guid.NewGuid` for a fresh authorization nonce and `TimeProvider.System.GetUtcNow()` for the authorization timestamp. The issuer rejects an empty generated ID before constructing a receipt.

A dependency-injected issuer constructor exists for deterministic tests. This slice has no App, Windows, or Indexer producer at all; a later UI change must separately prove that the production issuer is invoked only from an explicit user-confirmation action. The authorization issuer owns the nonce/time observation so a future UI caller does not stamp arbitrary consent metadata directly.

## Exact validation-instance binding

The receipt deliberately stays session-only and retains the exact validation object that was reviewed. `IsBoundTo(...)` uses reference identity, not structural equality.

A later fresh canonical validation therefore produces a different validation instance and cannot silently reuse an earlier receipt, even when its paths and identities happen to be structurally equal. The user must authorize the validation instance actually presented for the attempted action.

The copied path/identity evidence exists so a later mutation boundary can compare what the user reviewed with fresh mutation-time observations. It is historical consent evidence, not proof that those paths or identities are still current.

## No time-based safety claim

The receipt records `AuthorizedAtUtc`, but there is **no time window** that turns old validation evidence into current mutation authority. A file or namespace can change immediately after validation or immediately after user confirmation.

A future product may choose a short consent-expiry policy for user-experience reasons, but expiry cannot replace fresh mutation-time revalidation.

## Mutation boundary remains closed

`FileDeleteOperationUserAuthorizationReceipt.DeleteMutationAuthorized` is hard-coded `false`.

The receipt is **not a mutation lease**. It does not hold a directory handle, file handle, namespace lock, delete-sharing constraint, or any other filesystem binding. User intent and race-safe mutation proof remain separate prerequisites.

A future destructive lane still needs a separately reviewed mutation-time boundary that:

1. receives the exact consent receipt;
2. reacquires and holds the expected source-directory namespace identity;
3. opens the exact direct-child file relative to that held directory;
4. revalidates the expected file identity/type/reparse/protected-location assumptions;
5. keeps the relevant bindings alive through the authorized relative mutation;
6. defines durable delete history/recovery semantics before exposing an action button.

This slice does **not** add:

- a production App/Windows/Indexer producer of authorization receipts;
- `FileOperationKind.Delete`;
- `IFileOperationExecutor` integration;
- a delete execution state machine;
- a mutation-bound identity/handle lease;
- delete history/recovery or recycle-bin semantics;
- a Storage cleanup delete button;
- directory recursion;
- `File.Delete`, `Directory.Delete`, `DeleteFileW`, `SetFileInformationByHandle`, shell recycle, or another delete primitive;
- indexing-helper protocol changes.

Protocol remains v8.

## Validation

`tools/verify_file_delete_user_authorization.py` models explicit confirmation, blocked/missing identity evidence, issuer-generated non-empty/distinct authorization IDs, exact validation-instance binding, identity snapshots, and immutable non-mutation authority. At its standalone 50,000-state seed it performs **458,143 assertions**.

Through the existing delete execution-validation parent, the deterministic composed seed contributes **456,643 authorization assertions** alongside **456,090 canonical delete-validation assertions**. The outer existing Copy/Move execution-validation model contributes **150,009**, so the repository's single direct execution-validation gate now carries **1,062,742 randomized assertions across 50,000 cases** before source checks.

The source verifier also requires the authorization types to remain unwired from `FileOp.App`, `FileOp.Windows`, and `FileOp.Indexer` in this slice, retains the existing Copy/Move operation enum and protocol v8, and forbids executor/action-history/delete primitive integration.

Native Windows/.NET execution remains a local release-validation requirement.
