#!/usr/bin/env python3
"""Static contract checks for the exact-head cross-volume Move Windows batch gate."""
from __future__ import annotations

import argparse
from pathlib import Path


def require(text: str, *needles: str) -> int:
    for needle in needles:
        assert needle in text, needle
    return len(needles)


def reject(text: str, *needles: str) -> int:
    for needle in needles:
        assert needle not in text, needle
    return len(needles)


def verify(repo_root: Path) -> int:
    script_path = repo_root / "tools" / "test-cross-volume-move-merge-gate.ps1"
    if not script_path.is_file():
        raise FileNotFoundError(script_path)
    script = script_path.read_text(encoding="utf-8")

    checks = 0
    checks += require(
        script,
        '[Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT',
        'WindowsBuiltInRole]::Administrator',
        'ordinary unelevated token',
        'git rev-parse HEAD',
        'git status --porcelain=v1 --untracked-files=all',
        'clean working tree',
        '$repoPathForContainment',
        '$evidencePathForContainment.StartsWith(',
        'EvidenceDirectory must be outside the repository checkout',
        'GetVolumePathNameW(',
        'GetVolumeInformationW(',
        '$sourceVolume.Serial -eq $destinationVolume.Serial',
        'different stable filesystem volume serials',
        'DotNetSdkVersion=',
        'WindowsVersion=',
        'SourceVolumeSerial=',
        'DestinationVolumeSerial=',
        'Tee-Object -FilePath $LogPath',
        '01-test-local.log',
        '02-cross-volume-security.log',
        '03-cross-volume-native.log',
        'PASS exact-head cross-volume Move merge gate',
    )

    ordered = [
        'Invoke-LoggedGateStep `\n    -Name "Complete local FileOp gate"',
        'Invoke-LoggedGateStep `\n    -Name "Ordinary-token cross-volume Move security gate"',
        'Invoke-LoggedGateStep `\n    -Name "Explicit two-volume cross-volume Move native gate"',
    ]
    positions = [script.index(item) for item in ordered]
    assert positions == sorted(positions)
    checks += len(ordered)

    checks += require(
        script,
        'tools\\test-local.ps1',
        'tools\\test-cross-volume-move-security.ps1',
        'tools\\test-cross-volume-move-native.ps1',
        '"-SourceRoot", $resolvedSourceRoot',
        '"-DestinationRoot", $resolvedDestinationRoot',
        '"-Configuration", $Configuration',
    )

    checks += reject(
        script,
        'Start-Process',
        '-Verb RunAs',
        'gh workflow',
        'github-actions',
        'continue-on-error',
        'SilentlyContinue |',
    )

    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path(__file__).resolve().parents[1])
    args = parser.parse_args()
    checks = verify(args.repo_root.resolve())
    print(f"PASS cross-volume Move merge-gate script contract ({checks} checks)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
