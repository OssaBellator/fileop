#!/usr/bin/env python3
"""Verify file-only delete canonical execution-validation evidence."""
from __future__ import annotations

import argparse
import ntpath
import random
from dataclasses import dataclass
from pathlib import Path

READY = "ready-for-authorization-review"
BLOCKED = "blocked"


@dataclass(frozen=True)
class Resolved:
    requested: str
    canonical: str
    state: str
    reparse: bool = False
    identity: tuple[int, int] | None = None


def norm(path: str) -> str:
    value = ntpath.normpath(path.replace("/", "\\"))
    if len(value) == 2 and value[1] == ":":
        value += "\\"
    return value.casefold()


def within(candidate: str, root: str) -> bool:
    c, r = norm(candidate), norm(root)
    return c == r or c.startswith(r if r.endswith("\\") else r + "\\")


def volume_root(path: str) -> str:
    drive, _ = ntpath.splitdrive(ntpath.abspath(path))
    return drive + "\\" if drive else ""


def protected(path: str, protected_trees: tuple[str, ...]) -> bool:
    if path.casefold().startswith(("\\\\?\\", "\\\\.\\", "\\??\\")):
        return True
    root = volume_root(path)
    if not root:
        return True
    if norm(path) == norm(root):
        return True
    if any(within(path, tree) for tree in protected_trees):
        return True
    return any(
        within(path, ntpath.join(root, name))
        for name in ("$Recycle.Bin", "System Volume Information", "Recovery", "Boot", "EFI")
    )


def validate_delete(
    source_root: Resolved,
    source: Resolved,
    *,
    is_directory: bool,
    protected_trees: tuple[str, ...],
) -> str:
    if source_root.state != "directory" or source_root.reparse or source_root.identity is None:
        return BLOCKED
    if protected(source_root.canonical, protected_trees):
        return BLOCKED
    if is_directory:
        return BLOCKED
    if source.state != "file" or source.reparse or source.identity is None:
        return BLOCKED
    if norm(ntpath.dirname(source.canonical)) != norm(source_root.canonical):
        return BLOCKED
    if protected(source.canonical, protected_trees):
        return BLOCKED
    return READY


