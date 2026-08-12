# Read-only file delete preflight foundation

FileOp now has a destination-less **file delete preflight contract** for reasoning about captured source files without granting or performing deletion.

This is deliberately separate from `FileOperationPlan`, whose `FileOperationIntent` is destination-oriented for Copy/Move. The delete foundation does not add `Delete` to `FileOperationKind`, does not enter `FileOperationExecutionSnapshot`, and does not reuse destination/collision semantics that do not apply to deletion.

## Captured intent

`FileDeleteOperationIntent` snapshots:

- the source pane identifier;
- the source tab ID;
- the captured source directory path;
- one or more `FileOperationEntry` values.

The entry collection is copied into a read-only snapshot at construction time. The intent rejects an empty selection. `FileDeleteOperationPlan` adds a non-empty plan ID and UTC-normalized queue timestamp.

These values describe the user's captured source context only. They are not execution authorization and do not imply that a Storage cleanup/readiness result authorizes deletion.

## Windows read-only preflight

`WindowsFileDeleteOperationPreflightValidator` reuses the existing `IFileOperationPathProbe` / `WindowsFileOperationPathProbe` read-only inspection boundary.

For the captured source directory it requires:

- a normalizable path;
- a currently existing directory;
- readable inspection evidence;
- no leaf reparse point.

For each captured entry it requires:

- a normalizable source path;
- the path still being an exact direct child of the captured source directory;
- the current leaf name matching the captured entry name case-insensitively;
- no alternate-data-stream leaf syntax;
- a **file** entry, not a directory;
- the file still existing as a file;
- readable inspection evidence;
- no leaf reparse point.

Directory entries are blocked before an entry probe. Recursive directory deletion and its recovery policy are intentionally outside this foundation.

Any failed requirement blocks the whole preflight result. A result with no blocked file entries is named `ReadyForFurtherReview`, not ready to delete. The result constructor also enforces that a ready status cannot be paired with blocked or empty item evidence.

## Non-authorization boundary

`FileDeleteOperationPreflightResult.DeleteMutationAuthorized` is always `false`.

A green read-only preflight is **not authorization to delete** and does **not** provide:

- protected-location policy;
- canonical execution-time source identity validation;
- continuity with older indexed/cleanup evidence;
- fresh user deletion authorization;
- a delete execution state machine;
- a delete executor or helper mutation lane;
- durable delete action history/recovery semantics;
- recycle-bin semantics;
- directory-recursion policy;
- post-delete verification.

Those are separate prerequisites. Until they are reviewed, no UI should interpret this preflight as permission to delete or queue deletion.

## Relationship to Storage cleanup readiness

Storage cleanup readiness remains non-authorizing. It can establish current review-file consistency for its own read-only purpose, but it is not consumed by `FileDeleteOperationPreflight` and does not set or influence `DeleteMutationAuthorized`.

A future Storage-to-Files action flow must require a separate explicit user authorization and fresh delete execution validation. It must not convert cleanup readiness into mutation authority.

## No mutation in this slice

This foundation does not call `File.Delete`, `Directory.Delete`, `DeleteFileW`, `SetFileInformationByHandle`, shell recycle APIs, or any other filesystem mutation primitive. It does not implement `IFileOperationExecutor` and does not change the indexing-helper protocol.

Protocol remains v8.

## Validation

`tools/verify_file_delete_preflight.py` contains a randomized fail-closed model and source guards. It verifies direct-child/name/file-type/reparse invariants, directory rejection, immutable non-authorization, absence of delete primitives, and that `FileOperationKind` remains Copy/Move-only.

The existing `tools/verify_file_operation_preflight.py` imports and runs the delete-preflight child, so the current `tools/test-local.ps1 -OfflineOnly` file-operation preflight entry covers both the existing Copy/Move boundary and this new destination-less delete-readiness foundation without adding a second PowerShell gate entry.

Native Windows/.NET execution remains a local release-validation requirement.
