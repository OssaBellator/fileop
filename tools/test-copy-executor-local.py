#!/usr/bin/env python3
"""Run FileOp's offline Copy verification without PowerShell or GitHub Actions."""
from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Optional, Tuple

from verify_copy_basic_metadata_dotnet_sdk import list_dotnet_10_sdks


def run_step(name: str, repo_root: Path, arguments: list[str]) -> None:
    print(f"\n==> {name}", flush=True)
    subprocess.run(
        [sys.executable, *arguments],
        cwd=repo_root,
        check=True,
    )


def find_dotnet_10(repo_root: Path) -> Optional[Tuple[str, str]]:
    dotnet = shutil.which("dotnet")
    if dotnet is None:
        return None
    versions = list_dotnet_10_sdks(dotnet, repo_root)
    if not versions:
        return None
    return dotnet, versions[-1]


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
        dotnet_host, sdk_version = dotnet_10
        print(
            f"INFO: .NET 10 SDK {sdk_version} detected via {dotnet_host}; "
            "enabling the package-free C# implementation/interop probe."
        )
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
        ("Read-only Copy recovery inspection verifier", ["tools/verify_file_operation_recovery_inspection.py", "--repo-root", str(repo_root), "--cases", "50000"]),
        ("Copy content fingerprint evidence verifier", ["tools/verify_copy_content_fingerprint.py", "--repo-root", str(repo_root), "--cases", "20000"]),
        ("Recovery main-stream verification", ["tools/verify_recovery_main_stream.py", "--repo-root", str(repo_root), "--cases", "50000"]),
        ("Root-bound reader ABI verification", ["tools/verify_recovery_root_bound_reader_abi.py", "--repo-root", str(repo_root)]),
        ("Recovery destination-root identity verification", ["tools/verify_recovery_root_identity.py", "--repo-root", str(repo_root), "--cases", "50000"]),
        ("Recovery hard-link evidence verification", ["tools/verify_recovery_hard_link_evidence.py", "--repo-root", str(repo_root), "--cases", "50000"]),
        ("Recovery basic-metadata semantics verification", ["tools/verify_recovery_basic_metadata_evidence.py", "--repo-root", str(repo_root), "--cases", "50000"]),
        ("Recovery basic-metadata persistence verification", ["tools/verify_recovery_basic_metadata_persistence.py", "--repo-root", str(repo_root), "--sqlite-cases", "5000"]),
        ("Recovery owner/group/DACL security evidence verification", ["tools/verify_recovery_security_descriptor_evidence.py", "--repo-root", str(repo_root), "--cases", "50000", "--sqlite-cases", "5000"]),
        ("Recovery named-data-stream topology verification", ["tools/verify_recovery_named_data_stream_topology.py", "--repo-root", str(repo_root), "--cases", "50000", "--sqlite-cases", "5000"]),
        ("Named-stream default-alias compatibility", ["tools/verify_named_stream_default_alias.py", "--repo-root", str(repo_root), "--cases", "100000"]),
        ("Named-stream component-rule verification", ["tools/verify_named_stream_name_rules.py", "--repo-root", str(repo_root), "--cases", "100000"]),
        ("Aggregate recovery evidence assessment verification", ["tools/verify_recovery_evidence_assessment.py", "--repo-root", str(repo_root), "--cases", "50000"]),
        ("Offline paged directory browse verifier", ["tools/verify_directory_browse.py", "--repo-root", str(repo_root), "--cases", "10000"]),
        ("File Copy executor verification", ["tools/verify_file_copy_executor.py", "--repo-root", str(repo_root), "--cases", "20000"]),
        ("Windows Copy mutation handle-binding verification", ["tools/verify_windows_file_copy_mutation.py", "--repo-root", str(repo_root), "--cases", "2000"]),
        ("Copy basic metadata verification", ["tools/verify_copy_basic_metadata.py", "--repo-root", str(repo_root), "--cases", "50000"]),
        ("Windows FileBasicInformation semantics model", ["tools/verify_copy_basic_metadata_windows_semantics.py", "--cases", "100000"]),
        ("Copy basic metadata ABI verification", abi_arguments),
    ]
    if dotnet_10 is not None:
        dotnet_host, sdk_version = dotnet_10
        steps.append(
            (
                "Package-free .NET Copy metadata implementation/interop probe",
                [
                    "tools/verify_copy_basic_metadata_dotnet_sdk.py",
                    "--repo-root",
                    str(repo_root),
                    "--dotnet",
                    dotnet_host,
                    "--sdk-version",
                    sdk_version,
                ],
            )
        )

    for name, arguments in steps:
        run_step(name, repo_root, arguments)

    print("\nPASS: Copy executor, recovery inspection, content fingerprint evidence, root-bound recovery main-stream verification, root-bound reader ABI, destination-root identity evidence, hard-link evidence, recovery basic-metadata evidence, owner/group/DACL security evidence, named-data-stream topology evidence, named-stream default-alias compatibility, named-stream component rules, aggregate recovery evidence assessment, Windows mutation handle binding, basic metadata, Windows FileBasicInformation semantics, and interop ABI boundaries verified without GitHub Actions or PowerShell; optional local Clang/.NET 10 checks were used when available.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
