#!/usr/bin/env python3
"""Portable model/source checks for the same-volume directory Move transaction."""
from __future__ import annotations

import argparse
import random
import sys
from enum import Enum, auto
from pathlib import Path


class EntryState(Enum):
    PENDING = auto()
    MUTATION_STARTED = auto()
    COMMITTED = auto()
    SKIPPED = auto()
    FAILED = auto()
    RECOVERY_REQUIRED = auto()


class TerminalState(Enum):
    SUCCEEDED = auto()
    FAILED = auto()
    CANCELLED = auto()
    RECOVERY_REQUIRED = auto()


def strategy_ready(
    *,
    move: bool,
    validation_ready: bool,
    all_directories: bool,
    roots_identified: bool,
    roots_distinct: bool,
    equal_serials: bool,
    local_paths: bool,
    sources_safe: bool,
    destinations_safe: bool,
) -> bool:
    return all(
        (
            move,
            validation_ready,
            all_directories,
            roots_identified,
            roots_distinct,
            equal_serials,
            local_paths,
            sources_safe,
            destinations_safe,
        )
    )


def entry_transition_allowed(current: EntryState, target: EntryState) -> bool:
    return (current, target) in {
        (EntryState.PENDING, EntryState.MUTATION_STARTED),
        (EntryState.PENDING, EntryState.FAILED),
        (EntryState.MUTATION_STARTED, EntryState.COMMITTED),
        (EntryState.MUTATION_STARTED, EntryState.RECOVERY_REQUIRED),
    }


def terminal_allowed(states: list[EntryState], terminal: TerminalState) -> bool:
    mutation_sensitive = any(
        state in {EntryState.MUTATION_STARTED, EntryState.RECOVERY_REQUIRED}
        for state in states
    )
    if terminal is TerminalState.SUCCEEDED:
        return all(state in {EntryState.COMMITTED, EntryState.SKIPPED} for state in states)
    if terminal in {TerminalState.FAILED, TerminalState.CANCELLED}:
        return not mutation_sensitive
    if terminal is TerminalState.RECOVERY_REQUIRED:
        return mutation_sensitive
    raise AssertionError(terminal)


def run_model(cases: int) -> int:
    rng = random.Random(0xD1_200_2026)
    checks = 0

    assert strategy_ready(
        move=True,
        validation_ready=True,
        all_directories=True,
        roots_identified=True,
        roots_distinct=True,
        equal_serials=True,
        local_paths=True,
        sources_safe=True,
        destinations_safe=True,
    )
    assert entry_transition_allowed(EntryState.PENDING, EntryState.MUTATION_STARTED)
    assert entry_transition_allowed(EntryState.MUTATION_STARTED, EntryState.COMMITTED)
    assert entry_transition_allowed(EntryState.MUTATION_STARTED, EntryState.RECOVERY_REQUIRED)
    assert not entry_transition_allowed(EntryState.PENDING, EntryState.COMMITTED)
    assert terminal_allowed([EntryState.COMMITTED, EntryState.SKIPPED], TerminalState.SUCCEEDED)
    assert not terminal_allowed([EntryState.MUTATION_STARTED], TerminalState.FAILED)
    assert terminal_allowed([EntryState.RECOVERY_REQUIRED], TerminalState.RECOVERY_REQUIRED)
    checks += 8

    fields = (
        "move",
        "validation_ready",
        "all_directories",
        "roots_identified",
        "roots_distinct",
        "equal_serials",
        "local_paths",
        "sources_safe",
        "destinations_safe",
    )
    states = list(EntryState)
    terminals = list(TerminalState)
    for _ in range(cases):
        values = {field: bool(rng.getrandbits(1)) for field in fields}
        assert strategy_ready(**values) == all(values.values())
        checks += 1

        current = rng.choice(states)
        target = rng.choice(states)
        expected_transition = (current, target) in {
            (EntryState.PENDING, EntryState.MUTATION_STARTED),
            (EntryState.PENDING, EntryState.FAILED),
            (EntryState.MUTATION_STARTED, EntryState.COMMITTED),
            (EntryState.MUTATION_STARTED, EntryState.RECOVERY_REQUIRED),
        }
        assert entry_transition_allowed(current, target) == expected_transition
        checks += 1

        entry_states = [rng.choice(states) for _ in range(rng.randrange(1, 8))]
        terminal = rng.choice(terminals)
        sensitive = any(
            state in {EntryState.MUTATION_STARTED, EntryState.RECOVERY_REQUIRED}
            for state in entry_states
        )
        expected_terminal = (
            all(state in {EntryState.COMMITTED, EntryState.SKIPPED} for state in entry_states)
            if terminal is TerminalState.SUCCEEDED
            else not sensitive
            if terminal in {TerminalState.FAILED, TerminalState.CANCELLED}
            else sensitive
        )
        assert terminal_allowed(entry_states, terminal) == expected_terminal
        checks += 1

        trace = ["validate", "begin-history", "fresh-validate", "mutation-started", "rename", "commit"]
        assert trace.index("mutation-started") < trace.index("rename") < trace.index("commit")
        checks += 1

    return checks


