#!/usr/bin/env python3
"""Zero-Actions checks for FileOp's canonical execution-validation boundary."""
from __future__ import annotations

import argparse
import ntpath
import random
import sys
from dataclasses import dataclass
from enum import Enum, auto
from pathlib import Path


class State(Enum):
    MISSING = auto()
    FILE = auto()
    DIRECTORY = auto()
    INACCESSIBLE = auto()
    ERROR = auto()


class Policy(Enum):
    ASK = auto()
    SKIP = auto()
    STOP = auto()


class Decision(Enum):
    READY = auto()
    SKIP = auto()
    NEEDS_DECISION = auto()
    BLOCKED = auto()


@dataclass(frozen=True)
class Resolved:
    requested: str
    canonical: str
    state: State
    reparse_leaf: bool = False
    identity: tuple[int, int] | None = None


def norm(path: str) -> str:
    value = ntpath.normpath(path.replace("/", "\\"))
    if len(value) == 2 and value[1] == ":":
        value += "\\"
    return value.casefold()


def within(candidate: str, root: str) -> bool:
    c, r = norm(candidate), norm(root)
    return c == r or c.startswith(r if r.endswith("\\") else r + "\\")


def validate(
    source_root: Resolved,
    destination_root: Resolved,
    source: Resolved,
    destination: Resolved,
    is_directory: bool,
    policy: Policy,
) -> Decision:
    if source_root.state is not State.DIRECTORY or destination_root.state is not State.DIRECTORY:
        return Decision.BLOCKED
    if source_root.reparse_leaf or destination_root.reparse_leaf:
        return Decision.BLOCKED
    if norm(source_root.canonical) == norm(destination_root.canonical):
        return Decision.BLOCKED
    if (
        source_root.identity is not None
        and destination_root.identity is not None
        and source_root.identity == destination_root.identity
    ):
        return Decision.BLOCKED

    if source.state is not (State.DIRECTORY if is_directory else State.FILE):
        return Decision.BLOCKED
    if source.reparse_leaf:
        return Decision.BLOCKED
    if norm(ntpath.dirname(source.canonical)) != norm(source_root.canonical):
        return Decision.BLOCKED

    if destination.state in {State.INACCESSIBLE, State.ERROR} or destination.reparse_leaf:
        return Decision.BLOCKED
    if norm(ntpath.dirname(destination.canonical)) != norm(destination_root.canonical):
        return Decision.BLOCKED

    if is_directory and within(destination_root.canonical, source.canonical):
        return Decision.BLOCKED

    if (
        source.identity is not None
        and destination.identity is not None
        and source.identity == destination.identity
    ):
        return Decision.BLOCKED

    if destination.state is State.MISSING:
        return Decision.READY
    if destination.state not in {State.FILE, State.DIRECTORY}:
        return Decision.BLOCKED
    if policy is Policy.ASK:
        return Decision.NEEDS_DECISION
    if policy is Policy.SKIP:
        return Decision.SKIP
    return Decision.BLOCKED


