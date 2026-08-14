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
        '[System.Environment]::Is64BitOperatingSystem',
        '[System.Environment]::Is64BitProcess',
        'requires a 64-bit Windows OS and a 64-bit PowerShell host',
        '[System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::ProgramFiles)',
        'will not use an environment-variable or caller-selected fallback path',
        '$expectedPackageHash = $TrustedPackageSha256.ToLowerInvariant()',
        '$actualPackageHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $package).Hash.ToLowerInvariant()',
        '$actualPackageHash -ne $expectedPackageHash',
        'independently supplied release metadata',
        '$expectedThumbprint = ($TrustedSignerThumbprint',
        '$manifestThumbprint -ne $expectedThumbprint',
        'independently supplied trusted signer',
        '$programFiles = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($knownProgramFiles))',
        "$install = [IO.Path]::GetFullPath((Join-Path $programFiles 'FileOp'))",
        "$expectedInstall = $programFiles + [IO.Path]::DirectorySeparatorChar + 'FileOp'",
        'caller-selectable',
        'generic privileged',
        'canonical Program Files\\FileOp path',
        "Get-Process -Name 'FileOp.App', 'FileOp.Indexer'",
        'wait for its indexing helper to exit before installing or updating',
        '$work = Join-Path $parent (".FileOp.work." + [Guid]::NewGuid().ToString(\'N\'))',
        "$stage = Join-Path $work 'payload'",
        "$protectedPackage = Join-Path $work 'fileop-release.zip'",
        'Copy-Item -LiteralPath $package -Destination $protectedPackage',
        '$protectedPackageHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $protectedPackage).Hash.ToLowerInvariant()',
        '$protectedPackageHash -ne $expectedPackageHash',
        'protected package copy no longer matches the independently supplied SHA-256',
        'Expand-Archive -LiteralPath $protectedPackage -DestinationPath $stage -Force',
        '$stageRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($stage))',
        '[System.Collections.Generic.HashSet[string]]',
        '[System.StringComparer]::OrdinalIgnoreCase',
        "$segments = @($relative -split '[\\\\/]')",
        '$path.StartsWith($stageRoot + [IO.Path]::DirectorySeparatorChar',
        '$manifestPaths.Add($relative)',
        '[IO.FileAttributes]::ReparsePoint',
        'existing canonical FileOp install path is a reparse point',
        '$file.Length -ne [long]$entry.Length',
        'Get-ChildItem -LiteralPath $stage -Recurse -File -Force',
        'unexpected file not listed by the manifest',
        'Get-AuthenticodeSignature',
        '$actual -ne $expectedThumbprint',
        'No user-writable intermediate copy exists after verification.',
        'Move-Item -LiteralPath $install -Destination $backup',
        'Move-Item -LiteralPath $stage -Destination $install',
        'Move-Item -LiteralPath $backup -Destination $install',
        'Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue',
    ]
    for needle in required_install:
        assert needle in install, needle

    assert '[string]$InstallDirectory' not in install
    assert '$env:ProgramFiles' not in install
    assert '[IO.Path]::GetTempPath()' not in install
    assert 'Expand-Archive -LiteralPath $package' not in install
    assert "Copy-Item -Path (Join-Path $temp '*') -Destination $stage" not in install

    initial_hash_check = install.index('$actualPackageHash -ne $expectedPackageHash')
    protected_copy = install.index('Copy-Item -LiteralPath $package -Destination $protectedPackage')
    protected_hash_check = install.index('$protectedPackageHash -ne $expectedPackageHash')
    extraction = install.index('Expand-Archive -LiteralPath $protectedPackage -DestinationPath $stage -Force')
    final_publish = install.index('Move-Item -LiteralPath $stage -Destination $install')
    assert initial_hash_check < protected_copy < protected_hash_check < extraction < final_publish

    uninstall = source['uninstall']
    required_uninstall = [
        'PurgeUserData',
        '[System.Environment]::Is64BitOperatingSystem',
        '[System.Environment]::Is64BitProcess',
        'requires a 64-bit Windows OS and a 64-bit PowerShell host',
        '[System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::ProgramFiles)',
        '[System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::LocalApplicationData)',
        'will not use an environment-variable or caller-selected fallback path',
        'will not use an environment-variable fallback for purge state',
        '$programFiles = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($knownProgramFiles))',
        "$install = [IO.Path]::GetFullPath((Join-Path $programFiles 'FileOp'))",
        "$expectedInstall = $programFiles + [IO.Path]::DirectorySeparatorChar + 'FileOp'",
        'intentionally not a',
        'generic elevated recursive-delete wrapper',
        'canonical FileOp install path is a reparse point',
        '$localAppData = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($knownLocalAppData))',
        "$userData = [IO.Path]::GetFullPath((Join-Path $localAppData 'FileOp'))",
        "$expectedUserData = $localAppData + [IO.Path]::DirectorySeparatorChar + 'FileOp'",
        'per-user data root did not resolve beneath the current-user LocalApplicationData known folder',
        'per-user data path is a reparse point',
        'Per-user data remains',
        "Get-Process -Name 'FileOp.App', 'FileOp.Indexer'",
        'wait for its indexing helper to exit before uninstalling',
    ]
    for needle in required_uninstall:
        assert needle in uninstall, needle
    assert '[string]$InstallDirectory' not in uninstall
    assert '$env:ProgramFiles' not in uninstall
    assert '$env:LOCALAPPDATA' not in uninstall

    docs = source['docs'].casefold()
    assert 'do not accept an environment variable as a signer trust root' in docs
    assert 'independently supplied' in docs
    assert 'whole-package authenticity' in docs
    assert 'must not learn its trusted package hash from the zip' in docs
    assert 'if the original user-writable zip changes after the first hash' in docs
    assert 'installation stops before extraction' in docs
    assert 'single embedded authenticode signature' in docs
    assert 'canonical `%programfiles%\\fileop`' in docs
    assert 'caller-selectable install root' in docs
    assert 'known-folder' in docs
    assert 'environment variable' in docs
    assert '64-bit powershell host' in docs
    assert 'protected program files work directory' in docs
    assert 're-hashes that protected copy' in docs
    assert 'no user-writable post-verification staging copy' in docs
    assert 'verify_release_trust.py --repo-root $repoRoot' in source['gate']

    assert 'GetEnvironmentVariable("FileOpTrustedIndexerSignerThumbprints"' not in source['trust']
    return (
        len(required_project)
        + len(required_trust)
        + 5
        + len(required_policy)
        + len(required_package)
        + len(required_install)
        + 10
        + len(required_uninstall)
        + 3
        + 15
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
