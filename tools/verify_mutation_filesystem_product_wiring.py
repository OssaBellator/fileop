#!/usr/bin/env python3
"""Pin FileOp.App mutation call sites to the #193 NTFS-guarded validator aliases."""
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
    )

    checks += require(copy, "new WindowsFileOperationExecutionValidator()")
    assert move.count("new WindowsMoveOperationExecutionValidator()") >= 2
    checks += 1
    checks += require(
        delete,
        "private readonly IFileDeleteOperationExecutionValidator _deleteExecutionValidator =",
        "new WindowsFileDeleteOperationExecutionValidator();",
    )

    combined = copy + move + delete
    checks += reject(
        combined,
        "new FileOp.Windows.Operations.WindowsFileOperationExecutionValidator",
        "new global::FileOp.Windows.Operations.WindowsFileOperationExecutionValidator",
        "new FileOp.Windows.Operations.WindowsMoveOperationExecutionValidator",
        "new global::FileOp.Windows.Operations.WindowsMoveOperationExecutionValidator",
        "new FileOp.Windows.Operations.WindowsFileDeleteOperationExecutionValidator",
        "new global::FileOp.Windows.Operations.WindowsFileDeleteOperationExecutionValidator",
        "using WindowsFileOperationExecutionValidator =",
        "using WindowsMoveOperationExecutionValidator =",
        "using WindowsFileDeleteOperationExecutionValidator =",
    )

    # A project-wide alias is intentionally used because these are product mutation-policy
    # names. Pin its blast radius: no other App source may start using one of the aliased
    # lower-level validator names without updating this reviewed boundary explicitly.
    expected_usage = {
        "WindowsFileOperationExecutionValidator": "FilesView.Copy.cs",
        "WindowsMoveOperationExecutionValidator": "FilesView.Move.cs",
        "WindowsFileDeleteOperationExecutionValidator": "FilesView.Delete.cs",
    }
    observed_counts = {name: 0 for name in expected_usage}
    app_sources = sorted(app_root.glob("*.cs"))
    if not app_sources:
        raise FileNotFoundError("No FileOp.App C# sources found")
    for path in app_sources:
        if path == aliases_path:
            continue
        text = path.read_text(encoding="utf-8")
        for type_name, expected_file in expected_usage.items():
            count = text.count(type_name)
            if count == 0:
                continue
            assert path.name == expected_file, (
                f"Unexpected App use of {type_name} in {path.name}; "
                f"the #193 alias boundary currently permits only {expected_file}"
            )
            observed_counts[type_name] += count
            checks += count

    assert observed_counts["WindowsFileOperationExecutionValidator"] == 1
    assert observed_counts["WindowsMoveOperationExecutionValidator"] >= 2
    assert observed_counts["WindowsFileDeleteOperationExecutionValidator"] == 1
    checks += 3

    checks += require(
        gate,
        "verify_mutation_filesystem_identity_boundary.py",
        "verify_mutation_filesystem_product_wiring.py",
        "--cases 50000",
        "WindowsMutationFilesystemCapabilityBoundaryTests",
        "WindowsMutationFilesystemCapabilityBindingTests",
        "WindowsMutationFilesystemCapabilityPolicyTests",
        'FullyQualifiedName~WindowsMutationFilesystemCapability',
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
