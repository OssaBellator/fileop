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
    aliases = read(root, "src/FileOp.App/MutationExecutionValidatorAliases.cs")
    copy = read(root, "src/FileOp.App/FilesView.Copy.cs")
    move = read(root, "src/FileOp.App/FilesView.Move.cs")
    delete = read(root, "src/FileOp.App/FilesView.Delete.cs")

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
