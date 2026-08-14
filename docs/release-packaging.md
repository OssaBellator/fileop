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

`tools/install-release.ps1` requires an elevated PowerShell process, an **independently supplied** `-TrustedPackageSha256` and an **independently supplied** `-TrustedSignerThumbprint`.

This release tooling owns exactly one binary installation target: the canonical `%ProgramFiles%\FileOp` directory. There is deliberately no caller-selectable install root. An elevated updater that accepted an arbitrary destination would become a generic privileged rename/delete primitive and would weaken the runtime helper-path trust boundary. A future custom installation location requires its own reviewed ACL/path-protection policy rather than a free-form path switch.

The expected package hash and signer are deliberately not learned from the package. The installer verifies the entire ZIP SHA-256 before extraction. It then requires the package manifest's build signer to match the caller-supplied signer pin and requires every FileOp PE signature to match it as well. Editing a ZIP, manifest, dependency or data file therefore changes the required independent package hash; editing the manifest cannot choose a new trusted signer.

Before replacing an installation it:

- resolves and rechecks the canonical `%ProgramFiles%\FileOp` target and refuses a caller-selected fallback path;
- verifies the complete ZIP against the independently supplied SHA-256 before extraction;
- expands the package into a temporary directory;
- validates every manifest path remains inside that extraction root, rejects duplicate paths, verifies each length and SHA-256, and rejects payload reparse points;
- rejects unexpected extracted files that are not represented by the manifest (apart from the manifest itself);
- verifies every FileOp PE signature and requires its signer to match the independently supplied thumbprint;
- requires signed `FileOp.App.exe` and `FileOp.Indexer.exe`;
- refuses to update while either `FileOp.App` or `FileOp.Indexer` is still running;
- refuses to replace an existing `%ProgramFiles%\FileOp` root if that root is a reparse point;
- stages the verified package under the Program Files parent;
- renames the old installation to a backup and the staged installation into place;
- restores the backup if the replacement rename fails;
- removes the backup only after the new directory is in place.

Keeping staging and installed directories under the same protected parent makes the final directory replacement a same-filesystem namespace operation rather than a partially copied live install.

## Persistence compatibility

Application binaries live under the protected installation directory. Per-user indexes, action history and settings remain under `%LOCALAPPDATA%\FileOp` and are not stored inside the release package.

Any future persistent schema migration must be backward/forward policy-aware before an updater ships it. The updater must never delete an old data store merely because a new binary cannot read it; unsupported schema versions fail closed today.

## Uninstall

`tools/uninstall-fileop.ps1` removes only the canonical `%ProgramFiles%\FileOp` installation. It has no caller-selectable install root, so the elevated recursive delete cannot be redirected to an arbitrary filesystem path. It also refuses to delete the canonical install root if that root is a reparse point.

Per-user FileOp data is preserved by default so uninstall/reinstall does not silently destroy indexes or recovery evidence. Uninstall refuses to proceed while either `FileOp.App` or `FileOp.Indexer` is still running. `-PurgeUserData` is an explicit separate choice that removes `%LOCALAPPDATA%\FileOp` only after the FileOp processes are quiescent, and that purge refuses a reparse-point user-data root.

## Remaining release validation

The package/install scripts themselves require the batched Windows gate plus a real signing certificate dry run before a production release. That validation must include a successful pinned elevated-helper launch from the installed Program Files location and negative tests for package-hash mismatch, unsigned/wrong-signer binaries, a multi-signature helper, manifest-signer mismatch, unexpected payload files, running-app/helper replacement attempts, reparse-point install/user-data roots and attempts to use a user-writable/custom helper location.