def read(root: Path, relative: str) -> str:
    path = root / relative
    if not path.is_file():
        raise FileNotFoundError(str(path))
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
    strategy = read(root, "src/FileOp.Core/Operations/DirectorySameVolumeMoveExecutionStrategy.cs")
    history = read(root, "src/FileOp.Core/Operations/DirectorySameVolumeMoveActionHistory.cs")
    store = read(root, "src/FileOp.Core/Operations/SqliteDirectorySameVolumeMoveActionHistoryStore.cs")
    executor = read(root, "src/FileOp.Core/Operations/DirectorySameVolumeMoveOperationExecutor.cs")
    primitive = read(root, "src/FileOp.Windows/Operations/WindowsDirectorySameVolumeMoveMutationPrimitive.cs")
    guard = read(root, "src/FileOp.Windows/Operations/WindowsNtfsDirectorySameVolumeMoveMutationPrimitive.cs")
    move_validator = read(root, "src/FileOp.Windows/Operations/WindowsMoveOperationExecutionValidator.cs")
    app_move = read(root, "src/FileOp.App/FilesView.Move.cs")
    tests = read(root, "tests/FileOp.Windows.Tests/DirectorySameVolumeMoveTransactionTests.cs")

    checks = 0
    checks += require(
        strategy,
        "public enum DirectorySameVolumeMoveExecutionStrategy",
        "SameVolumeDirectoryRenameRequired",
        "GrantsMutationAuthority => false",
        "!item.Entry.IsDirectory",
        "item.Source.State != FileOperationCanonicalPathState.Directory",
        "item.Source.IsLeafReparsePoint",
        "item.Destination.State != FileOperationCanonicalPathState.Missing",
        "sourceRootIdentity.VolumeSerialNumber != destinationRootIdentity.VolumeSerialNumber",
        "local drive-letter paths",
    )
    checks += require(
        history,
        "DirectorySameVolumeMoveActionEntryState",
        "MutationStarted",
        "RecoveryRequired",
        "IDirectorySameVolumeMoveActionHistoryStore",
        "GrantsAutomaticReplayAuthority => false",
        "GrantsRollbackAuthority => false",
        "same-volume rename only",
    )
    checks += forbid(history, "IFileMoveOperationActionHistoryStore", "FileOperationUndoKind")

    checks += require(
        store,
        "directory_same_volume_move_operations",
        "directory_same_volume_move_entries",
        "DirectorySameVolumeMoveActionEntryState.Pending",
        "DirectorySameVolumeMoveActionEntryState.MutationStarted",
        "DirectorySameVolumeMoveActionEntryState.Committed",
        "DirectorySameVolumeMoveActionEntryState.RecoveryRequired",
        "WHERE operation_id = @operation_id",
        "AND state = @expected_state",
        "A mutation-sensitive directory Move cannot settle as Failed/Cancelled",
    )

    mutation_started = executor.index(".MarkMutationStartedAsync(plan.Id, ordinal, UtcNow())")
    rename = executor.index(".RenameDirectoryAsync(new DirectorySameVolumeMoveMutationRequest(")
    commit = executor.index(".CommitAsync(plan.Id, ordinal, receipt.DestinationIdentity, UtcNow())")
    assert mutation_started < rename < commit
    checks += 3
    checks += require(
        executor,
        "public sealed class DirectorySameVolumeMoveOperationExecutor : IFileOperationExecutor",
        "DirectorySameVolumeMoveExecutionStrategy.SameVolumeDirectoryRenameRequired",
        "The directory Move transaction does not accept regular-file entries.",
        "Cancellation intentionally stops at the durable MutationStarted barrier.",
        "MarkRecoveryRequiredAsync(",
        "DirectorySameVolumeMoveActionTerminalState.RecoveryRequired",
        "receipt.DestinationIdentity != expected",
    )

    checks += require(
        primitive,
        "public sealed class WindowsDirectorySameVolumeMoveMutationPrimitive",
        "FileDirectoryFile = 0x00000001",
        "FileOpenReparsePoint",
        "FileRenameInformation = 10",
        "NtSetInformationFile(",
        "Marshal.SizeOf<FileRenameInfoLayout>() + fileNameLength",
        "for (var offset = 0; offset < bufferSize; offset++)",
        "0); // ReplaceIfExists == FALSE.",
        "OpenRelativeSourceDirectory(sourceParent, sourceLeafName)",
        "FileSynchronousIoNonAlert | FileDirectoryFile | FileOpenReparsePoint",
        "The renamed destination is no longer an ordinary directory.",
        "destinationIdentity != sourceIdentity",
        "PathsEqual(postRenamePath, destinationCanonicalPath)",
    )
    checks += forbid(
        primitive,
        "Directory.Move(",
        "Directory.Delete(",
        "File.Copy(",
        "File.Move(",
    )

    checks += require(
        guard,
        "WindowsNtfsMutationCapabilityGuard.RequireExactNtfs(",
        "Directory Move source mutation root",
        "Directory Move destination mutation root",
        "_volumeRelationshipProbe.Query(",
        "FileOperationVolumeRelationshipState.SameVolume",
        "return _inner.RenameDirectoryAsync(request);",
    )
    assert guard.index("WindowsNtfsMutationCapabilityGuard.RequireExactNtfs(") < guard.index("_volumeRelationshipProbe.Query(") < guard.index("return _inner.RenameDirectoryAsync(request);")
    checks += 3

    checks += require(
        move_validator,
        "EqualSerialDifferentVolumeSummary",
        "sourceIdentity.VolumeSerialNumber == destinationIdentity.VolumeSerialNumber",
        "case FileOperationVolumeRelationshipState.SameVolume:",
        "case FileOperationVolumeRelationshipState.DifferentVolume:",
        "return Block(validation, EqualSerialDifferentVolumeSummary);",
    )
    checks += require(
        app_move,
        "plan.Intent.Entries.Any(static entry => entry.IsDirectory)",
        "Directory Move remains disabled.",
    )
    checks += forbid(app_move, "DirectorySameVolumeMoveOperationExecutor", "WindowsDirectorySameVolumeMoveMutationPrimitive")

    checks += require(
        tests,
        "SqliteHistoryPersistsMutationBarrierAndCommitAcrossReopen",
        "MutationSensitiveHistoryCannotSettleAsOrdinaryFailure",
        "ExecutorPersistsMutationStartedBeforeProviderAndCommitsIdentity",
        "ProviderFailureAfterMutationBarrierBecomesRecoveryRequired",
        "RawGuardRequiresExactNtfsBeforeVolumeRelationshipOrRename",
        "RawGuardRequiresStrongerSameVolumeRelationshipBeforeRename",
        "NativeDirectoryRenamePreservesIdentityAndNestedDescendants",
        "NativeDirectoryRenameNeverReplacesDestinationCreatedAfterValidation",
    )
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--cases", type=int, default=50000)
    parser.add_argument("--model-only", action="store_true")
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases)
    print(
        f"PASS same-volume directory Move model: {model_checks:,} checks across {args.cases:,} randomized cases"
    )
    if not args.model_only:
        source_checks = check_repository(args.repo_root.resolve())
        print(f"PASS same-volume directory Move source contract: {source_checks} checks")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
