#!/usr/bin/env python3
"""Zero-Actions source checks for the reusable FileOp filesystem-safety provider boundary."""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path


def check_repository(root: Path) -> int:
    paths = {
        "project": root / "src/FileOp.FilesystemSafety/FileOp.FilesystemSafety.csproj",
        "contracts": root / "src/FileOp.FilesystemSafety/V1/Contracts.cs",
        "provider": root / "src/FileOp.FilesystemSafety/V1/WindowsFileSystemSafetyProviderV1.cs",
        "tests": root / "tests/FileOp.FilesystemSafety.Tests/FileSystemSafetyProviderV1Tests.cs",
        "docs": root / "docs/filesystem-safety-provider.md",
        "gate": root / "tools/test-local.ps1",
        "package_readme": root / "src/FileOp.FilesystemSafety/README.md",
    }
    missing = [str(path) for path in paths.values() if not path.is_file()]
    if missing:
        raise FileNotFoundError(", ".join(missing))

    source = {name: path.read_text(encoding="utf-8") for name, path in paths.items()}
    checks = 0

    project = source["project"]
    required_project = [
        "<TargetFramework>net10.0-windows10.0.17763.0</TargetFramework>",
        "<PackageId>FileOp.FilesystemSafety</PackageId>",
        "<VersionPrefix>1.0.0</VersionPrefix>",
        "<IsPackable>true</IsPackable>",
        "<PackageLicenseExpression>Apache-2.0</PackageLicenseExpression>",
        "<PackageReadmeFile>README.md</PackageReadmeFile>",
    ]
    for needle in required_project:
        assert needle in project, needle
        checks += 1
    assert "<ProjectReference" not in project, "provider project must not transitively expose FileOp product assemblies"
    assert "<PackageReference" not in project, "provider project must remain dependency-free in V1"
    checks += 2

    contracts = source["contracts"]
    required_contract = [
        'ContractId = "filesystem.safety.v1"',
        "MajorVersion = 1",
        "MaxEntriesPerOperation = 256",
        'IdentityInspectCapability = "filesystem.identity.inspect.v1"',
        'OperationPreflightCapability = "filesystem.operation.preflight.v1"',
        'RecoveryAssessmentCapability = "filesystem.recovery.assess.v1"',
        'EvidenceNotice = "Evidence only. This contract never grants filesystem mutation authority."',
        "public interface IFileSystemSafetyProviderV1",
        "public bool MutationAuthorized { get; }",
        '"filesystem.copy.execute.v1"',
        '"filesystem.move.same-volume.execute.v1"',
        '"filesystem.delete.permanent.execute.v1"',
    ]
    for needle in required_contract:
        assert needle in contracts, needle
        checks += 1

    interface_match = re.search(
        r"public interface IFileSystemSafetyProviderV1\s*\{(?P<body>.*?)\n\}",
        contracts,
        flags=re.DOTALL,
    )
    assert interface_match, "IFileSystemSafetyProviderV1 body not found"
    interface_body = interface_match.group("body")
    assert "Execute" not in interface_body, "V1 provider interface must remain non-executable"
    assert interface_body.count(";") == 3, "V1 provider interface must remain limited to three operations"
    checks += 2

    assert contracts.count("Executable: false") == 3, "all mapped future execution capabilities must remain non-executable"
    assert "Executable: true" not in contracts, "V1 must not publish an executable future capability"
    checks += 2

    for forbidden in ("FileOp.App", "FileOp.Indexer", "StorageAnalytics", "Cleanup"):
        assert forbidden not in project, f"provider project leaked product boundary: {forbidden}"
        checks += 1

    provider = source["provider"]
    required_provider = [
        "GetFileInformationByHandle",
        "GetFinalPathNameByHandleW",
        "dwDesiredAccess: 0",
        "FileReadAttributes",
        "FileCsFlagCaseSensitiveDir",
        '"$Recycle.Bin"',
        '"System Volume Information"',
        "FileSystemOperationStrategyV1.CrossVolumeMoveUnsupported",
        "FileSystemOperationStrategyV1.DirectoryOperationUnsupported",
        "FileSystemRecoveryAssessmentStatusV1.ObservedSubsetMatches",
    ]
    for needle in required_provider:
        assert needle in provider, needle
        checks += 1

    forbidden_mutation_primitives = [
        "CopyFileW(",
        "MoveFileW(",
        "MoveFileExW(",
        "DeleteFileW(",
        "ReplaceFileW(",
        "RemoveDirectoryW(",
        "SetFileInformationByHandle(",
        "WriteFile(",
    ]
    for needle in forbidden_mutation_primitives:
        assert needle not in provider, f"V1 provider imports/uses mutation primitive: {needle}"
        checks += 1

    tests = source["tests"]
    required_tests = [
        "Contract_IsVersionedBoundedAndNonExecutable",
        "OperationRequest_RejectsUnboundedEntrySets",
        "RecoveryAssessment_IsDeterministicAndFailClosed",
        "IdentityInspection_ReturnsCanonicalStableIdentityEvidence",
        "CopyCollisionAsk_ProducesDecisionEvidenceWithoutAuthority",
        "ExpectedIdentityMismatch_BlocksFreshOperationEvidence",
        "SameVolumeMove_MapsStrategyButCannotExecute",
        "PermanentDelete_ProtectedWindowsRootBlocksBeforeItemInspection",
        'DoesNotContain(references, "FileOp.Core")',
        'DoesNotContain(references, "FileOp.App")',
    ]
    for needle in required_tests:
        assert needle in tests, needle
        checks += 1

    package_readme = source["package_readme"].casefold()
    for needle in ("read-only", "never grants mutation authority", "apache-2.0"):
        assert needle in package_readme, needle
        checks += 1

    docs = source["docs"].casefold()
    required_docs = [
        "read-only and non-executable",
        "evidence only",
        "mutationauthorized == false",
        "workspace confinement",
        "opaque writer leases",
        "transactional journal",
        "unknown-outcome recovery",
        "filesystem.copy.execute.v1",
        "filesystem.move.same-volume.execute.v1",
        "filesystem.delete.permanent.execute.v1",
    ]
    for needle in required_docs:
        assert needle in docs, needle
        checks += 1

    gate = source["gate"]
    assert "verify_filesystem_safety_provider.py --repo-root $repoRoot" in gate, "offline authoritative gate must include provider boundary verifier"
    assert "tests/FileOp.FilesystemSafety.Tests/FileOp.FilesystemSafety.Tests.csproj --configuration Release" in gate, "native authoritative gate must include provider tests"
    checks += 2

    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    args = parser.parse_args()
    count = check_repository(args.repo_root.resolve())
    print(f"PASS filesystem-safety provider source boundary: {count} checks")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