def check_properties(cases: int) -> int:
    src_root = Resolved(r"C:\Source", r"C:\Real\Source", State.DIRECTORY, identity=(1, 10))
    dst_root = Resolved(r"D:\Destination", r"D:\Real\Destination", State.DIRECTORY, identity=(2, 20))
    source = Resolved(r"C:\Source\a.txt", r"C:\Real\Source\a.txt", State.FILE, identity=(1, 100))
    missing = Resolved(r"D:\Destination\a.txt", r"D:\Real\Destination\a.txt", State.MISSING)
    existing = Resolved(r"D:\Destination\a.txt", r"D:\Real\Destination\a.txt", State.FILE, identity=(2, 200))

    assert validate(src_root, dst_root, source, missing, False, Policy.ASK) is Decision.READY
    assert validate(src_root, dst_root, source, existing, False, Policy.ASK) is Decision.NEEDS_DECISION
    assert validate(src_root, dst_root, source, existing, False, Policy.SKIP) is Decision.SKIP
    assert validate(src_root, dst_root, source, existing, False, Policy.STOP) is Decision.BLOCKED

    aliased_root = Resolved(r"X:\Alias", r"C:\Real\Source", State.DIRECTORY, identity=(1, 10))
    assert validate(src_root, aliased_root, source, missing, False, Policy.ASK) is Decision.BLOCKED

    escaped = Resolved(r"C:\Source\a.txt", r"E:\Escaped\a.txt", State.FILE, identity=(3, 300))
    assert validate(src_root, dst_root, escaped, missing, False, Policy.ASK) is Decision.BLOCKED

    same_object = Resolved(r"D:\Destination\a.txt", r"D:\Real\Destination\a.txt", State.FILE, identity=(1, 100))
    assert validate(src_root, dst_root, source, same_object, False, Policy.SKIP) is Decision.BLOCKED

    directory_source = Resolved(r"C:\Source\Folder", r"C:\Real\Source\Folder", State.DIRECTORY, identity=(1, 400))
    nested_root = Resolved(r"D:\Alias", r"C:\Real\Source\Folder\Nested", State.DIRECTORY, identity=(1, 401))
    nested_destination = Resolved(r"D:\Alias\Folder", r"C:\Real\Source\Folder\Nested\Folder", State.MISSING)
    assert validate(src_root, nested_root, directory_source, nested_destination, True, Policy.ASK) is Decision.BLOCKED

    reparse_source = Resolved(source.requested, source.canonical, source.state, reparse_leaf=True, identity=source.identity)
    assert validate(src_root, dst_root, reparse_source, missing, False, Policy.ASK) is Decision.BLOCKED

    checks = 9
    rng = random.Random(20260808)
    policies = list(Policy)
    destination_states = [State.MISSING, State.FILE, State.DIRECTORY, State.INACCESSIBLE, State.ERROR]

    for case in range(cases):
        source_canonical_root = rf"C:\Real\S{case % 101}"
        destination_canonical_root = rf"D:\Real\D{case % 97}"
        source_root = Resolved(rf"C:\Alias\S{case % 101}", source_canonical_root, State.DIRECTORY, identity=(1, case % 101 + 1))
        destination_root = Resolved(rf"D:\Alias\D{case % 97}", destination_canonical_root, State.DIRECTORY, identity=(2, case % 97 + 1))
        is_directory = rng.random() < 0.25
        name = f"entry-{case}" + ("" if is_directory else ".dat")
        source_state = State.DIRECTORY if is_directory else State.FILE
        source = Resolved(
            source_root.requested + "\\" + name,
            source_canonical_root + "\\" + name,
            source_state,
            reparse_leaf=rng.random() < 0.02,
            identity=(1, 100000 + case),
        )
        destination_state = rng.choice(destination_states)
        destination_identity = (
            source.identity
            if destination_state in {State.FILE, State.DIRECTORY} and rng.random() < 0.01
            else ((2, 200000 + case) if destination_state in {State.FILE, State.DIRECTORY} else None)
        )
        destination = Resolved(
            destination_root.requested + "\\" + name,
            destination_canonical_root + "\\" + name,
            destination_state,
            reparse_leaf=rng.random() < 0.02,
            identity=destination_identity,
        )
        policy = rng.choice(policies)

        result = validate(source_root, destination_root, source, destination, is_directory, policy)

        if source.reparse_leaf or destination.reparse_leaf:
            assert result is Decision.BLOCKED
        elif destination.identity is not None and destination.identity == source.identity:
            assert result is Decision.BLOCKED
        elif destination.state is State.MISSING:
            assert result is Decision.READY
        elif destination.state in {State.INACCESSIBLE, State.ERROR}:
            assert result is Decision.BLOCKED
        elif policy is Policy.ASK:
            assert result is Decision.NEEDS_DECISION
        elif policy is Policy.SKIP:
            assert result is Decision.SKIP
        else:
            assert result is Decision.BLOCKED
        checks += 1

        escaped_source = Resolved(source.requested, rf"E:\Escape\{name}", source.state, identity=source.identity)
        assert validate(source_root, destination_root, escaped_source, destination, is_directory, policy) is Decision.BLOCKED
        checks += 1

        alias_destination_root = Resolved(destination_root.requested, source_root.canonical, State.DIRECTORY, identity=source_root.identity)
        assert validate(source_root, alias_destination_root, source, destination, is_directory, policy) is Decision.BLOCKED
        checks += 1

    return checks


