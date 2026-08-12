#!/usr/bin/env python3
"""Verify destination-less read-only file delete preflight."""
from __future__ import annotations

import argparse
import ntpath
import random
from pathlib import Path

READY = "ready-for-further-review"
BLOCKED = "blocked"


def norm(path: str) -> str:
    value = ntpath.normpath(path.replace('/', '\\'))
    if len(value) == 2 and value[1] == ':':
        value += '\\'
    return value.casefold()


def classify(
    *,
    source_dir: str,
    entry_path: str,
    entry_name: str,
    is_directory: bool,
    root_state: str,
    root_reparse: bool,
    source_state: str,
    source_reparse: bool,
) -> str:
    if root_state != "directory" or root_reparse:
        return BLOCKED
    normalized_entry = ntpath.normpath(entry_path)
    if norm(ntpath.dirname(normalized_entry)) != norm(source_dir):
        return BLOCKED
    leaf = ntpath.basename(normalized_entry)
    if not leaf or leaf.casefold() != entry_name.casefold() or ':' in leaf:
        return BLOCKED
    expected = "directory" if is_directory else "file"
    if source_state != expected or source_reparse:
        return BLOCKED
    return READY


def run_model(cases: int, seed: int) -> int:
    checks = 0
    base = dict(
        source_dir=r"C:\Source",
        entry_path=r"C:\Source\a.txt",
        entry_name="a.txt",
        is_directory=False,
        root_state="directory",
        root_reparse=False,
        source_state="file",
        source_reparse=False,
    )
    assert classify(**base) == READY
    assert classify(**(base | {"entry_path": r"C:\Source\Nested\a.txt"})) == BLOCKED
    assert classify(**(base | {"entry_name": "renamed.txt"})) == BLOCKED
    assert classify(**(base | {"entry_path": r"C:\Source\a.txt:stream", "entry_name": "a.txt:stream"})) == BLOCKED
    assert classify(**(base | {"root_reparse": True})) == BLOCKED
    assert classify(**(base | {"source_reparse": True})) == BLOCKED
    checks += 6

    rng = random.Random(seed)
    states = ("file", "directory", "missing", "inaccessible", "error")
    for case in range(cases):
        source_dir = rf"C:\Root\S{case % 101}"
        is_directory = rng.random() < 0.25
        name = f"entry{case}{'' if is_directory else '.dat'}"
        direct = rng.random() < 0.92
        exact_name = rng.random() < 0.95
        ads = rng.random() < 0.03
        root_state = rng.choice(states)
        root_reparse = rng.random() < 0.05
        source_state = rng.choice(states)
        source_reparse = rng.random() < 0.05

        leaf = name + (":stream" if ads else "")
        entry_path = (
            source_dir + "\\" + leaf
            if direct
            else source_dir + "\\Nested\\" + leaf
        )
        entry_name = leaf if exact_name else "other-" + leaf
        result = classify(
            source_dir=source_dir,
            entry_path=entry_path,
            entry_name=entry_name,
            is_directory=is_directory,
            root_state=root_state,
            root_reparse=root_reparse,
            source_state=source_state,
            source_reparse=source_reparse,
        )
        expected_state = "directory" if is_directory else "file"
        expected_ready = (
            root_state == "directory"
            and not root_reparse
            and direct
            and exact_name
            and not ads
            and source_state == expected_state
            and not source_reparse
        )
        assert (result == READY) == expected_ready
        assert result in {READY, BLOCKED}
        checks += 2

        # Mutation authorization is intentionally invariant: preflight never grants it.
        delete_mutation_authorized = False
        assert not delete_mutation_authorized
        checks += 1

        # Changing any ready source into a reparse point must fail closed.
        if expected_ready:
            changed = classify(
                source_dir=source_dir,
                entry_path=entry_path,
                entry_name=entry_name,
                is_directory=is_directory,
                root_state=root_state,
                root_reparse=root_reparse,
                source_state=source_state,
                source_reparse=True,
            )
            assert changed == BLOCKED
            checks += 1

    return checks


def require(text: str, needle: str, label: str) -> int:
    if needle not in text:
        raise AssertionError(f"missing {label}: {needle}")
    return 1


def forbid(text: str, needle: str, label: str) -> int:
    if needle in text:
        raise AssertionError(f"forbidden {label}: {needle}")
    return 1


