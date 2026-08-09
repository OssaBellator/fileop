#!/usr/bin/env python3
"""Zero-Actions checks for read-only Copy recovery destination inspection."""
from __future__ import annotations

import argparse
import random
import sys
from enum import IntEnum
from pathlib import Path


class CurrentState(IntEnum):
    MISSING = 0
    FILE = 1
    DIRECTORY = 2
    INACCESSIBLE = 3
    ERROR = 4


class Status(IntEnum):
    NO_VERIFIED_IDENTITY = 0
    MISSING = 1
    SAME_OBJECT = 2
    DIFFERENT_OBJECT = 3
    REDIRECTED = 4
    REPARSE_POINT = 5
    UNEXPECTED_TYPE = 6
    INACCESSIBLE = 7
    ERROR = 8


def classify(
    state: CurrentState,
    *,
    expected_identity: int | None,
    actual_identity: int | None,
    redirected: bool,
    reparse: bool,
) -> Status:
    if state == CurrentState.MISSING:
        return Status.MISSING
    if state == CurrentState.INACCESSIBLE:
        return Status.INACCESSIBLE
    if state == CurrentState.ERROR:
        return Status.ERROR
    if state == CurrentState.DIRECTORY:
        return Status.UNEXPECTED_TYPE
    if reparse:
        return Status.REPARSE_POINT
    if redirected:
        return Status.REDIRECTED
    if expected_identity is None or actual_identity is None:
        return Status.NO_VERIFIED_IDENTITY
    return (
        Status.SAME_OBJECT
        if expected_identity == actual_identity
        else Status.DIFFERENT_OBJECT
    )


def fixed_cases() -> list[tuple[dict[str, object], Status]]:
    return [
        (
            dict(
                state=CurrentState.FILE,
                expected_identity=10,
                actual_identity=10,
                redirected=False,
                reparse=False,
            ),
            Status.SAME_OBJECT,
        ),
        (
            dict(
                state=CurrentState.FILE,
                expected_identity=None,
                actual_identity=10,
                redirected=False,
                reparse=False,
            ),
            Status.NO_VERIFIED_IDENTITY,
        ),
        (
            dict(
                state=CurrentState.FILE,
                expected_identity=10,
                actual_identity=11,
                redirected=False,
                reparse=False,
            ),
            Status.DIFFERENT_OBJECT,
        ),
        (
            dict(
                state=CurrentState.FILE,
                expected_identity=10,
                actual_identity=10,
                redirected=True,
                reparse=False,
            ),
            Status.REDIRECTED,
        ),
        (
            dict(
                state=CurrentState.FILE,
                expected_identity=10,
                actual_identity=10,
                redirected=True,
                reparse=True,
            ),
            Status.REPARSE_POINT,
        ),
        (
            dict(
                state=CurrentState.MISSING,
                expected_identity=10,
                actual_identity=None,
                redirected=False,
                reparse=False,
            ),
            Status.MISSING,
        ),
        (
            dict(
                state=CurrentState.DIRECTORY,
                expected_identity=10,
                actual_identity=10,
                redirected=False,
                reparse=False,
            ),
            Status.UNEXPECTED_TYPE,
        ),
        (
            dict(
                state=CurrentState.INACCESSIBLE,
                expected_identity=10,
                actual_identity=None,
                redirected=False,
                reparse=False,
            ),
            Status.INACCESSIBLE,
        ),
        (
            dict(
                state=CurrentState.ERROR,
                expected_identity=10,
                actual_identity=None,
                redirected=False,
                reparse=False,
            ),
            Status.ERROR,
        ),
    ]


