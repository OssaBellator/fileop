# Release packaging, signer trust and update policy

## Trust boundary

An adjacent `FileOp.Indexer.exe` is not trusted merely because it is next to the desktop app. Before an elevated helper launch, FileOp now requires:

1. the requested executable to be the exact `FileOp.Indexer.exe` resolved beside the running FileOp application;
2. one embedded Authenticode signature at primary signature index 0 accepted by Windows Authenticode policy; secondary embedded signatures are rejected so signer pinning is unambiguous;
3. the signer certificate thumbprint to match the semicolon-separated thumbprints compiled into `FileOp.Windows` as `FileOpTrustedIndexerSignerThumbprints` assembly metadata;
4. the current unelevated token to be unable to open the helper file, its containing directory, or that directory's parent with any tested write/delete/ACL-ownership mutation right.

Release builds do not accept an environment variable as a signer trust root. Debug builds have one explicit local-development bypass, `FILEOP_ALLOW_UNSIGNED_ELEVATED_HELPER_FOR_DEVELOPMENT=1`; that code is excluded from Release compilation.

`IndexingServiceProcessSession.StartAsync` applies this policy before constructing a `runas` launch.

## Build and sign

`tools/package-release.ps1` is the release packaging entry point. It requires a code-signing certificate thumbprint, RFC3161 timestamp URL and product version.

The script:

- builds the x64 Release app with `FileOpRequireTrustedIndexerSigner=true` and the supplied thumbprint compiled into `FileOp.Windows`;
- stages the complete unpackaged WinUI output;
- Authenticode-signs every staged `FileOp.*.exe` and `FileOp.*.dll` with SHA-256 and timestamps the signatures;
- verifies every signature through `signtool` and `Get-AuthenticodeSignature`;
- requires every FileOp binary signer to match the same pinned certificate thumbprint;
- writes `fileop-release-manifest.json` with relative path, length and signed-file SHA-256;
- produces a versioned x64 ZIP;
- computes the final ZIP SHA-256 and writes a companion `.sha256` file for publication through independently authenticated release metadata.

The elevated-helper runtime accepts only a single embedded Authenticode signature. The release signing dry run must therefore confirm `FileOp.Indexer.exe` has no secondary embedded signatures after signing; dual-signing is intentionally unsupported by this trust boundary.

The manifest hashes the already-signed staged artifacts and is an integrity inventory. It is not itself a package trust root. Whole-package authenticity is established at install time by comparing the ZIP against a SHA-256 obtained independently of the ZIP, while elevated helper runtime trust still comes from Windows Authenticode policy plus the build-owned signer pin.

The generated `.sha256` file is publication material, not self-authentication. An installer must not learn its trusted package hash from the ZIP or from an unauthenticated sibling digest file; the release channel/operator must supply the expected hash independently.

## Install and update

`tools/install-release.ps1` requires an elevated **64-bit PowerShell host on 64-bit Windows**, an **independently supplied** `-TrustedPackageSha256` and an **independently supplied** `-TrustedSignerThumbprint`. The package is x64, and refusing a 32-bit host prevents process-bitness redirection of the Program Files known folder into the x86 installation tree.

This release tooling owns exactly one binary installation target: the canonical `%ProgramFiles%\FileOp` directory. There is deliberately no caller-selectable install root. An elevated updater that accepted an arbitrary destination would become a generic privileged rename/delete primitive and would weaken the runtime helper-path trust boundary. A future custom installation location requires its own reviewed ACL/path-protection policy rather than a free-form path switch.

The privileged Program Files root is resolved through the OS/.NET known-folder API, not from the mutable `ProgramFiles` environment variable. That distinction is part of the trust boundary: changing a process environment string must not redirect an elevated release rename/delete target.

The expected package hash and signer are deliberately not learned from the package. The installer first verifies the caller-supplied ZIP against the independently supplied SHA-256. Because that source ZIP may itself live in a user-writable directory, FileOp does **not** extract or verify payload files there. Instead, the installer creates a protected Program Files work directory, copies the ZIP into that protected directory, and **re-hashes that protected copy** against the same independently supplied SHA-256 before any extraction occurs.