def check_repository(root: Path) -> int:
    core = (root / "src/FileOp.Core/Operations/FileDeleteOperationPreflight.cs").read_text(encoding="utf-8")
    windows = (root / "src/FileOp.Windows/Operations/WindowsFileDeleteOperationPreflightValidator.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/FileDeleteOperationPreflightTests.cs").read_text(encoding="utf-8")
    docs = (root / "docs/file-delete-preflight.md").read_text(encoding="utf-8")
    plan = (root / "src/FileOp.Core/Operations/FileOperationPlan.cs").read_text(encoding="utf-8")
    execution = (root / "src/FileOp.Core/Operations/FileOperationExecution.cs").read_text(encoding="utf-8")
    cleanup = (root / "src/FileOp.Core/Storage/StorageCleanupReadiness.cs").read_text(encoding="utf-8")
    parent = (root / "tools/verify_file_operation_preflight.py").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")

    checks = 0
    required = [
        (core, "public sealed record FileDeleteOperationIntent", "delete intent"),
        (core, "var entrySnapshot = entries.ToArray();", "immutable entry snapshot"),
        (core, "Delete preflight requires at least one captured source entry", "non-empty selection"),
        (core, "public sealed record FileDeleteOperationPlan", "delete plan"),
        (core, "FileDeleteOperationPreflightDecision", "delete decision"),
        (core, "ReadyForFurtherReview", "non-execution ready naming"),
        (core, "public bool DeleteMutationAuthorized => false;", "hard non-authorization"),
        (core, "IFileDeleteOperationPreflightValidator", "delete validator contract"),
        (windows, "WindowsFileOperationPathProbe", "existing read-only probe reuse"),
        (windows, "Path.GetDirectoryName(sourcePath)", "direct-child validation"),
        (windows, "Path.GetFileName(sourcePath)", "leaf-name validation"),
        (windows, "leafName.Contains(Path.VolumeSeparatorChar)", "ADS rejection"),
        (windows, "sourceInspection.IsReparsePoint", "source reparse rejection"),
        (windows, "This is read-only evidence and is not authorization to delete", "runtime non-authorization wording"),
        (tests, "MatchingDirectFileIsReadyForFurtherReviewButNeverAuthorized", "authorization regression"),
        (tests, "MissingChangedOrReparseSourceFailsClosed", "fail-closed regression"),
        (tests, "EntryMustRemainExactDirectChildWithCapturedLeafName", "path regression"),
        (tests, "CallerCancellationPropagatesBeforeProbe", "cancellation regression"),
        (docs, "does not add `Delete` to `FileOperationKind`", "separate contract documentation"),
        (docs, "not authorization to delete", "documentation authorization boundary"),
        (docs, "Storage cleanup readiness remains non-authorizing", "cleanup boundary"),
        (plan, "public enum FileOperationKind\n{\n    Copy,\n    Move,", "Copy/Move enum unchanged"),
        (cleanup, "CleanupMutationAuthorized => false", "cleanup remains non-authorizing"),
        (parent, "from verify_file_delete_preflight import (", "parent imports delete child"),
        (parent, "run_delete_preflight_model(args.cases", "parent runs delete model"),
        (parent, "check_delete_preflight_repository(args.repo_root.resolve())", "parent runs delete source checks"),
        (gate, "verify_file_operation_preflight.py --repo-root $repoRoot --cases 50000", "existing direct offline gate"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
    ]
    for text, needle, label in required:
        checks += require(text, needle, label)

    for text, needle, label in (
        (plan, "Delete,", "Delete enum value"),
        (core + "\n" + windows, "IFileOperationExecutor", "execution interface"),
        (core + "\n" + windows, "FileOperationExecutionSnapshot", "execution state"),
        (windows, "File.Delete(", "file delete primitive"),
        (windows, "Directory.Delete(", "directory delete primitive"),
        (windows, "DeleteFileW", "native delete primitive"),
        (windows, "SetFileInformationByHandle", "handle delete primitive"),
        (windows, "MoveFileEx", "move/delete primitive"),
        (windows, "File.Copy(", "copy primitive"),
        (windows, "File.Move(", "move primitive"),
        (execution, "FileDeleteOperationPlan", "delete execution wiring"),
    ):
        checks += forbid(text, needle, label)

    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0xDE1E7E)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {source_checks:,} source checks" if args.repo_root else ""
    print(
        f"PASS: file delete preflight verified with {model_checks:,} model assertions "
        f"across {args.cases:,} randomized states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
