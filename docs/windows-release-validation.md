# Windows release validation checklist

This checklist is the native handoff for the beta/release-hardening stack. It supplements `tools/test-local.ps1`; it does not replace the aggregate gate.

Record the exact tested commit SHA and Windows/.NET versions with the results. Any source change after the recorded SHA makes the affected results stale.

## 1. Complete repository gate

From an ordinary Windows development shell with .NET 10 SDK, Python 3 and the required Windows/WinUI tooling installed:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-local.ps1
```

Do not use `-SkipWinUI` or `-SkipBenchmarks` for the final merge candidate.

The final record must show success for:

- the complete offline/model/source verifier inventory;
- `FileOp.Core` Release build;
- benchmark Release build;
- `FileOp.Windows` Release build;
- `FileOp.Indexer` x64 Release build;
- Windows regression/integration tests;
- `FileOp.App` WinUI x64 Release build;
- bundled Indexer artifact checks;
- real bundled-helper process/pipe handshake.

## 2. Files Copy regression matrix

Use disposable test directories/files. Never use irreplaceable data for mutation testing.

Verify:

- regular-file Copy to a missing destination succeeds and source content remains unchanged;
- multiple files settle monotonically and both UI progress and durable history agree on committed/skipped counts;
- **Skip existing** leaves the existing destination unchanged;
- **Stop on collision** performs no replacement and settles through the reviewed non-overwrite path;
- Ask-later cannot execute until resolved into a fresh immutable Skip or Stop plan;
- a queued plan whose source/destination tab/path changes cannot execute;
- a fresh preflight in flight keeps Run disabled even when an older Ready snapshot exists;
- cancellation during validation causes no mutation;
- cancellation during execution settles only between entries and never interrupts a post-`MutationStarted` mutation/commit boundary;
- a source/backing-index reset while Copy is active discards non-running plans but does not replay or duplicate the active plan;
- recovery-sensitive injected failures leave the original operation ID single-use.

## 3. Same-volume file Move matrix

Use two directories on the same local NTFS volume.

Verify:

- a regular-file Move to a missing destination succeeds;
- the source path disappears and the destination resolves to the same filesystem file identity recorded before rename;
- destination replacement is refused when a destination already exists;
- directory Move is refused;
- UNC/network Move is refused;
- a Move across two local volumes is classified as cross-volume and remains queued/non-executable;
- a fresh preflight in flight keeps Run disabled despite an older Ready snapshot;
- changing source/destination tab/path after preflight invalidates execution;
- cancellation during validation creates no mutation history;
- cancellation during a multi-entry Move settles only between entries;
- failure after `MutationStarted` becomes recovery-sensitive and is never automatically replayed or rolled back;
- a committed Move entry has no Copy undo kind, content fingerprint or destination hard-link evidence.

### NTFS case-sensitive directory tests

On a disposable NTFS directory, use Windows' supported per-directory case-sensitivity mechanism to create a case-sensitive source and then a case-sensitive destination in separate scenarios.

Verify:

- case-sensitive source root returns Move validation `Blocked` before durable history begins;
- case-sensitive destination root returns `Blocked` before durable history begins;
- ordinary case-insensitive roots remain eligible;
- the deterministic `WindowsMoveOperationExecutionValidatorTests` all pass, including the unavailable-capability test double;
- blocked capability cases create no `MutationStarted` entry and perform no rename.

## 4. Permanent-delete regression

The new Copy/Move stack must not weaken the existing destructive boundary.

Verify the existing permanent-delete Windows tests and one disposable UI session still enforce:

- cross-process destructive-session lock;
- recovery-history scan before authorization and recheck after confirmation;
- canonical/protected-location validation;
- explicit confirmation;
- authorization receipt before durable history begin;
- same-handle mutation and recovery-sensitive settlement;
- regular-file-only scope.

No Recycle Bin/restore operation is authorized by the permanent-delete receipt.

## 5. Release signing and package trust

Use a real test/release code-signing certificate appropriate for the release channel.

Create the package with `tools/package-release.ps1`. Publish the resulting ZIP SHA-256 through a channel independent of the ZIP itself.

Run install/update only from **64-bit PowerShell on 64-bit Windows**. Verify a positive install/update cycle with `tools/install-release.ps1` using both the independently supplied package SHA-256 and signer thumbprint. The release installer has no custom install-root parameter and must resolve its binary target to the canonical `%ProgramFiles%\FileOp` known-folder path.

The positive run must also confirm the protected staging sequence:

1. caller ZIP hash matches the independently supplied SHA-256;
2. ZIP is copied into a GUID-named work directory beneath the protected Program Files parent;
3. the protected ZIP copy is re-hashed against the same expected SHA-256;
4. extraction and all manifest/file/signature verification occur beneath that protected work directory;
5. the verified payload is renamed directly into `%ProgramFiles%\FileOp` without a `%TEMP%` or other user-writable post-verification staging copy.

Then prove the following negative cases fail closed:

- wrong package SHA-256;
- correct package with wrong independently supplied signer thumbprint;
- modified ZIP or payload;
- modify/replace the original ZIP after the first source hash but before/during protected copy: protected-copy rehash must fail before extraction/publication;
- confirm there is no user-writable `%TEMP%` extraction/stage to tamper with after verification;
- attempt from an unelevated process of the same account to modify files inside the protected `.FileOp.work.*` tree while install is paused there: access must be denied or the install must otherwise fail closed before publication;
- modified manifest;
- manifest path traversal or duplicate path;
- unexpected payload file;
- payload reparse point;
- unsigned FileOp binary;
- FileOp binary signed by the wrong certificate;
- `FileOp.Indexer.exe` carrying any secondary embedded Authenticode signature;
- helper copied to or launched from a user-writable directory;
- helper path other than the exact adjacent installed `FileOp.Indexer.exe`;
- run the installer or uninstaller from 32-bit PowerShell on 64-bit Windows: it must refuse before privileged path mutation;
- set/spoof `ProgramFiles` or `LOCALAPPDATA` environment variables before launch: known-folder resolution must keep install/uninstall/purge on the canonical roots;
- a test/refactor variant that tries to redirect install/update or uninstall away from `%ProgramFiles%\FileOp`;
- an existing `%ProgramFiles%\FileOp` root replaced with a reparse point before install/update;
- an existing `%ProgramFiles%\FileOp` root replaced with a reparse point before uninstall;
- install/update while `FileOp.App` is running;
- install/update while `FileOp.Indexer` is running;
- uninstall while either process is running.

From the protected installed location, verify one real elevated helper launch and successful desktop/helper handshake. The same binary from a user-writable location must be rejected before `runas` is started.

## 6. Update rollback, binary uninstall and per-user purge

With a valid existing installation:

- perform a successful update and confirm the new binary set is complete;
- induce a replacement failure after the old installation is renamed to backup but before the protected verified payload becomes live, and confirm the old installation is restored;
- induce a failure before live replacement and confirm the protected `.FileOp.work.*` directory is cleaned without changing the current live installation;
- if stale backup/work cleanup fails **after** successful publication, confirm the installer reports cleanup debt without pretending the live update rolled back;
- run `tools/uninstall-fileop.ps1` elevated and confirm it owns/removes only the canonical `%ProgramFiles%\FileOp` binary tree;
- confirm elevated uninstall does **not** resolve or recursively delete current-user LocalApplicationData;
- reinstall and confirm existing compatible per-user state is not silently deleted;
- run `tools/purge-user-data.ps1 -ConfirmPurge` under the ordinary current-user token and confirm it removes only the exact current-user FileOp LocalApplicationData tree;
- confirm `purge-user-data.ps1` refuses an elevated/Administrator token;
- confirm purge refuses a reparse-point user-data root and any nested reparse point before recursive deletion;
- spoof `LOCALAPPDATA` and confirm purge still resolves the OS current-user LocalApplicationData known folder rather than trusting the environment variable.

The binary uninstall and per-user purge are intentionally separate trust boundaries. Do not reintroduce a `-PurgeUserData` switch on the elevated uninstaller.

## 7. Capability/refusal regression

Confirm the release UI/source behavior still refuses unfinished capabilities rather than silently falling back:

- overwrite/replacement;
- cross-volume Move mutation;
- directory Copy/Move mutation;
- Recycle Bin/restore execution;
- different-account elevation/service behavior;
- exact-case mutation inside case-sensitive NTFS namespaces;
- live shadow-index database swap.

The directory fidelity classifier may report a plain tree as eligible for a future executor, but that classification alone must never grant mutation authority.

## 8. Validation record for the PR

Post the final results to the consolidation PR with:

- exact head SHA;
- Windows edition/build and architecture;
- 64-bit PowerShell host/version;
- `dotnet --info` summary;
- aggregate `tools/test-local.ps1` result;
- focused Copy/Move/delete results;
- case-sensitive namespace results;
- package/sign/protected-stage/install/update/binary-uninstall/user-purge results;
- exact signing certificate thumbprint used for the test (certificate private material must never be attached);
- any skipped scenario and the reason.

Do not mark the source stack merge-ready until every beta-critical item above either passes or is explicitly removed from release scope.