#!/usr/bin/env python3
"""Zero-Actions model/source checks for Files same-volume file and directory Move UI."""
from __future__ import annotations

import argparse
import random
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def common_ready(
    *,
    kind: str,
    entries: int,
    preflight: str,
    preflight_running: bool,
    bound: bool,
    copy_busy: bool,
    move_busy: bool,
) -> bool:
    return (
        kind == "Move"
        and entries > 0
        and preflight == "Ready"
        and not preflight_running
        and bound
        and not copy_busy
        and not move_busy
    )


def can_attempt_file(*, composition: str, **kwargs: object) -> bool:
    return composition == "Files" and common_ready(**kwargs)


def can_attempt_directory(*, composition: str, **kwargs: object) -> bool:
    return composition == "Directories" and common_ready(**kwargs)


def classify_file_for_ui(strategy: str) -> str:
    if strategy == "SameVolumeRenameRequired":
        return "Execute"
    if strategy == "SkipOnly":
        return "ExecuteNoMutation"
    if strategy == "CrossVolumeCopyDeleteRequired":
        return "KeepQueuedCrossVolume"
    return "KeepQueuedBlocked"


def classify_directory_for_ui(strategy: str) -> str:
    if strategy == "SameVolumeDirectoryRenameRequired":
        return "Execute"
    if strategy == "SkipOnly":
        return "ExecuteNoMutation"
    return "KeepQueuedBlocked"


def check_properties(cases: int) -> int:
    base = dict(
        kind="Move",
        entries=1,
        preflight="Ready",
        preflight_running=False,
        bound=True,
        copy_busy=False,
        move_busy=False,
    )
    assert can_attempt_file(composition="Files", **base)
    assert not can_attempt_file(composition="Directories", **base)
    assert can_attempt_directory(composition="Directories", **base)
    assert not can_attempt_directory(composition="Files", **base)
    assert not can_attempt_file(composition="Mixed", **base)
    assert not can_attempt_directory(composition="Mixed", **base)
    assert classify_file_for_ui("SameVolumeRenameRequired") == "Execute"
    assert classify_file_for_ui("CrossVolumeCopyDeleteRequired") == "KeepQueuedCrossVolume"
    assert classify_directory_for_ui("SameVolumeDirectoryRenameRequired") == "Execute"
    checks = 9

    rng = random.Random(20260815)
    for _ in range(cases):
        composition = rng.choice(["Empty", "Files", "Directories", "Mixed"])
        entries = 0 if composition == "Empty" else rng.randrange(1, 9)
        values = dict(
            kind=rng.choice(["Copy", "Move"]),
            entries=entries,
            preflight=rng.choice(["Missing", "Ready", "NeedsDecision", "Blocked"]),
            preflight_running=bool(rng.getrandbits(1)),
            bound=bool(rng.getrandbits(1)),
            copy_busy=bool(rng.getrandbits(1)),
            move_busy=bool(rng.getrandbits(1)),
        )
        common = common_ready(**values)
        assert can_attempt_file(composition=composition, **values) == (
            common and composition == "Files"
        )
        assert can_attempt_directory(composition=composition, **values) == (
            common and composition == "Directories"
        )
        if composition in {"Empty", "Mixed"}:
            assert not can_attempt_file(composition=composition, **values)
            assert not can_attempt_directory(composition=composition, **values)
            checks += 2
        checks += 2

        file_strategy = rng.choice([
            "SameVolumeRenameRequired",
            "SkipOnly",
            "CrossVolumeCopyDeleteRequired",
            "Blocked",
        ])
        file_disposition = classify_file_for_ui(file_strategy)
        assert file_disposition.startswith("Execute") == (
            file_strategy in {"SameVolumeRenameRequired", "SkipOnly"}
        )
        checks += 1

        directory_strategy = rng.choice([
            "SameVolumeDirectoryRenameRequired",
            "SkipOnly",
            "Blocked",
        ])
        directory_disposition = classify_directory_for_ui(directory_strategy)
        assert directory_disposition.startswith("Execute") == (
            directory_strategy in {"SameVolumeDirectoryRenameRequired", "SkipOnly"}
        )
        checks += 1

    return checks


def read(root: Path, relative: str) -> str:
    path = root / relative
    if not path.is_file():
        raise FileNotFoundError(path)
    return path.read_text(encoding="utf-8")


def require(text: str, *needles: str) -> int:
    for needle in needles:
        assert needle in text, needle
    return len(needles)