def check_repository(root: Path) -> int:
    paths = {
        "core": root / "src/FileOp.Core/Operations/FileOperationExecutionValidation.cs",
        "windows": root / "src/FileOp.Windows/Operations/WindowsFileOperationExecutionValidator.cs",
        "execution": root / "src/FileOp.Core/Operations/FileOperationExecution.cs",
        "preflight": root / "src/FileOp.Windows/Operations/WindowsFileOperationPreflightValidator.cs",
        "docs": root / "docs/files-browser.md",
        "local": root / "tools/test-local.ps1",
    }
    missing = [str(path) for path in paths.values() if not path.is_file()]
    if missing:
        raise FileNotFoundError(", ".join(missing))
    source = {name: path.read_text(encoding="utf-8") for name, path in paths.items()}

    required_core = [
        "public enum FileOperationCanonicalPathState",
        "public sealed record FileOperationCanonicalPath(",
        "FileIdentity? Identity",
        "public interface IFileOperationCanonicalPathResolver",
        "public enum FileOperationExecutionValidationDecision",
        "public sealed record FileOperationExecutionValidationItem(",
        "public sealed record FileOperationExecutionValidationResult",
        "Items = Array.AsReadOnly(items.ToArray());",
        "public bool CanBeginMutation",
        "public interface IFileOperationExecutionValidator",
    ]
    for needle in required_core:
        assert needle in source["core"], needle

    required_windows = [
        "public sealed class WindowsFileOperationCanonicalPathResolver",
        "CreateFileW(",
        "dwDesiredAccess: 0",
        "FileShare.ReadWrite | FileShare.Delete",
        "GetFinalPathNameByHandleW(",
        "GetFileInformationByHandle(",
        "NormalizeFinalPath(",
        "allowMissingLeaf: true",
        "source.Identity is FileIdentity sourceIdentity",
        "destination.Identity is FileIdentity destinationIdentity",
        "The source resolves outside the canonical source directory.",
        "The destination resolves outside the canonical destination directory.",
        "The source and destination resolve to the same filesystem object.",
        "public sealed class WindowsFileOperationExecutionValidator",
    ]
    for needle in required_windows:
        assert needle in source["windows"], needle

    combined = source["core"] + source["windows"]
    for forbidden in [
        "File.Copy(", "File.Move(", "File.Delete(",
        "Directory.Move(", "Directory.Delete(",
        "File.Create(", "File.Write", "File.OpenWrite(",
    ]:
        assert forbidden not in combined, forbidden

    assert "public interface IFileOperationExecutor" in source["execution"]
    assert "WindowsFileOperationExecutionValidator : IFileOperationExecutor" not in source["windows"]
    assert "File.GetAttributes(normalized)" in source["preflight"]
    assert "## Canonical execution validation" in source["docs"]
    assert "handle-resolved" in source["docs"]
    assert "verify_file_operation_execution_validation.py" in source["local"]

    return len(required_core) + len(required_windows) + 8 + 6


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--self-test-only", action="store_true")
    parser.add_argument("--cases", type=int, default=50000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error("--cases must be greater than zero")

    print(f"PASS canonical execution validation properties: {check_properties(args.cases)} checks")
    if not args.self_test_only:
        print(
            "PASS canonical execution validation source wiring: "
            f"{check_repository(args.repo_root.resolve())} checks"
        )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
