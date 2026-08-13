#!/usr/bin/env python3
"""Zero-Actions checks for FileOp's read-only file-operation preflight boundary."""
from __future__ import annotations
import argparse, ntpath, random, re, sys
from dataclasses import dataclass
from enum import Enum, auto
from pathlib import Path

from verify_file_delete_preflight import (
    check_repository as check_delete_preflight_repository,
    run_model as run_delete_preflight_model,
)


class PathState(Enum):
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
class Inspection:
    state: PathState
    reparse: bool = False


def norm(path: str) -> str:
    value = ntpath.normpath(path.replace('/', '\\'))
    if len(value) == 2 and value[1] == ':':
        value += '\\'
    return value.casefold()


def within(candidate: str, root: str) -> bool:
    c, r = norm(candidate), norm(root)
    return c == r or c.startswith(r if r.endswith('\\') else r + '\\')


def classify(
    source_dir: str,
    destination_dir: str,
    entry_path: str,
    entry_name: str,
    is_directory: bool,
    source: Inspection,
    destination: Inspection,
    policy: Policy,
    source_root: Inspection = Inspection(PathState.DIRECTORY),
    destination_root: Inspection = Inspection(PathState.DIRECTORY),
) -> Decision:
    if norm(source_dir) == norm(destination_dir):
        return Decision.BLOCKED
    if source_root.state is not PathState.DIRECTORY or source_root.reparse:
        return Decision.BLOCKED
    if destination_root.state is not PathState.DIRECTORY or destination_root.reparse:
        return Decision.BLOCKED

    normalized_entry = ntpath.normpath(entry_path)
    if norm(ntpath.dirname(normalized_entry)) != norm(source_dir):
        return Decision.BLOCKED
    leaf = ntpath.basename(normalized_entry)
    if not leaf or leaf.casefold() != entry_name.casefold() or ':' in leaf:
        return Decision.BLOCKED
    if is_directory and within(destination_dir, normalized_entry):
        return Decision.BLOCKED

    expected = PathState.DIRECTORY if is_directory else PathState.FILE
    if source.state is not expected or source.reparse:
        return Decision.BLOCKED
    if destination.state in {PathState.INACCESSIBLE, PathState.ERROR}:
        return Decision.BLOCKED
    if destination.state is PathState.MISSING:
        return Decision.READY
    if policy is Policy.ASK:
        return Decision.NEEDS_DECISION
    if policy is Policy.SKIP:
        return Decision.SKIP
    return Decision.BLOCKED


def check_properties(cases: int) -> int:
    src = r'C:\Source'
    dst = r'C:\Destination'
    file_path = src + r'\a.txt'
    directory_path = src + r'\Folder'
    present_file = Inspection(PathState.FILE)
    present_dir = Inspection(PathState.DIRECTORY)
    missing = Inspection(PathState.MISSING)

    assert classify(src, dst, file_path, 'a.txt', False, present_file, missing, Policy.ASK) is Decision.READY
    assert classify(src, dst, file_path, 'a.txt', False, present_file, present_file, Policy.ASK) is Decision.NEEDS_DECISION
    assert classify(src, dst, file_path, 'a.txt', False, present_file, present_file, Policy.SKIP) is Decision.SKIP
    assert classify(src, dst, file_path, 'a.txt', False, present_file, present_file, Policy.STOP) is Decision.BLOCKED
    assert classify(src, src, file_path, 'a.txt', False, present_file, missing, Policy.ASK) is Decision.BLOCKED
    assert classify(src, dst, r'C:\Other\a.txt', 'a.txt', False, present_file, missing, Policy.ASK) is Decision.BLOCKED
    assert classify(src, dst, file_path, 'renamed.txt', False, present_file, missing, Policy.ASK) is Decision.BLOCKED
    assert classify(src, dst, src + r'\a.txt:stream', 'a.txt:stream', False, present_file, missing, Policy.ASK) is Decision.BLOCKED
    assert classify(src, dst, file_path, 'a.txt', False, Inspection(PathState.MISSING), missing, Policy.ASK) is Decision.BLOCKED
    assert classify(src, dst, file_path, 'a.txt', False, present_dir, missing, Policy.ASK) is Decision.BLOCKED
    assert classify(src, dst, file_path, 'a.txt', False, Inspection(PathState.FILE, True), missing, Policy.ASK) is Decision.BLOCKED
    assert classify(src, dst, file_path, 'a.txt', False, present_file, Inspection(PathState.INACCESSIBLE), Policy.ASK) is Decision.BLOCKED
    assert classify(src, dst, directory_path, 'Folder', True, present_dir, missing, Policy.ASK,
                    destination_root=Inspection(PathState.DIRECTORY, True)) is Decision.BLOCKED
    assert classify(src, directory_path + r'\Child', directory_path, 'Folder', True, present_dir, missing, Policy.ASK) is Decision.BLOCKED
    checks = 14

    rng = random.Random(20260808)
    path_states = list(PathState)
    policies = list(Policy)
    for case in range(cases):
        source_dir = rf'C:\Root\S{case % 97}'
        destination_dir = rf'C:\Root\D{case % 89}'
        is_directory = rng.random() < 0.3
        name = f"entry{case}{'' if is_directory else '.dat'}"
        entry_path = source_dir + '\\' + name
        source_state = PathState.DIRECTORY if is_directory else PathState.FILE
        source = Inspection(source_state, reparse=rng.random() < 0.03)
        destination = Inspection(rng.choice(path_states), reparse=rng.random() < 0.03)
        policy = rng.choice(policies)

        result = classify(
            source_dir,
            destination_dir,
            entry_path,
            name,
            is_directory,
            source,
            destination,
            policy,
        )

        if source.reparse:
            assert result is Decision.BLOCKED
        elif destination.state is PathState.MISSING:
            assert result is Decision.READY
        elif destination.state in {PathState.INACCESSIBLE, PathState.ERROR}:
            assert result is Decision.BLOCKED
        elif policy is Policy.ASK:
            assert result is Decision.NEEDS_DECISION
        elif policy is Policy.SKIP:
            assert result is Decision.SKIP
        else:
            assert result is Decision.BLOCKED
        checks += 1

        assert classify(
            source_dir,
            destination_dir,
            entry_path,
            name,
            not is_directory,
            source,
            missing,
            policy,
        ) is Decision.BLOCKED
        checks += 1

        if is_directory:
            assert classify(
                source_dir,
                entry_path + r'\Nested',
                entry_path,
                name,
                True,
                source,
                missing,
                policy,
            ) is Decision.BLOCKED
            checks += 1

    return checks