def forbid(text: str, *needles: str) -> int:
    for needle in needles:
        assert needle not in text, needle
    return len(needles)


def check_repository(root: Path) -> int:
    source = {
        "xaml": read(root, "src/FileOp.App/FilesView.xaml"),
        "move": read(root, "src/FileOp.App/FilesView.Move.cs"),
        "directory_move": read(root, "src/FileOp.App/FilesView.DirectoryMove.cs"),
        "copy": read(root, "src/FileOp.App/FilesView.Copy.cs"),
        "source": read(root, "src/FileOp.App/MainWindow.StorageSourceIdentity.cs"),
        "aliases": read(root, "src/FileOp.App/MutationExecutionValidatorAliases.cs"),
        "executor": read(root, "src/FileOp.Core/Operations/FileSameVolumeMoveOperationExecutor.cs"),
        "directory_executor": read(root, "src/FileOp.Core/Operations/DirectorySameVolumeMoveOperationExecutor.cs"),
        "directory_history": read(root, "src/FileOp.Core/Operations/DirectorySameVolumeMoveActionHistory.cs"),
        "primitive": read(root, "src/FileOp.Windows/Operations/WindowsFileSameVolumeMoveMutationPrimitive.cs"),
        "directory_primitive": read(root, "src/FileOp.Windows/Operations/WindowsDirectorySameVolumeMoveMutationPrimitive.cs"),
        "directory_guard": read(root, "src/FileOp.Windows/Operations/WindowsNtfsDirectorySameVolumeMoveMutationPrimitive.cs"),
        "move_validator": read(root, "src/FileOp.Windows/Operations/WindowsMoveOperationExecutionValidator.cs"),
        "namespace": read(root, "src/FileOp.Windows/Operations/WindowsFileOperationNamespaceCapability.cs"),
        "tests": read(root, "tests/FileOp.Windows.Tests/WindowsMoveOperationExecutionValidatorTests.cs"),
        "directory_tests": read(root, "tests/FileOp.Windows.Tests/DirectorySameVolumeMoveTransactionTests.cs"),
        "gate": read(root, "tools/test-local.ps1"),
    }
    ET.fromstring(source["xaml"])
    checks = 0

    checks += require(
        source["xaml"],
        'x:Name="RunQueuedMoveButton"',
        'Content="Run File Move"',
        'x:Name="RunQueuedDirectoryMoveButton"',
        'Loaded="RunQueuedDirectoryMoveButton_Loaded"',
        'Content="Run Directory Move"',
        "reviewed same-volume local file and homogeneous directory Move",
        "cross-volume and mixed file/directory Move remain disabled",
    )

    checks += require(
        source["move"],
        "public bool IsFileOperationExecutionBusy => _copyExecutionRunning || _moveExecutionRunning;",
        "new WindowsMoveOperationExecutionValidator()",
        "FileMoveExecutionStrategyClassifier.Classify(executionValidation)",
        "FileMoveExecutionStrategy.CrossVolumeCopyDeleteRequired",
        "new FileSameVolumeMoveOperationExecutor(",
        "new WindowsFileSameVolumeMoveMutationPrimitive()",
        "plan.Intent.Entries.Any(static entry => entry.IsDirectory)",
        "The executable Move boundary supports regular files only. Directory Move remains disabled.",
        "await executor.RequestCancellationAsync(operationId)",
        "will not replay, rollback or reinterpret the original operation ID automatically",
    )
    assert source["move"].count("new WindowsMoveOperationExecutionValidator()") >= 2
    checks += 1

    checks += require(
        source["directory_move"],
        "RunQueuedDirectoryMoveButton_Loaded",
        "RunSelectedDirectoryMoveAsync",
        "CanAttemptDirectoryMovePlan",
        "plan.Intent.Entries.All(static entry => entry.IsDirectory)",
        "Mixed file-and-directory Move batches remain unsupported",
        "new WindowsMoveOperationExecutionValidator()",
        "DirectorySameVolumeMoveExecutionStrategyClassifier.Classify(executionValidation)",
        "DirectorySameVolumeMoveExecutionStrategy.SameVolumeDirectoryRenameRequired",
        "DirectorySameVolumeMoveExecutionStrategy.SkipOnly",
        "new SqliteDirectorySameVolumeMoveActionHistoryStore(",
        "new DirectorySameVolumeMoveOperationExecutor(",
        "new WindowsDirectorySameVolumeMoveMutationPrimitive()",
        "DirectorySameVolumeMoveActionHistory? finalHistory",
        "finalSnapshot = await executor.ExecuteAsync(plan, progress);",
        "finalHistory = await historyStore.GetAsync(plan.Id);",
        "_queuedOperations.RemoveAll(operation => operation.Id == plan.Id);",
        "_preflightSnapshots.Remove(plan.Id);",
        "RequestRefreshForMoveEndpoint(plan.Intent.SourceDirectoryPath);",
        "RequestRefreshForMoveEndpoint(plan.Intent.DestinationDirectoryPath);",
        "Directory Move requires recovery inspection.",
        "will not replay, rollback or reinterpret the original operation ID automatically",
    )
    assert source["directory_move"].index(
        "DirectorySameVolumeMoveExecutionStrategyClassifier.Classify(executionValidation)"
    ) < source["directory_move"].index("_moveExecutionRunning = true;")
    checks += 1

    checks += require(
        source["aliases"],
        "global using WindowsDirectorySameVolumeMoveMutationPrimitive =",
        "FileOp.Windows.Operations.WindowsNtfsDirectorySameVolumeMoveMutationPrimitive",
    )
    checks += require(
        source["directory_executor"],
        "public sealed class DirectorySameVolumeMoveOperationExecutor : IFileOperationExecutor",
        ".MarkMutationStartedAsync(plan.Id, ordinal, UtcNow())",
        "Cancellation intentionally stops at the durable MutationStarted barrier.",
    )
    checks += require(
        source["directory_history"],
        "public sealed record DirectorySameVolumeMoveActionHistory",
        "public bool GrantsAutomaticReplayAuthority => false",
        "public bool GrantsRollbackAuthority => false",
    )
    checks += require(
        source["directory_primitive"],
        "public sealed class WindowsDirectorySameVolumeMoveMutationPrimitive",
        "NtSetInformationFile(",
        "FileRenameInformation = 10",
        "FileDirectoryFile | FileOpenReparsePoint",
        "ReplaceIfExists == FALSE",
    )
    checks += require(
        source["directory_guard"],
        "public sealed class WindowsNtfsDirectorySameVolumeMoveMutationPrimitive",
        "WindowsNtfsMutationCapabilityGuard.RequireExactNtfs(",
        "FileOperationVolumeRelationshipState.SameVolume",
        "destination parent cannot be the source directory or one of its descendants",
    )

    checks += require(
        source["move_validator"],
        "public sealed class WindowsMoveOperationExecutionValidator : IFileOperationExecutionValidator",
        "before durable mutation history",
        "No MutationStarted record or filesystem mutation was created",
    )
    checks += require(
        source["namespace"],
        "FileCaseSensitiveInformation = 71",
        "UnsupportedCaseSensitiveDirectory",
        "Unavailable",
        "throw new NotSupportedException(capability.Summary)",
    )
    checks += require(
        source["tests"],
        "CaseSensitiveSourceBlocksReadyMoveBeforeMutationHistory",
        "SupportedSameVolumeNamespacesReturnOriginalReadyMoveValidation",
    )
    checks += require(
        source["directory_tests"],
        "ExecutorPersistsMutationStartedBeforeProviderAndCommitsIdentity",
        "NativeDirectoryRenamePreservesIdentityAndNestedDescendants",
        "NativeDirectoryRenameNeverReplacesDestinationCreatedAfterValidation",
    )

    assert "_filesView.ReassertOperationExecutionBusyAfterSourceChange();" in source["source"]
    assert "FileSameVolumeMoveOperationExecutor : IFileOperationExecutor" in source["executor"]
    assert "SetFileInformationByHandle(" in source["primitive"]
    for text in (source["move"], source["directory_move"]):
        checks += forbid(text, "File.Move(", "Directory.Move(", "File.Copy(", "File.Delete(")
    assert "verify_files_same_volume_move_ui.py --repo-root $repoRoot --cases 50000" in source["gate"]
    checks += 4

    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--self-test-only", action="store_true")
    parser.add_argument("--cases", type=int, default=50000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error("--cases must be greater than zero")
    print(f"PASS Files same-volume file/directory Move UI model: {check_properties(args.cases):,} checks")
    if not args.self_test_only:
        print(f"PASS Files same-volume file/directory Move UI source wiring: {check_repository(args.repo_root.resolve())} checks")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ET.ParseError, ValueError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
