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

- a production App/Windows producer of authorization receipts;
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

`tools/verify_file_delete_user_authorization.py` models explicit confirmation, blocked/missing identity evidence, non-empty authorization IDs, exact validation-instance binding, identity snapshots, and immutable non-mutation authority. At 50,000 randomized states it performs **389,305 assertions**.

The verifier also requires the receipt to remain unwired from `FileOp.App`, `FileOp.Windows`, and `FileOp.Indexer` in this slice, retains the existing Copy/Move operation enum and protocol v8, and forbids executor/action-history/delete primitive integration.

It is composed through the existing delete execution-validation verifier, which is already reached from the repository's single direct execution-validation local gate. Native Windows/.NET execution remains a local release-validation requirement.
