#!/usr/bin/env python3
"""Zero-Actions source checks for FileOp release signer/elevation/package trust."""
from __future__ import annotations

import argparse
import sys
from pathlib import Path


def check_repository(root: Path) -> int:
    paths = {
        'csproj': root / 'src/FileOp.Windows/FileOp.Windows.csproj',
        'trust': root / 'src/FileOp.Windows/IndexingService/WindowsAuthenticodeFileTrust.cs',
        'policy': root / 'src/FileOp.Windows/IndexingService/IndexingServiceHelperTrustPolicy.cs',
        'session': root / 'src/FileOp.Windows/IndexingService/IndexingServiceProcessSession.cs',
        'package': root / 'tools/package-release.ps1',
        'install': root / 'tools/install-release.ps1',
        'uninstall': root / 'tools/uninstall-fileop.ps1',
        'docs': root / 'docs/release-packaging.md',
        'gate': root / 'tools/test-local.ps1',
    }
    missing = [str(path) for path in paths.values() if not path.is_file()]
    if missing:
        raise FileNotFoundError(', '.join(missing))
    source = {name: path.read_text(encoding='utf-8') for name, path in paths.items()}

    required_project = [
        'FileOpTrustedIndexerSignerThumbprints',
        'AssemblyMetadata Include="FileOpTrustedIndexerSignerThumbprints"',
        'FileOpRequireTrustedIndexerSigner',
        'Production helper trust requires',
    ]
    for needle in required_project:
        assert needle in source['csproj'], needle

    required_trust = [
        'GenericVerifyV2',
        '00AAC56B-CD44-11D0-8CC2-00C04FC295EE',
        'WinVerifyTrust(',
        'WtdRevokeWholeChain',
        'WtdProvFlagsRevocationCheckChainExcludeRoot',
        'WssVerifySpecific = 0x00000001',
        'WssGetSecondarySigCount = 0x00000002',
        'Flags = WssVerifySpecific | WssGetSecondarySigCount',
        'signatureSettings.VerifiedSignatureIndex != 0',
        'signatureSettings.SecondarySignatureCount != 0',
        'rejects ambiguous multi-signature binaries',
        'X509Certificate.CreateFromSignedFile(fullPath)',
        'trustedThumbprints.Contains(thumbprint',
        '#if DEBUG',
        'FILEOP_ALLOW_UNSIGNED_ELEVATED_HELPER_FOR_DEVELOPMENT',
        'FileMutationRights',
        'DirectoryMutationRights',
        'foreach (var desiredAccess in rights)',
        'CanOpenForAnyMutation(parentDirectory',
    ]
    for needle in required_trust:
        assert needle in source['trust'], needle
    assert source['trust'].index('VerifySingleAuthenticodeSignature(fullPath);') < source['trust'].index('X509Certificate.CreateFromSignedFile(fullPath)')

    session = source['session']
    trust_call = session.index('IndexingServiceHelperTrustPolicy.RequireTrustedForElevation(executable);')
    runas = session.index('startInfo.Verb = "runas";')
    process_start = session.index('process = Process.Start(startInfo)')
    assert trust_call < runas < process_start
    assert 'if (elevated)' in session[:trust_call]

    policy = source['policy']
    required_policy = [
        'IndexingServiceHelperLocator.ResolveAdjacentHelper(AppContext.BaseDirectory)',
        'if (!string.Equals(requested, adjacent, StringComparison.OrdinalIgnoreCase))',
        'restricted to the exact FileOp.Indexer.exe installed beside the running FileOp application',
        'WindowsAuthenticodeFileTrust.VerifyPinnedEmbeddedSignature(adjacent)',
        'WindowsElevatedHelperPathProtection.RequireProtectedLaunchPath(adjacent)',
    ]
    for needle in required_policy:
        assert needle in policy, needle

    package = source['package']
    required_package = [
        'FileOpRequireTrustedIndexerSigner=true',
        'FileOpTrustedIndexerSignerThumbprints=$thumbprint',
        'signtool.exe',
        'sign /sha1 $thumbprint /fd SHA256 /tr $TimestampUrl /td SHA256',
        'verify /pa /all',
        'Get-AuthenticodeSignature',
        'Get-FileHash -Algorithm SHA256',
        'fileop-release-manifest.json',
        'Compress-Archive',
        '$digestPath = "$zipPath.sha256"',
        '$zipHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash.ToLowerInvariant()',
        'independently authenticated release metadata',
    ]
    for needle in required_package:
        assert needle in package, needle

    install = source['install']
    required_install = [
        '[string]$TrustedPackageSha256',
        '[string]$TrustedSignerThumbprint',
        '$expectedPackageHash = $TrustedPackageSha256.ToLowerInvariant()',
        '$actualPackageHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $package).Hash.ToLowerInvariant()',
        '$actualPackageHash -ne $expectedPackageHash',
        'independently supplied release metadata',
        '$expectedThumbprint = ($TrustedSignerThumbprint',
        '$manifestThumbprint -ne $expectedThumbprint',
        'independently supplied trusted signer',
        "$programFiles = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($env:ProgramFiles))",
        "$install = [IO.Path]::GetFullPath((Join-Path $programFiles 'FileOp'))",
        "$expectedInstall = $programFiles + [IO.Path]::DirectorySeparatorChar + 'FileOp'",
        'caller-selectable elevated rename/delete target',
        'canonical Program Files\\FileOp path',
        "Get-Process -Name 'FileOp.App', 'FileOp.Indexer'",
        'wait for its indexing helper to exit before installing or updating',
        '[System.Collections.Generic.HashSet[string]]',
        '[System.StringComparer]::OrdinalIgnoreCase',
        "$segments = @($relative -split '[\\\\/]')",
        '$path.StartsWith($tempRoot + [IO.Path]::DirectorySeparatorChar',
        '$manifestPaths.Add($relative)',
        '[IO.FileAttributes]::ReparsePoint',
        'existing canonical FileOp install path is a reparse point',
        '$file.Length -ne [long]$entry.Length',
        'Get-FileHash -Algorithm SHA256',
        'unexpected file not listed by the manifest',
        'Get-AuthenticodeSignature',
        '$actual -ne $expectedThumbprint',
        'New-Item -ItemType Directory -Path $stage -Force',
        "Copy-Item -Path (Join-Path $temp '*') -Destination $stage -Recurse -Force",
        'Move-Item -LiteralPath $install -Destination $backup',
        'Move-Item -LiteralPath $stage -Destination $install',
        'Move-Item -LiteralPath $backup -Destination $install',
    ]
    for needle in required_install:
        assert needle in install, needle
    assert '[string]$InstallDirectory' not in install

    package_hash_check = install.index('$actualPackageHash -ne $expectedPackageHash')
    extraction = install.index('Expand-Archive -LiteralPath $package')
    assert package_hash_check < extraction

    uninstall = source['uninstall']
    required_uninstall = [
        'PurgeUserData',
        "$programFiles = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($env:ProgramFiles))",
        "$install = [IO.Path]::GetFullPath((Join-Path $programFiles 'FileOp'))",
        "$expectedInstall = $programFiles + [IO.Path]::DirectorySeparatorChar + 'FileOp'",
        'not a generic elevated recursive-delete wrapper',
        'canonical FileOp install path is a reparse point',
        "Join-Path $env:LOCALAPPDATA 'FileOp'",
        'per-user data path is a reparse point',
        'Per-user data remains',
        "Get-Process -Name 'FileOp.App', 'FileOp.Indexer'",
        'wait for its indexing helper to exit before uninstalling',
    ]
    for needle in required_uninstall:
        assert needle in uninstall, needle
    assert '[string]$InstallDirectory' not in uninstall

    docs = source['docs'].casefold()
    assert 'does not accept an environment variable as a signer trust root' in docs
    assert 'independently supplied' in docs
    assert 'whole-package authenticity' in docs
    assert 'must not learn its trusted package hash from the zip' in docs
    assert 'editing a zip, manifest, dependency or data file' in docs
    assert 'single embedded authenticode signature' in docs
    assert 'canonical `%programfiles%\\fileop`' in docs
    assert 'caller-selectable install root' in docs
    assert 'verify_release_trust.py --repo-root $repoRoot' in source['gate']

    assert 'GetEnvironmentVariable("FileOpTrustedIndexerSignerThumbprints"' not in source['trust']
    return (
        len(required_project)
        + len(required_trust)
        + 5
        + len(required_policy)
        + len(required_package)
        + len(required_install)
        + 2
        + len(required_uninstall)
        + 1
        + 9
        + 1
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('--repo-root', type=Path, default=Path.cwd())
    parser.add_argument('--self-test-only', action='store_true')
    args = parser.parse_args()
    if args.self_test_only:
        print('PASS release trust verifier self-test: source-only verifier has no randomized state model')
        return 0
    print(f'PASS release trust source wiring: {check_repository(args.repo_root.resolve())} checks')
    return 0


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f'FAIL: {exc}', file=sys.stderr)
        raise SystemExit(1)