def run_model(cases: int) -> int:
    checks = 0
    for arguments, expected in fixed_cases():
        assert classify(**arguments) == expected
        checks += 1

    rng = random.Random(20260809)
    states = tuple(CurrentState)
    for _ in range(cases):
        state = rng.choice(states)
        expected_identity = None if rng.random() < 0.35 else rng.randrange(1, 1 << 20)
        identity_mode = rng.randrange(3)
        if identity_mode == 0:
            actual_identity = None
        elif identity_mode == 1 and expected_identity is not None:
            actual_identity = expected_identity
        else:
            actual_identity = rng.randrange(1, 1 << 20)
            if expected_identity is not None and actual_identity == expected_identity:
                actual_identity += 1
        redirected = rng.random() < 0.2
        reparse = rng.random() < 0.1

        status = classify(
            state,
            expected_identity=expected_identity,
            actual_identity=actual_identity,
            redirected=redirected,
            reparse=reparse,
        )

        if status == Status.SAME_OBJECT:
            assert state == CurrentState.FILE
            assert not redirected
            assert not reparse
            assert expected_identity is not None
            assert actual_identity == expected_identity
            checks += 5
        if expected_identity is None and state == CurrentState.FILE and not redirected and not reparse:
            assert status == Status.NO_VERIFIED_IDENTITY
            checks += 1
        if state == CurrentState.FILE and reparse:
            assert status == Status.REPARSE_POINT
            checks += 1
        elif state == CurrentState.FILE and redirected:
            assert status == Status.REDIRECTED
            checks += 1
        if state == CurrentState.MISSING:
            assert status == Status.MISSING
            checks += 1
        elif state == CurrentState.DIRECTORY:
            assert status == Status.UNEXPECTED_TYPE
            checks += 1
        elif state == CurrentState.INACCESSIBLE:
            assert status == Status.INACCESSIBLE
            checks += 1
        elif state == CurrentState.ERROR:
            assert status == Status.ERROR
            checks += 1
        checks += 1
    return checks


def check_repository(root: Path) -> int:
    paths = {
        "core": root / "src/FileOp.Core/Operations/FileOperationRecoveryInspection.cs",
        "resolver": root / "src/FileOp.Windows/Operations/WindowsFileOperationExecutionValidator.cs",
        "tests": root / "tests/FileOp.Windows.Tests/FileOperationRecoveryInspectionTests.cs",
        "docs": root / "docs/file-operation-recovery-inspection.md",
        "python_wrapper": root / "tools/test-copy-executor-local.py",
        "powershell_wrapper": root / "tools/test-copy-executor-local.ps1",
    }
    missing = [str(path) for path in paths.values() if not path.is_file()]
    if missing:
        raise FileNotFoundError(", ".join(missing))
    source = {name: path.read_text(encoding="utf-8") for name, path in paths.items()}

    required_core = (
        "public enum FileOperationRecoveryDestinationStatus",
        "NoVerifiedIdentity",
        "SameObject",
        "DifferentObject",
        "Redirected",
        "ReparsePoint",
        "UnexpectedType",
        "public sealed class FileOperationRecoveryInspector",
        "IFileOperationCanonicalPathResolver",
        "allowMissingLeaf: true",
        "entry.DestinationIdentity is not FileIdentity expected",
        "current.Identity is not FileIdentity actual",
        "Identity equality is evidence only",
        "Array.AsReadOnly(items.ToArray())",
    )
    for needle in required_core:
        assert needle in source["core"], needle

    forbidden_core = (
        "File.Copy(",
        "File.Move(",
        "File.Delete(",
        "Directory.Move(",
        "Directory.Delete(",
        "File.OpenWrite(",
        "new FileStream(",
        "DeleteCreatedDestination",
        "CanDelete",
        "CanUndo",
    )
    for needle in forbidden_core:
        assert needle not in source["core"], needle

    required_resolver = (
        "public sealed class WindowsFileOperationCanonicalPathResolver",
        "dwDesiredAccess: 0",
        "GetFinalPathNameByHandleW",
        "GetFileInformationByHandle",
    )
    for needle in required_resolver:
        assert needle in source["resolver"], needle

    for test_name in (
        "SameIdentityAtCanonicalPathIsEvidenceButNotUndoAuthority",
        "RecoveryInspectionClassifiesUnsafeAndChangedDestinationsConservatively",
        "ExistingDestinationWithoutDurableIdentityCannotBeReportedAsSameObject",
        "InspectorSkipsSettledEntriesAndSnapshotsResults",
    ):
        assert test_name in source["tests"], test_name

    for needle in (
        "read-only",
        "identity evidence",
        "not deletion authorization",
        "no-user-change",
    ):
        assert needle.casefold() in source["docs"].casefold(), needle

    verifier_name = "verify_file_operation_recovery_inspection.py"
    assert verifier_name in source["python_wrapper"]
    assert verifier_name in source["powershell_wrapper"]

    return (
        len(required_core)
        + len(forbidden_core)
        + len(required_resolver)
        + 4
        + 4
        + 2
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--self-test-only", action="store_true")
    parser.add_argument("--cases", type=int, default=50000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error("--cases must be greater than zero")

    checks = run_model(args.cases)
    print(
        f"PASS recovery inspection model: {checks} checks across "
        f"{args.cases} randomized cases + {len(fixed_cases())} fixed cases"
    )
    if not args.self_test_only:
        print(
            "PASS recovery inspection source wiring: "
            f"{check_repository(args.repo_root.resolve())} checks"
        )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