All extraction, manifest checking, file hashing and Authenticode verification then occurs beneath that protected Program Files work directory. The verified payload is renamed directly from that protected work tree into the live `%ProgramFiles%\FileOp` location. There is **no user-writable post-verification staging copy** and no `%TEMP%` extraction/copy window between verification and publication.

This two-hash sequence closes two distinct same-user races:

- if the original user-writable ZIP changes after the first hash but before/during the protected copy, the protected-copy hash no longer matches and installation stops before extraction;
- after the protected copy is revalidated, an ordinary unelevated process cannot modify the protected extraction/payload tree under the Program Files parent before it is published.

Before replacing an installation it:

- requires a 64-bit OS and 64-bit PowerShell process before resolving privileged paths;
- resolves and rechecks the canonical `%ProgramFiles%\FileOp` target from the Program Files known folder and refuses environment-variable or caller-selected fallback paths;
- verifies the source ZIP against the independently supplied SHA-256;
- copies the package into a GUID-named protected work directory under the Program Files parent and verifies that protected copy against the same SHA-256;
- extracts only the protected package copy into a protected payload directory;
- validates every manifest path remains inside that protected extraction root, rejects duplicate paths, verifies each length and SHA-256, and rejects payload reparse points;
- rejects unexpected extracted files that are not represented by the manifest (apart from the manifest itself);
- verifies every FileOp PE signature and requires its signer to match the independently supplied thumbprint;
- requires signed `FileOp.App.exe` and `FileOp.Indexer.exe`;
- refuses to update while either `FileOp.App` or `FileOp.Indexer` is still running;
- refuses to replace an existing `%ProgramFiles%\FileOp` root if that root is a reparse point;
- renames the old installation to a backup and the already-verified protected payload into place;
- restores the backup if the replacement rename fails;
- removes the backup only after the new directory is in place;
- removes the protected work directory during cleanup.

Keeping extraction, verification, staging and installed directories under the same protected parent makes publication a same-filesystem namespace operation rather than a partially copied live install.

## Persistence compatibility

Application binaries live under the protected installation directory. Per-user indexes, action history and settings remain under `%LOCALAPPDATA%\FileOp` and are not stored inside the release package.

Any future persistent schema migration must be backward/forward policy-aware before an updater ships it. The updater must never delete an old data store merely because a new binary cannot read it; unsupported schema versions fail closed today.

## Uninstall

`tools/uninstall-fileop.ps1` also requires a 64-bit PowerShell host on 64-bit Windows and removes only the canonical `%ProgramFiles%\FileOp` installation. It has no caller-selectable install root, so the elevated recursive delete cannot be redirected to an arbitrary filesystem path. It also refuses to delete the canonical install root if that root is a reparse point.

Both the Program Files binary root and the current-user LocalApplicationData purge root are resolved through OS/.NET known-folder APIs rather than trusting mutable `ProgramFiles` or `LOCALAPPDATA` environment variables. `-PurgeUserData` then derives only the `FileOp` child beneath that known LocalApplicationData root and rechecks the resolved child before deletion.

Per-user FileOp data is preserved by default so uninstall/reinstall does not silently destroy indexes or recovery evidence. Uninstall refuses to proceed while either `FileOp.App` or `FileOp.Indexer` is still running. `-PurgeUserData` is an explicit separate choice that removes `%LOCALAPPDATA%\FileOp` only after the FileOp processes are quiescent, and that purge refuses a reparse-point user-data root.

## Remaining release validation

The package/install scripts themselves require the batched Windows gate plus a real signing certificate dry run before a production release. That validation must include a successful pinned elevated-helper launch from the installed Program Files location and negative tests for package-hash mismatch, protected-copy hash mismatch/source-package races, unsigned/wrong-signer binaries, a multi-signature helper, manifest-signer mismatch, unexpected payload files, running-app/helper replacement attempts, reparse-point install/user-data roots, environment-variable root spoofing, a 32-bit PowerShell host, attempts to modify the protected work tree from an unelevated process, and attempts to use a user-writable/custom helper location.
