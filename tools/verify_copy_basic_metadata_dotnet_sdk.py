#!/usr/bin/env python3
"""Run the package-free Copy metadata probe with an explicitly selected .NET 10 SDK."""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import tempfile
from pathlib import Path
from typing import List, Optional, Tuple

import verify_copy_basic_metadata_dotnet as probe


def sdk_sort_key(version: str) -> Tuple[int, Tuple[int, ...], str]:
    core = version.split("-", 1)[0]
    try:
        numbers = tuple(int(part) for part in core.split("."))
    except ValueError:
        numbers = tuple()
    stable = 1 if "-" not in version else 0
    return stable, numbers, version


def list_dotnet_10_sdks(dotnet: str, cwd: Path) -> List[str]:
    result = subprocess.run(
        [dotnet, "--list-sdks"],
        cwd=cwd,
        check=False,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    if result.returncode != 0:
        return []

    versions = []
    for line in result.stdout.splitlines():
        version = line.strip().split(" ", 1)[0]
        if version.startswith("10."):
            versions.append(version)
    return sorted(set(versions), key=sdk_sort_key)


def select_sdk(dotnet: str, cwd: Path, requested: Optional[str]) -> str:
    installed = list_dotnet_10_sdks(dotnet, cwd)
    if requested:
        if requested not in installed:
            found = ", ".join(installed) if installed else "none"
            raise RuntimeError(
                f"Requested .NET 10 SDK {requested} is not installed; found: {found}"
            )
        return requested
    if not installed:
        raise RuntimeError("No installed .NET 10 SDK was found via 'dotnet --list-sdks'.")
    return installed[-1]


def run(arguments: List[str], cwd: Path, env: dict) -> None:
    subprocess.run(arguments, cwd=cwd, env=env, check=True)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--dotnet", help="path to the dotnet host; defaults to PATH")
    parser.add_argument("--sdk-version", help="specific installed .NET 10 SDK to pin")
    args = parser.parse_args()

    repo_root = args.repo_root.resolve()
    helper = repo_root / "src/FileOp.Windows/Operations/WindowsFileCopyBasicMetadata.cs"
    if not helper.is_file():
        raise FileNotFoundError(helper)

    dotnet = args.dotnet or shutil.which("dotnet")
    if not dotnet:
        raise RuntimeError("A dotnet host with an installed .NET 10 SDK is required.")
    sdk_version = select_sdk(dotnet, repo_root, args.sdk_version)

    with tempfile.TemporaryDirectory(prefix="fileop-copy-metadata-dotnet-sdk-") as temp_dir:
        project_dir = Path(temp_dir)
        shutil.copy2(helper, project_dir / helper.name)
        (project_dir / "InteropProbe.csproj").write_text(probe.PROJECT, encoding="utf-8")
        (project_dir / "Program.cs").write_text(probe.PROGRAM, encoding="utf-8")
        (project_dir / "global.json").write_text(
            json.dumps(
                {
                    "sdk": {
                        "version": sdk_version,
                        "rollForward": "disable",
                        "allowPrerelease": "-" in sdk_version,
                    }
                },
                indent=2,
            )
            + "\n",
            encoding="utf-8",
        )
        empty_feed = project_dir / ".empty-feed"
        empty_feed.mkdir()

        env = os.environ.copy()
        env.update(
            {
                "DOTNET_CLI_HOME": str(project_dir / ".dotnet-home"),
                "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
                "DOTNET_NOLOGO": "1",
                "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
                "NUGET_PACKAGES": str(project_dir / ".nuget-packages"),
            }
        )

        selected = subprocess.run(
            [dotnet, "--version"],
            cwd=project_dir,
            env=env,
            check=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        ).stdout.strip()
        if selected != sdk_version:
            raise RuntimeError(
                f"global.json requested SDK {sdk_version}, but dotnet selected {selected or 'unknown'}"
            )

        run(
            [
                dotnet,
                "restore",
                "--source",
                str(empty_feed),
                "--ignore-failed-sources",
            ],
            project_dir,
            env,
        )
        run([dotnet, "build", "--configuration", "Release", "--no-restore"], project_dir, env)
        run(
            [dotnet, "run", "--configuration", "Release", "--no-build", "--no-restore"],
            project_dir,
            env,
        )

    print(
        "PASS Copy basic metadata package-free .NET interop probe with explicitly pinned "
        f"SDK {sdk_version}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
