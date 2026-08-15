#!/usr/bin/env python3
"""Pin FileOp.App mutation call sites to the #193 NTFS-guarded aliases."""
from __future__ import annotations

import argparse
import sys
from pathlib import Path


def read(root: Path, relative: str) -> str:
    path = root / relative
    if not path.is_file():
        raise FileNotFoundError(path)
    return path.read_text(encoding="utf-8")


def require(text: str, *needles: str) -> int:
    for needle in needles:
        assert needle in text, needle
    return len(needles)


def reject(text: str, *needles: str) -> int:
    for needle in needles:
        assert needle not in text, needle
    return len(needles)


def check(root: Path) -> int:
    app_root = root / "src/FileOp.App"
    aliases_path = app_root / "MutationExecutionValidatorAliases.cs"
    if not aliases_path.is_file():
        raise FileNotFoundError(aliases_path)

    aliases = aliases_path.read_text(encoding="utf-8")
    app_project = read(root, "src/FileOp.App/FileOp.App.csproj")
    copy = read(root, "src/FileOp.App/FilesView.Copy.cs")
    move = read(root, "src/FileOp.App/FilesView.Move.cs")
    delete = read(root, "src/FileOp.App/FilesView.Delete.cs")
    gate = read(root, "tools/test-mutation-filesystem-identity.ps1")
    authoritative = read(root, "tools/verify_file_operation_execution_validation.py")

    checks = 0
    checks += require(
        aliases,
        "global using WindowsFileOperationExecutionValidator =",
        "FileOp.Windows.Operations.WindowsNtfsMutationExecutionValidator",
        "global using WindowsMoveOperationExecutionValidator =",
        "FileOp.Windows.Operations.WindowsNtfsMoveOperationExecutionValidator",
        "global using WindowsFileDeleteOperationExecutionValidator =",
        "FileOp.Windows.Operations.WindowsNtfsFileDeleteOperationExecutionValidator",
        "global using WindowsFileCopyMutationPrimitive =",
        "FileOp.Windows.Operations.WindowsNtfsFileCopyMutationPrimitive",
        "global using WindowsFileSameVolumeMoveMutationPrimitive =",
        "FileOp.Windows.Operations.WindowsNtfsFileSameVolumeMoveMutationPrimitive",
        "global using WindowsFileDeleteOperationFinalMutationLeaseProvider =",
        "FileOp.Windows.Operations.WindowsNtfsFileDeleteOperationFinalMutationLeaseProvider",
    )

    # The alias source relies on SDK default compile items. Pin the project contract so the
    # policy cannot disappear from production merely through an MSBuild item change.
    checks += require(
        app_project,
        '<Project Sdk="Microsoft.NET.Sdk">',
        "<UseWinUI>true</UseWinUI>",
        '<ProjectReference Include="..\\FileOp.Windows\\FileOp.Windows.csproj" />',
    )
    checks += reject(
        app_project,
        "<EnableDefaultCompileItems>false</EnableDefaultCompileItems>",
        '<Compile Remove="MutationExecutionValidatorAliases.cs"',
        '<Compile Remove="**\\*.cs"',
    )

    checks += require(
        copy,
        "new WindowsFileOperationExecutionValidator()",
        "new WindowsFileCopyMutationPrimitive()",
    )
    assert move.count("new WindowsMoveOperationExecutionValidator()") >= 2
    checks += 1
    checks += require(move, "new WindowsFileSameVolumeMoveMutationPrimitive()")
    checks += require(
        delete,
        "private readonly IFileDeleteOperationExecutionValidator _deleteExecutionValidator =",
        "new WindowsFileDeleteOperationExecutionValidator();",
        "new WindowsFileDeleteOperationFinalMutationLeaseProvider()",
    )

    guarded_names = (
        "WindowsFileOperationExecutionValidator",
        "WindowsMoveOperationExecutionValidator",
        "WindowsFileDeleteOperationExecutionValidator",
        "WindowsFileCopyMutationPrimitive",
        "WindowsFileSameVolumeMoveMutationPrimitive",
        "WindowsFileDeleteOperationFinalMutationLeaseProvider",
    )
    combined = copy + move + delete
    forbidden = []
    for name in guarded_names:
        forbidden.extend(
            (
                f"new FileOp.Windows.Operations.{name}",
                f"new global::FileOp.Windows.Operations.{name}",
                f"using {name} =",
            )
        )
    checks += reject(combined, *forbidden)

    # A project-wide alias is intentionally used because these are product mutation-policy
    # names. Pin its blast radius recursively across source directories while ignoring build
    # output generated under bin/obj. Exact relative paths are required so a nested file that
    # merely reuses an allowed basename cannot bypass the review boundary.
    #
    # The reviewed Copy primitive is also intentionally allowed in FilesView.Move.cs. Draft
    # #185 composes the dormant cross-volume engine from that same primitive; after #194 is
    # merged the App alias makes that composition resolve to WindowsNtfsFileCopyMutationPrimitive.
    # Keeping this path allowed avoids making #193's offline policy verifier reject the safe,
    # product-blocked #185 integration merely because it reuses the guarded Copy provider.
    allowed_usage = {
        "WindowsFileOperationExecutionValidator": {Path("FilesView.Copy.cs")},
        "WindowsMoveOperationExecutionValidator": {Path("FilesView.Move.cs")},
        "WindowsFileDeleteOperationExecutionValidator": {Path("FilesView.Delete.cs")},
        "WindowsFileCopyMutationPrimitive": {
            Path("FilesView.Copy.cs"),
            Path("FilesView.Move.cs"),
        },
        "WindowsFileSameVolumeMoveMutationPrimitive": {Path("FilesView.Move.cs")},
        "WindowsFileDeleteOperationFinalMutationLeaseProvider": {Path("FilesView.Delete.cs")},
    }
    observed_counts = {name: 0 for name in allowed_usage}
    app_sources = sorted(
        path
        for path in app_root.rglob("*.cs")
        if not ({"bin", "obj"} & set(path.relative_to(app_root).parts))
    )
    if not app_sources:
        raise FileNotFoundError("No FileOp.App C# sources found")
    for path in app_sources:
        if path == aliases_path:
            continue
        relative_path = path.relative_to(app_root)
        text = path.read_text(encoding="utf-8")
        for type_name, allowed_paths in allowed_usage.items():
            count = text.count(type_name)
            if count == 0:
                continue
            assert relative_path in allowed_paths, (
                f"Unexpected App use of {type_name} in {relative_path}; "
                f"the #193 alias boundary currently permits only "
                f"{', '.join(str(candidate) for candidate in sorted(allowed_paths))}"
            )
            observed_counts[type_name] += count
            checks += count

    assert observed_counts["WindowsFileOperationExecutionValidator"] == 1
    assert observed_counts["WindowsMoveOperationExecutionValidator"] >= 2
    assert observed_counts["WindowsFileDeleteOperationExecutionValidator"] == 1
    assert observed_counts["WindowsFileCopyMutationPrimitive"] >= 1
    assert observed_counts["WindowsFileSameVolumeMoveMutationPrimitive"] == 1
    assert observed_counts["WindowsFileDeleteOperationFinalMutationLeaseProvider"] == 1
    checks += 6

    checks += require(
        gate,
        "verify_mutation_filesystem_identity_boundary.py",
        "verify_mutation_filesystem_product_wiring.py",
        "--cases 50000",
        "WindowsMutationFilesystemCapabilityBoundaryTests",
        "WindowsMutationFilesystemCapabilityBindingTests",
        "WindowsMutationFilesystemCapabilityPolicyTests",
        "WindowsMutationFilesystemCapabilityPrimitiveGuardTests",
        'FullyQualifiedName~WindowsMutationFilesystemCapability',
        '$indexerProject = Join-Path $repoRoot "src\\FileOp.Indexer\\FileOp.Indexer.csproj"',
        '$appProject = Join-Path $repoRoot "src\\FileOp.App\\FileOp.App.csproj"',
        "dotnet build $indexerProject",
        "dotnet build $appProject",
        "-p:Platform=x64",
        "App product-wiring build failed",
    )

    # The narrower #193 gate must not be the only place this policy is checked. Pin the
    # repository-authoritative offline path as well: test-local already invokes this canonical
    # execution-validation verifier, which must continue importing and running both #193
    # source/model and product-wiring checks.
    checks += require(
        authoritative,
        "from verify_mutation_filesystem_identity_boundary import (",
        "check_repository as check_mutation_filesystem_repository",
        "run_model as run_mutation_filesystem_model",
        "from verify_mutation_filesystem_product_wiring import (",
        "check as check_mutation_product_wiring",
        "mutation_filesystem_checks = run_mutation_filesystem_model(args.cases)",
        "mutation_filesystem_source = check_mutation_filesystem_repository(repo_root)",
        "mutation_product_wiring = check_mutation_product_wiring(repo_root)",
    )

    print(f"PASS mutation filesystem product wiring ({checks} checks)")
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path(__file__).resolve().parents[1])
    args = parser.parse_args()
    check(args.repo_root.resolve())
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
