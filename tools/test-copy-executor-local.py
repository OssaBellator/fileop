#!/usr/bin/env python3
"""Run FileOp's offline Copy verification without PowerShell or GitHub Actions."""
from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Optional


def run_step(name: str, repo_root: Path, arguments: list[str]) -> None:
    print(f"\n==> {name}", flush=True)
    subprocess.run(
        [sys.executable, *arguments],
        cwd=repo_root,
        check=True,
    )


def find_dotnet_10(repo_root: Path) -> Optional[str]:
    dotnet = shutil.which("dotnet")
    if dotnet is None:
        return None

    version = subprocess.run(
        [dotnet, "--version"],
        cwd=repo_root,
        check=False,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    if version.returncode != 0 or not version.stdout.strip().startswith("10."):
        return None
    return dotnet


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    args = parser.parse_args()
    repo_root = args.repo_root.resolve()

    abi_arguments = ["tools/verify_copy_basic_metadata_abi.py", "--repo-root", str(repo_root)]
    clang = shutil.which("clang")
    if clang is not None:
        abi_arguments.append("--clang")
        print(f"INFO: Clang detected at {clang}; enabling x86/x64/ARM64 Windows COFF ABI cross-compilation.")
    else:
        print("INFO: Clang not found; skipping optional Windows COFF ABI cross-compilation.")

    dotnet_10 = find_dotnet_10(repo_root)
    if dotnet_10 is not None:
        print(f"INFO: .NET 10 detected at {dotnet_10}; enabling the package-free C# implementation/interop probe.")
    else:
        print("INFO: .NET 10 SDK not found; skipping the optional package-free C# implementation/interop probe.")

    steps = [
        ("Offline Storage UI/property verifier", ["tools/verify_storage_ui.py", "--repo-root", str(repo_root)]),
        ("Offline Storage UI edge-case verifier", ["tools/verify_storage_ui_edgecases.py"]),
        ("Offline Storage file-type verifier", ["tools/verify_storage_types.py", "--repo-root", str(repo_root)]),
        ("Offline Storage file-type randomized verifier", ["tools/verify_storage_types_fuzz.py", "--cases", "1000"]),
        ("Offline Storage history verifier", ["tools/verify_storage_history.py", "--repo-root", str(repo_root), "--cases", "1000"]),
        ("Offline Storage history Unicode-root verifier", ["tools/verify_storage_history_unicode.py", "--repo-root", str(repo_root)]),
        ("Offline Storage history service verifier", ["tools/verify_storage_history_service.py", "--repo-root", str(repo_root), "--cases", "2000"]),
        ("Offline Storage history UI/scheduler verifier", ["tools/verify_storage_history_ui.py", "--repo-root", str(repo_root), "--cases", "10000"]),
        ("Offline indexed Files browser verifier", ["tools/verify_files_ui.py", "--repo-root", str(repo_root), "--cases", "10000"]),
        ("Offline file operation state verifier", ["tools/verify_file_operation_state.py", "--repo-root", str(repo_root), "--cases", "20000"]),
        ("Offline file operation preflight verifier", ["tools/verify_file_operation_preflight.py", "--repo-root", str(repo_root), "--cases", "50000"]),
        ("Offline file operation execution validation verifier", ["tools/verify_file_operation_execution_validation.py", "--repo-root", str(repo_root), "--cases", "50000"]),
        ("Offline file operation action-history verifier", ["tools/verify_file_operation_action_history.py", "--repo-root", str(repo_root), "--cases", "20000"]),
        ("Offline paged directory browse verifier", ["tools/verify_directory_browse.py", "--repo-root", str(repo_root), "--cases", "10000"]),
        ("File Copy executor verification", ["tools/verify_file_copy_executor.py", "--repo-root", str(repo_root), "--cases", "20000"]),
        ("Windows Copy mutation handle-binding verification", ["tools/verify_windows_file_copy_mutation.py", "--repo-root", str(repo_root), "--cases", "2000"]),
        ("Copy basic metadata verification", ["tools/verify_copy_basic_metadata.py", "--repo-root", str(repo_root), "--cases", "50000"]),
        ("Copy basic metadata ABI verification", abi_arguments),
    ]
    if dotnet_10 is not None:
        steps.append(
            (
                "Package-free .NET Copy metadata implementation/interop probe",
                [
                    "tools/verify_copy_basic_metadata_dotnet.py",
                    "--repo-root",
                    str(repo_root),
                    "--dotnet",
                    dotnet_10,
                ],
            )
        )

    for name, arguments in steps:
        run_step(name, repo_root, arguments)

    print("\nPASS: Copy executor, Windows mutation handle binding, basic metadata, and interop ABI boundaries verified without GitHub Actions or PowerShell; optional local Clang/.NET 10 checks were used when available.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