def check_repository(root: Path) -> int:
    paths = {
        'core': root / 'src/FileOp.Core/Operations/FileOperationPreflight.cs',
        'windows': root / 'src/FileOp.Windows/Operations/WindowsFileOperationPreflightValidator.cs',
        'execution': root / 'src/FileOp.Core/Operations/FileOperationExecution.cs',
        'docs': root / 'docs/file-operation-execution-validation.md',
        'local': root / 'tools/test-local.ps1',
    }
    missing = [str(path) for path in paths.values() if not path.is_file()]
    if missing:
        raise FileNotFoundError(', '.join(missing))
    source = {name: path.read_text(encoding='utf-8') for name, path in paths.items()}

    for needle in [
        'using System.Linq;',
        'public enum FileOperationPathState',
        'public interface IFileOperationPathProbe',
        'public enum FileOperationPreflightDecision',
        'public sealed record FileOperationPreflightItem(',
        'public sealed record FileOperationPreflightResult(',
        'public bool CanProceedToExecutionValidation',
        'public interface IFileOperationPreflightValidator',
    ]:
        assert needle in source['core'], needle

    required_windows = [
        'public sealed class WindowsFileOperationPathProbe : IFileOperationPathProbe',
        'File.GetAttributes(normalized)',
        'FileAttributes.ReparsePoint',
        'Task.Run(',
        'public sealed class WindowsFileOperationPreflightValidator : IFileOperationPreflightValidator',
        'PathsEqual(sourceDirectory, destinationDirectory)',
        'IsUsableDirectory(sourceDirectoryInspection',
        'IsUsableDirectory(destinationDirectoryInspection',
        'Path.GetDirectoryName(sourcePath)',
        '!string.Equals(leafName, entry.Name, StringComparison.OrdinalIgnoreCase)',
        'leafName.Contains(Path.VolumeSeparatorChar)',
        'IsSameOrDescendantPath(destinationDirectory, sourcePath)',
        'sourceInspection.IsReparsePoint',
        'FileOperationCollisionPolicy.Ask',
        'FileOperationCollisionPolicy.Skip',
        'FileOperationCollisionPolicy.Stop',
        'This is read-only preflight and is not authorization to mutate the filesystem.',
    ]
    for needle in required_windows:
        assert needle in source['windows'], needle

    combined = source['core'] + source['windows']
    for forbidden in [
        'Directory.Enumerate', 'Directory.GetFiles', 'Directory.GetDirectories',
        'File.Copy(', 'File.Move(', 'File.Delete(',
        'Directory.Move(', 'Directory.Delete(',
        'File.Create(', 'File.Write', 'File.OpenWrite(',
    ]:
        assert forbidden not in combined, forbidden

    assert re.search(r'public interface IFileOperationExecutor\s*\{', source['execution'])
    assert 'class WindowsFileOperationPreflightValidator : IFileOperationExecutor' not in source['windows']
    assert 'deliberately separate from queue preflight' in source['docs']
    assert 'Preflight remains a fast conservative metadata check' in source['docs']
    assert 'reparse' in source['docs'].casefold()
    assert 'verify_file_operation_preflight.py' in source['local']

    return 8 + len(required_windows) + 11


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument('--repo-root', type=Path, default=Path.cwd())
    parser.add_argument('--self-test-only', action='store_true')
    parser.add_argument('--cases', type=int, default=50000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error('--cases must be greater than zero')

    copy_move_model = check_properties(args.cases)
    delete_model = run_delete_preflight_model(args.cases, 0xDE1E7E)
    print(f'PASS file operation preflight properties: {copy_move_model} Copy/Move checks; {delete_model} delete checks')
    if not args.self_test_only:
        copy_move_source = check_repository(args.repo_root.resolve())
        delete_source = check_delete_preflight_repository(args.repo_root.resolve())
        print(
            f'PASS file operation preflight source wiring: {copy_move_source} Copy/Move checks; '
            f'{delete_source} delete checks'
        )
    return 0


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f'FAIL: {exc}', file=sys.stderr)
        raise SystemExit(1)
