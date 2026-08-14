# Release packaging, signer trust and update policy

## Trust boundary

An adjacent `FileOp.Indexer.exe` is not trusted merely because it is next to the desktop app. Before an elevated helper launch, FileOp now requires:

1. the requested executable to be the exact `FileOp.Indexer.exe` resolved beside the running FileOp application;
2. an embedded Authenticode signature accepted by Windows Authenticode policy;
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
- produces a versioned x64 ZIP.

The manifest hashes the already-signed artifacts. It is an integrity inventory for install/update; elevated helper runtime trust still comes from Windows Authenticode policy plus the build-owned signer pin.

## Install and update

`tools/install-release.ps1` requires an elevated PowerShell process, an **independently supplied** `-TrustedSignerThumbprint`, and defaults to `%ProgramFiles%\FileOp`.

The expected signer is deliberately not learned from the package. The package manifest contains its build signer for consistency checking, but the installer first normalizes the caller-supplied trust pin, requires the manifest to match that independent value, then requires every FileOp PE signature to match it as well. Editing a ZIP and its manifest therefore cannot choose a new trusted signer.

Before replacing an installation it:

- expands the package into a temporary directory;
- validates every manifest path remains inside that extraction root and verifies each SHA-256;
- verifies every FileOp PE signature and requires its signer to match the independently supplied thumbprint;
- requires signed `FileOp.App.exe` and `FileOp.Indexer.exe`;
- refuses to update while FileOp is running;
- stages the verified package under the installation parent;
- renames the old installation to a backup and the staged installation into place;
- restores the backup if the replacement rename fails;
- removes the backup only after the new directory is in place.

Keeping staging and installed directories under the same protected parent makes the final directory replacement a same-filesystem namespace operation rather than a partially copied live install.

## Persistence compatibility

Application binaries live under the protected installation directory. Per-user indexes, action history and settings remain under `%LOCALAPPDATA%\FileOp` and are not stored inside the release package.

Any future persistent schema migration must be backward/forward policy-aware before an updater ships it. The updater must never delete an old data store merely because a new binary cannot read it; unsupported schema versions fail closed today.

## Uninstall

`tools/uninstall-fileop.ps1` removes the protected installation directory. Per-user FileOp data is preserved by default so uninstall/reinstall does not silently destroy indexes or recovery evidence.

`-PurgeUserData` is an explicit separate choice that removes `%LOCALAPPDATA%\FileOp` after FileOp is closed.

## Remaining release validation

The package/install scripts themselves require the batched Windows gate plus a real signing certificate dry run before a production release. That validation must include a successful pinned elevated-helper launch from the installed Program Files location and negative tests for unsigned, wrong-signer, manifest-signer mismatch and user-writable helper paths.