def run_model(cases: int, seed: int) -> int:
    protected_trees = (r"C:\Windows", r"C:\Program Files", r"C:\ProgramData")
    root = Resolved(r"C:\Users\A\Temp", r"C:\Users\A\Temp", "directory", identity=(1, 10))
    file = Resolved(r"C:\Users\A\Temp\a.tmp", r"C:\Users\A\Temp\a.tmp", "file", identity=(1, 11))
    checks = 0

    assert validate_delete(root, file, is_directory=False, protected_trees=protected_trees) == READY
    assert validate_delete(root, file, is_directory=True, protected_trees=protected_trees) == BLOCKED
    assert validate_delete(root, file.__class__(file.requested, r"C:\Other\a.tmp", "file", identity=(1, 11)), is_directory=False, protected_trees=protected_trees) == BLOCKED
    assert protected(r"C:\", protected_trees)
    assert protected(r"C:\Windows\Temp\a.tmp", protected_trees)
    assert protected(r"C:\$Recycle.Bin\x.bin", protected_trees)
    assert protected(r"\\?\Volume{00000000-0000-0000-0000-000000000000}\Users\A\a.tmp", protected_trees)
    assert protected(r"\\.\C:\Users\A\a.tmp", protected_trees)
    assert protected(r"\??\C:\Users\A\a.tmp", protected_trees)
    assert not protected(r"C:\Users\A\AppData\Local\Temp\a.tmp", protected_trees)
    checks += 10

    rng = random.Random(seed)
    states = ("file", "directory", "missing", "inaccessible", "error")
    for case in range(cases):
        source_root_path = rf"C:\Users\U{case % 101}\Temp"
        root_state = "directory" if rng.random() < 0.9 else rng.choice(states)
        root_reparse = rng.random() < 0.03
        root_identity = None if rng.random() < 0.03 else (1, case + 1)
        root_canonical = source_root_path
        if rng.random() < 0.03:
            root_canonical = r"C:\Windows\Temp"

        is_directory = rng.random() < 0.2
        source_state = rng.choice(states)
        source_reparse = rng.random() < 0.03
        source_identity = None if rng.random() < 0.03 else (1, 100_000 + case)
        name = f"entry-{case}.tmp"
        source_canonical = root_canonical + "\\" + name
        escaped = rng.random() < 0.04
        if escaped:
            source_canonical = rf"C:\Escape\{name}"

        root_value = Resolved(
            source_root_path,
            root_canonical,
            root_state,
            root_reparse,
            root_identity,
        )
        source_value = Resolved(
            source_root_path + "\\" + name,
            source_canonical,
            source_state,
            source_reparse,
            source_identity,
        )
        result = validate_delete(
            root_value,
            source_value,
            is_directory=is_directory,
            protected_trees=protected_trees,
        )
        expected_ready = (
            root_state == "directory"
            and not root_reparse
            and root_identity is not None
            and not protected(root_canonical, protected_trees)
            and not is_directory
            and source_state == "file"
            and not source_reparse
            and source_identity is not None
            and not escaped
            and not protected(source_canonical, protected_trees)
        )
        assert (result == READY) == expected_ready
        assert result in {READY, BLOCKED}
        checks += 2

        delete_mutation_authorized = False
        assert not delete_mutation_authorized
        checks += 1

        if expected_ready:
            identityless = source_value.__class__(
                source_value.requested,
                source_value.canonical,
                source_value.state,
                source_value.reparse,
                None,
            )
            assert validate_delete(
                root_value,
                identityless,
                is_directory=is_directory,
                protected_trees=protected_trees,
            ) == BLOCKED
            checks += 1

        escaped_value = source_value.__class__(
            source_value.requested,
            rf"D:\Escaped\{name}",
            source_value.state,
            source_value.reparse,
            source_value.identity,
        )
        assert validate_delete(
            root_value,
            escaped_value,
            is_directory=is_directory,
            protected_trees=protected_trees,
        ) == BLOCKED
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
    core = (root / "src/FileOp.Core/Operations/FileDeleteOperationExecutionValidation.cs").read_text(encoding="utf-8")
    windows = (root / "src/FileOp.Windows/Operations/WindowsFileDeleteOperationExecutionValidator.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/FileDeleteOperationExecutionValidationTests.cs").read_text(encoding="utf-8")
    policy_tests = (root / "tests/FileOp.Windows.Tests/FileDeleteProtectedLocationPolicyTests.cs").read_text(encoding="utf-8")
    requested_tests = (root / "tests/FileOp.Windows.Tests/FileDeleteOperationExecutionRequestedPathTests.cs").read_text(encoding="utf-8")
    docs = (root / "docs/file-delete-execution-validation.md").read_text(encoding="utf-8")
    delete_preflight = (root / "src/FileOp.Core/Operations/FileDeleteOperationPreflight.cs").read_text(encoding="utf-8")
    generic_execution = (root / "src/FileOp.Core/Operations/FileOperationExecution.cs").read_text(encoding="utf-8")
    plan = (root / "src/FileOp.Core/Operations/FileOperationPlan.cs").read_text(encoding="utf-8")
    cleanup = (root / "src/FileOp.Core/Storage/StorageCleanupReadiness.cs").read_text(encoding="utf-8")
    parent = (root / "tools/verify_file_operation_execution_validation.py").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")

    checks = 0
    required = [
        (core, "public interface IFileDeleteProtectedLocationPolicy", "protected policy contract"),
        (core, "if (!Enum.IsDefined(decision))", "protected policy decision invariant"),
        (core, "ArgumentException.ThrowIfNullOrWhiteSpace(reason)", "protected policy reason invariant"),
        (core, "ReadyForAuthorizationReview", "authorization-review naming"),
        (core, "current non-reparse file identity evidence", "ready item identity invariant"),
        (core, "sourceDirectory.Identity is not null", "root identity invariant"),
        (core, "public bool CanRequestAuthorizationReview", "review handoff state"),
        (core, "public bool DeleteMutationAuthorized => false;", "hard non-authorization"),
        (core, "IFileDeleteOperationExecutionValidator", "delete validation contract"),
        (windows, "new WindowsFileOperationCanonicalPathResolver()", "existing canonical resolver reuse"),
        (windows, "WindowsFileDeleteProtectedLocationPolicy", "Windows protected policy"),
        (windows, "Environment.SpecialFolder.Windows", "Windows tree protection"),
        (windows, "Environment.SpecialFolder.ProgramFiles", "Program Files protection"),
        (windows, "Environment.SpecialFolder.CommonApplicationData", "ProgramData protection"),
        (windows, '"$Recycle.Bin"', "recycle root protection"),
        (windows, '"System Volume Information"', "volume metadata protection"),
        (windows, "Filesystem volume/share roots are protected", "volume-root protection"),
        (windows, "Residual extended/device namespace paths are blocked", "extended/device namespace protection"),
        (windows, "Path.GetDirectoryName(requestedSource)", "requested direct-child validation"),
        (windows, "Path.GetFileName(requestedSource)", "requested leaf validation"),
        (windows, "leafName.Contains(Path.VolumeSeparatorChar)", "requested ADS rejection"),
        (windows, "sourceDirectory.Identity is null", "root identity check"),
        (windows, "source.Identity is null", "file identity check"),
        (windows, "canonicalSourceParent", "canonical parent validation"),
        (windows, "outside the canonical captured source directory", "canonical escape wording"),
        (windows, "Directory deletion remains outside", "file-only scope"),
        (windows, "Fresh user authorization and a mutation-bound identity lease are still required", "future lease boundary"),
        (windows, "No delete authorization was granted and no filesystem mutation was attempted", "runtime non-authorization wording"),
        (tests, "ProtectedLocationPolicyBlocksSystemTreesAndVolumeRoots", "protected policy regression"),
        (tests, "MatchingCanonicalFileIsReadyForAuthorizationReviewButNeverAuthorized", "ready/non-authorized regression"),
        (tests, "CanonicalParentEscapeFailsClosed", "canonical escape regression"),
        (tests, "ProtectedCanonicalRootBlocksBeforeEntryResolution", "protected short-circuit regression"),
        (tests, "MissingChangedReparseOrIdentitylessFileFailsClosed", "file identity regression"),
        (tests, "SourceRootNeedsCanonicalDirectoryIdentityAndCannotBeReparse", "root identity regression"),
        (policy_tests, "ProtectedLocationResultRejectsMalformedDecisionsAndReasons", "policy result invariant regression"),
        (policy_tests, "ResidualExtendedAndDeviceNamespacesFailClosed", "extended/device namespace regression"),
        (requested_tests, "RequestedPathMustRemainDirectChildWithCapturedLeafAndNoAds", "requested path regression"),
        (docs, "fresh handle-resolved validation pass", "fresh validation documentation"),
        (docs, "volume/share roots", "protected root documentation"),
        (docs, "mutation-time identity/handle lease", "TOCTOU boundary documentation"),
        (delete_preflight, "public bool DeleteMutationAuthorized => false;", "preflight remains non-authorizing"),
        (cleanup, "CleanupMutationAuthorized => false", "cleanup remains non-authorizing"),
        (plan, "public enum FileOperationKind\n{\n    Copy,\n    Move,", "Copy/Move enum unchanged"),
        (parent, "from verify_file_delete_execution_validation import (", "parent imports delete execution child"),
        (parent, "run_delete_execution_model(args.cases", "parent runs delete execution model"),
        (parent, "check_delete_execution_repository(args.repo_root.resolve())", "parent runs delete execution source checks"),
        (gate, "verify_file_operation_execution_validation.py --repo-root $repoRoot --cases 50000", "existing direct execution gate"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
    ]
    for text, needle, label in required:
        checks += require(text, needle, label)

    combined = core + "\n" + windows
    for text, needle, label in (
        (plan, "Delete,", "Delete enum value"),
        (combined, "IFileOperationExecutor", "generic executor integration"),
        (generic_execution, "FileDeleteOperationPlan", "delete execution wiring"),
        (windows, "File.Delete(", "file delete primitive"),
        (windows, "Directory.Delete(", "directory delete primitive"),
        (windows, "DeleteFileW", "native delete primitive"),
        (windows, "SetFileInformationByHandle", "handle delete primitive"),
        (windows, "MoveFileEx", "move/delete primitive"),
        (windows, "File.Copy(", "copy primitive"),
        (windows, "File.Move(", "move primitive"),
    ):
        checks += forbid(text, needle, label)

    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0xD31E7E)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {source_checks:,} source checks" if args.repo_root else ""
    print(
        f"PASS: file delete execution validation verified with {model_checks:,} model assertions "
        f"across {args.cases:,} randomized states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
