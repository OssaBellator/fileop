#!/usr/bin/env python3
"""Zero-Actions model/source checks for the Windows file Copy mutation primitive."""
from __future__ import annotations

import argparse
import os
import random
import tempfile
from pathlib import Path


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

        source_directory = os.open(
            source,
            os.O_RDONLY | getattr(os, "O_DIRECTORY", 0),
        )
        destination_directory = os.open(
            destination,
            os.O_RDONLY | getattr(os, "O_DIRECTORY", 0),
        )
        try:
            source_file = os.open(
                "item.bin",
                os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0),
                dir_fd=source_directory,
            )
            try:
                expected_source = os.fstat(source_file)

                # Swap the path after the directory handle is acquired. Relative
                # creation must stay bound to the already-open directory object.
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
                    actual_source = os.fstat(source_file)
                    assert (actual_source.st_dev, actual_source.st_ino) == (
                        expected_source.st_dev,
                        expected_source.st_ino,
                    )
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
    source_path = (
        root
        / "src/FileOp.Windows/Operations/WindowsFileCopyMutationPrimitive.cs"
    )
    wrapper_path = root / "tools/test-copy-executor-local.ps1"
    missing = [str(path) for path in (source_path, wrapper_path) if not path.is_file()]
    if missing:
        raise FileNotFoundError(", ".join(missing))

    source = source_path.read_text(encoding="utf-8")
    wrapper = wrapper_path.read_text(encoding="utf-8")
    required = [
        "class WindowsFileCopyMutationPrimitive",
        "NtCreateFile(",
        "RootDirectory",
        "FileCreate",
        "FileOpenReparsePoint",
        "FileShare.ReadWrite",
        "FileShare.Read",
        "FlushFileBuffers(",
        "GetFileInformationByHandle(",
        "GetFinalPathNameByHandleW(",
        "expectedSourceIdentity",
        "actualIdentity != expectedIdentity",
        "createInformation != FileCreated",
        "MutationLease",
    ]
    for needle in required:
        assert needle in source, needle

    for forbidden in [
        "File.Copy(",
        "File.Move(",
        "File.Delete(",
        "Directory.Move(",
        "Directory.Delete(",
    ]:
        assert forbidden not in source, forbidden

    assert "verify_windows_file_copy_mutation.py" in wrapper
    return len(required) + 6


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
        f"PASS Windows Copy handle-binding model: {checks} checks "
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
