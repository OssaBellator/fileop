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

A dependency-injected issuer constructor exists for deterministic tests. There is still no App or Indexer producer in the current delete chain; a later UI change must separately prove that the production issuer is invoked only from an explicit user-confirmation action. The authorization issuer owns the nonce/time observation so a future UI caller does not stamp arbitrary consent metadata directly.

The Windows layer now has one deliberate **consumer** of the receipt: `WindowsFileDeleteOperationStabilityLeaseProvider` from `file-delete-stability-lease.md`. It uses the receipt only to reacquire and hold read-only namespace/identity evidence. It cannot create a receipt and does not receive mutation authority from one.

## Exact validation-instance binding

The receipt deliberately stays session-only and retains the exact validation object that was reviewed. `IsBoundTo(...)` uses reference identity, not structural equality.

A later fresh canonical validation therefore produces a different validation instance and cannot silently reuse an earlier receipt, even when its paths and identities happen to be structurally equal. The user must authorize the validation instance actually presented for the attempted action.

The copied path/identity evidence exists so later stability/mutation boundaries can compare what the user reviewed with fresh observations. It is historical consent evidence, not proof that those paths or identities are still current.

## No time-based safety claim

The receipt records `AuthorizedAtUtc`, but there is **no time window** that turns old validation evidence into current mutation authority. A file or namespace can change immediately after validation or immediately after user confirmation.

A future product may choose a short consent-expiry policy for user-experience reasons, but expiry cannot replace fresh mutation-time revalidation.

## Mutation boundary remains closed

`FileDeleteOperationUserAuthorizationReceipt.DeleteMutationAuthorized` is hard-coded `false`.

The receipt is **not a mutation lease**. It does not itself hold a directory handle, file handle, namespace lock, delete-sharing constraint, or any other filesystem binding. User intent and race-safe mutation proof remain separate prerequisites.

The subsequent read-only stability lease now covers the non-mutating portion of the earlier design: it receives the exact receipt, reacquires the source-directory identity, opens the exact direct child relative to that held directory, revalidates identity/type/reparse/protected-location assumptions, and keeps metadata-only handles alive until disposal while denying incompatible write/delete sharing.

That stability lease deliberately does **not** request `DELETE` access and cannot be released/reopened by path for a later delete without recreating a race. The remaining destructive design must therefore be reviewed together with durable delete history/execution semantics so the same held identity binding can eventually be upgraded to the minimum mutation right only after a durable pre-mutation barrier exists.

The current chain still does **not** add:

- a production App/Indexer producer of authorization receipts;
- `FileOperationKind.Delete`;
- `IFileOperationExecutor` integration;
- a delete execution state machine;
- a delete-capable mutation-bound handle lease;
- delete history/recovery or recycle-bin semantics;
- a Storage cleanup delete button;
- directory recursion;
- `File.Delete`, `Directory.Delete`, `DeleteFileW`, `SetFileInformationByHandle`, shell recycle, or another delete primitive;
- indexing-helper protocol changes.

Protocol remains v8.

## Validation

The pure `run_authorization_model` in `tools/verify_file_delete_user_authorization.py` retains #122's **458,143 assertions across 50,000 randomized states** at the authorization verifier's standalone seed. The public verifier now composes the read-only stability child as well; at that standalone seed the child contributes **540,325**, for **998,468 authorization + stability assertions**.

Through the existing delete execution-validation parent, the deterministic nested seed contributes **456,643 authorization assertions** plus **540,077 stability assertions**, alongside **456,090 canonical delete-validation assertions**. The outer existing Copy/Move execution-validation model contributes **150,009**, so the repository's single direct execution-validation gate now carries **1,602,819 randomized assertions across 50,000 cases** before source checks.

The source verifier requires authorization issuance to remain unwired from `FileOp.App`/`FileOp.Indexer`, retains exact receipt/validation provenance, composes the stability source guard, preserves the existing Copy/Move operation enum and protocol v8, and forbids delete executor/action-history/mutation primitive integration.

Native Windows/.NET execution remains a local release-validation requirement.
