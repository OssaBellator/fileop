#!/usr/bin/env python3
"""Zero-Actions model/source checks for the Windows file Copy mutation primitive."""
from __future__ import annotations

import argparse
import os
import random
import tempfile
from pathlib import Path


def identity(stat_result: os.stat_result) -> tuple[int, int]:
    return stat_result.st_dev, stat_result.st_ino


def model_once(seed: int) -> int:
    rng = random.Random(seed)
    checks = 0
    with tempfile.TemporaryDirectory(prefix="fileop-copy-model-") as temporary:
        root = Path(temporary)
        source = root / "source"
        destination = root / "destination"
        moved_destination = root / "destination-moved"
        source.mkdir()
        destination.mkdir()
        payload = rng.randbytes(rng.randrange(0, 65_536))
        (source / "item.bin").write_bytes(payload)

        # Fresh validation observed this directory identity. If the textual root is
        # swapped before handle acquisition, the newly opened object must differ.
        expected_destination_identity = identity(
            os.stat(destination, follow_symlinks=False)
        )
        os.rename(destination, moved_destination)
        destination.mkdir()
        replaced_directory = os.open(
            destination,
            os.O_RDONLY | getattr(os, "O_DIRECTORY", 0),
        )
        try:
            assert identity(os.fstat(replaced_directory)) != expected_destination_identity
            checks += 1
        finally:
            os.close(replaced_directory)
        os.rmdir(destination)
        os.rename(moved_destination, destination)

        source_directory = os.open(
            source,
            os.O_RDONLY | getattr(os, "O_DIRECTORY", 0),
        )
        destination_directory = os.open(
            destination,
            os.O_RDONLY | getattr(os, "O_DIRECTORY", 0),
        )
        try:
            assert identity(os.fstat(destination_directory)) == expected_destination_identity
            checks += 1

            source_file = os.open(
                "item.bin",
                os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0),
                dir_fd=source_directory,
            )
            try:
                expected_source = identity(os.fstat(source_file))

                # Once the directory handle is acquired, replacing its textual path
                # cannot redirect a relative create through that handle.
                os.rename(destination, moved_destination)
                destination.mkdir()
                flags = (
                    os.O_WRONLY
                    | os.O_CREAT
                    | os.O_EXCL
                    | getattr(os, "O_NOFOLLOW", 0)
                )
                destination_file = os.open(
                    "item.bin",
                    flags,
                    0o600,
                    dir_fd=destination_directory,
                )
                try:
                    while True:
                        chunk = os.read(source_file, 8192)
                        if not chunk:
                            break
                        view = memoryview(chunk)
                        while view:
                            written = os.write(destination_file, view)
                            assert written > 0
                            view = view[written:]
                    os.fsync(destination_file)
                    assert identity(os.fstat(source_file)) == expected_source
                    checks += 2
                finally:
                    os.close(destination_file)

                assert (moved_destination / "item.bin").read_bytes() == payload
                assert not (destination / "item.bin").exists()
                checks += 2

                try:
                    os.open(
                        "item.bin",
                        flags,
                        0o600,
                        dir_fd=destination_directory,
                    )
                except FileExistsError:
                    checks += 1
                else:
                    raise AssertionError(
                        "exclusive relative create opened or overwrote an existing destination"
                    )
            finally:
                os.close(source_file)
        finally:
            os.close(destination_directory)
            os.close(source_directory)

    return checks


def check_repository(root: Path) -> int:
    primitive_path = (
        root
        / "src/FileOp.Windows/Operations/WindowsFileCopyMutationPrimitive.cs"
    )
    executor_path = root / "src/FileOp.Core/Operations/FileCopyOperationExecutor.cs"
    tests_path = (
        root
        / "tests/FileOp.Windows.Tests/WindowsFileCopyMutationPrimitiveTests.cs"
    )
    wrapper_path = root / "tools/test-copy-executor-local.ps1"
    missing = [
        str(path)
        for path in (primitive_path, executor_path, tests_path, wrapper_path)
        if not path.is_file()
    ]
    if missing:
        raise FileNotFoundError(", ".join(missing))

    source = primitive_path.read_text(encoding="utf-8")
    executor = executor_path.read_text(encoding="utf-8")
    tests = tests_path.read_text(encoding="utf-8")
    wrapper = wrapper_path.read_text(encoding="utf-8")

    required_source = [
        "class WindowsFileCopyMutationPrimitive",
        "FileCopyMutationRequest request",
        "request.SourceDirectory.Identity",
        "request.DestinationDirectory.Identity",
        "NtCreateFile(",
        "RootDirectory",
        "FileCreate",
        "FileWriteThrough",
        "FileOpenReparsePoint",
        "FileShare.ReadWrite",
        "FileShare.Read",
        "FileReadData",
        "GenericWrite",
        "FlushFileBuffers(",
        "GetFileInformationByHandle(",
        "GetFinalPathNameByHandleW(",
        "expectedSourceDirectoryIdentity",
        "expectedDestinationDirectoryIdentity",
        "ToIdentity(information) != expectedIdentity",
        "actualIdentity != expectedIdentity",
        "createInformation != FileCreated",
        "MutationLease",
    ]
    for needle in required_source:
        assert needle in source, needle

    required_executor = [
        "public sealed record FileCopyMutationRequest(",
        ".CopyNewFileAsync(new FileCopyMutationRequest(",
        "freshValidation.SourceDirectory",
        "freshValidation.DestinationDirectory",
    ]
    for needle in required_executor:
        assert needle in executor, needle

    for test_name in [
        "CopyCreatesExclusiveIdentityBoundDestinationAndLeaseBlocksDelete",
        "CollisionAppearingAfterValidationNeverOverwritesExistingFile",
        "SourceIdentityReplacementAfterValidationFailsBeforeDestinationCreation",
        "DestinationRootReplacementAfterValidationFailsBeforeCreation",
        "PrimitiveRejectsRootWithoutStableIdentity",
    ]:
        assert test_name in tests, test_name

    for forbidden in [
        "File.Copy(",
        "File.Move(",
        "File.Delete(",
        "Directory.Move(",
        "Directory.Delete(",
    ]:
        assert forbidden not in source, forbidden

    assert "verify_windows_file_copy_mutation.py" in wrapper
    return len(required_source) + len(required_executor) + 5 + 6


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--cases", type=int, default=2000)
    parser.add_argument("--self-test-only", action="store_true")
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error("--cases must be greater than zero")

    checks = sum(model_once(20260808 + case) for case in range(args.cases))
    print(
        f"PASS Windows Copy handle/root-binding model: {checks} checks "
        f"across {args.cases} cases"
    )
    if not args.self_test_only:
        print(
            "PASS Windows Copy source wiring: "
            f"{check_repository(args.repo_root.resolve())} checks"
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
