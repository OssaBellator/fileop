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

    session = source['session']
    trust_call = session.index('IndexingServiceHelperTrustPolicy.RequireTrustedForElevation(executable);')
    runas = session.index('startInfo.Verb = "runas";')
    process_start = session.index('process = Process.Start(startInfo)')
    assert trust_call < runas < process_start
    assert 'if (elevated)' in session[:trust_call]

    policy = source['policy']
    for needle in [
        'IndexingServiceHelperLocator.ResolveAdjacentHelper(AppContext.BaseDirectory)',
        'if (!string.Equals(requested, adjacent, StringComparison.OrdinalIgnoreCase))',
        'restricted to the exact FileOp.Indexer.exe installed beside the running FileOp application',
        'WindowsAuthenticodeFileTrust.VerifyPinnedEmbeddedSignature(adjacent)',
        'WindowsElevatedHelperPathProtection.RequireProtectedLaunchPath(adjacent)',
    ]:
        assert needle in policy, needle

    package = source['package']
    for needle in [
        'FileOpRequireTrustedIndexerSigner=true',
        'FileOpTrustedIndexerSignerThumbprints=$thumbprint',
        'signtool.exe',
        'sign /sha1 $thumbprint /fd SHA256 /tr $TimestampUrl /td SHA256',
        'verify /pa /all',
        'Get-AuthenticodeSignature',
        'Get-FileHash -Algorithm SHA256',
        'fileop-release-manifest.json',
        'Compress-Archive',
    ]:
        assert needle in package, needle

    install = source['install']
    for needle in [
        "Join-Path $env:ProgramFiles 'FileOp'",
        "Get-Process -Name 'FileOp.App'",
        "$segments = @($relative -split '[\\\\/]')",
        '$path.StartsWith($tempRoot + [IO.Path]::DirectorySeparatorChar',
        'Get-FileHash -Algorithm SHA256',
        'Get-AuthenticodeSignature',
        'TrustedSignerThumbprint',
        'New-Item -ItemType Directory -Path $stage -Force',
        "Copy-Item -Path (Join-Path $temp '*') -Destination $stage -Recurse -Force",
        'Move-Item -LiteralPath $install -Destination $backup',
        'Move-Item -LiteralPath $stage -Destination $install',
        'Move-Item -LiteralPath $backup -Destination $install',
    ]:
        assert needle in install, needle

    uninstall = source['uninstall']
    assert 'PurgeUserData' in uninstall
    assert "Join-Path $env:LOCALAPPDATA 'FileOp'" in uninstall
    assert 'Per-user data remains' in uninstall

    assert 'does not accept an environment variable as a signer trust root'.casefold() in source['docs'].casefold()
    assert 'verify_release_trust.py --repo-root $repoRoot' in source['gate']

    # Release runtime trust cannot depend on a runtime pin environment variable.
    assert 'GetEnvironmentVariable("FileOpTrustedIndexerSignerThumbprints"' not in source['trust']
    return len(required_project) + len(required_trust) + 4 + 5 + 9 + 12 + 3 + 2


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
