#!/usr/bin/env python3
"""Zero-Actions model/source checks for the Windows file Copy mutation primitive."""
from __future__ import annotations

import argparse
import os
import random
import tempfile
import time
from pathlib import Path


def identity(stat_result: os.stat_result) -> tuple[int, int]:
    return stat_result.st_dev, stat_result.st_ino


if os.name == "nt":
    import ctypes
    from ctypes import wintypes

    FileTraverse = 0x0020
    FileReadAttributes = 0x0080
    FileReadData = 0x0001
    GenericWrite = 0x40000000
    Synchronize = 0x00100000
    FileShareRead = 0x0001
    FileShareWrite = 0x0002
    FileShareDelete = 0x0004
    FileOpen = 1
    FileCreate = 2
    OpenExisting = 3
    FileAttributeNormal = 0x0080
    FileFlagBackupSemantics = 0x02000000
    FileFlagOpenReparsePoint = 0x00200000
    FileSynchronousIoNonAlert = 0x00000020
    FileNonDirectoryFile = 0x00000040
    ObjCaseInsensitive = 0x00000040
    InvalidHandleValue = ctypes.c_void_p(-1).value

    class UnicodeString(ctypes.Structure):
        _fields_ = [
            ("Length", wintypes.USHORT),
            ("MaximumLength", wintypes.USHORT),
            ("Buffer", wintypes.LPWSTR),
        ]

    class ObjectAttributes(ctypes.Structure):
        _fields_ = [
            ("Length", wintypes.ULONG),
            ("RootDirectory", wintypes.HANDLE),
            ("ObjectName", ctypes.POINTER(UnicodeString)),
            ("Attributes", wintypes.ULONG),
            ("SecurityDescriptor", ctypes.c_void_p),
            ("SecurityQualityOfService", ctypes.c_void_p),
        ]

    class IoStatusBlock(ctypes.Structure):
        _fields_ = [("Status", ctypes.c_long), ("Information", ctypes.c_size_t)]

    class ByHandleFileInformation(ctypes.Structure):
        _fields_ = [
            ("FileAttributes", wintypes.DWORD),
            ("CreationTimeLow", wintypes.DWORD),
            ("CreationTimeHigh", wintypes.DWORD),
            ("LastAccessTimeLow", wintypes.DWORD),
            ("LastAccessTimeHigh", wintypes.DWORD),
            ("LastWriteTimeLow", wintypes.DWORD),
            ("LastWriteTimeHigh", wintypes.DWORD),
            ("VolumeSerialNumber", wintypes.DWORD),
            ("FileSizeHigh", wintypes.DWORD),
            ("FileSizeLow", wintypes.DWORD),
            ("NumberOfLinks", wintypes.DWORD),
            ("FileIndexHigh", wintypes.DWORD),
            ("FileIndexLow", wintypes.DWORD),
        ]

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    ntdll = ctypes.WinDLL("ntdll")
    kernel32.CreateFileW.restype = wintypes.HANDLE
    kernel32.GetFileInformationByHandle.restype = wintypes.BOOL
    kernel32.ReadFile.restype = wintypes.BOOL
    kernel32.WriteFile.restype = wintypes.BOOL
    kernel32.FlushFileBuffers.restype = wintypes.BOOL
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.CloseHandle.restype = wintypes.BOOL
    ntdll.NtCreateFile.restype = ctypes.c_long

    def _raise_last_error(action: str) -> None:
        raise OSError(ctypes.get_last_error(), action)

    def _close(handle: int) -> None:
        if handle not in (None, InvalidHandleValue):
            if not kernel32.CloseHandle(handle):
                _raise_last_error("CloseHandle")

    def _handle_identity(handle: int) -> tuple[int, int]:
        information = ByHandleFileInformation()
        if not kernel32.GetFileInformationByHandle(handle, ctypes.byref(information)):
            _raise_last_error("GetFileInformationByHandle")
        return (
            information.VolumeSerialNumber,
            (information.FileIndexHigh << 32) | information.FileIndexLow,
        )

    def _open_directory(path: Path) -> int:
        handle = kernel32.CreateFileW(
            str(path),
            FileTraverse | FileReadAttributes | Synchronize,
            FileShareRead | FileShareWrite | FileShareDelete,
            None,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            None,
        )
        if handle == InvalidHandleValue:
            _raise_last_error("CreateFileW directory open")
        return handle

    def _directory_identity(path: Path) -> tuple[int, int]:
        handle = _open_directory(path)
        try:
            return _handle_identity(handle)
        finally:
            _close(handle)

    def _open_relative(root_handle: int, name: str, access: int, disposition: int) -> int:
        buffer = ctypes.create_unicode_buffer(name)
        object_name = UnicodeString(
            len(name) * ctypes.sizeof(ctypes.c_wchar),
            (len(name) + 1) * ctypes.sizeof(ctypes.c_wchar),
            ctypes.cast(buffer, wintypes.LPWSTR),
        )
        attributes = ObjectAttributes(
            ctypes.sizeof(ObjectAttributes),
            root_handle,
            ctypes.pointer(object_name),
            ObjCaseInsensitive,
            None,
            None,
        )
        status = IoStatusBlock()
        handle = wintypes.HANDLE()
        result = ntdll.NtCreateFile(
            ctypes.byref(handle),
            access,
            ctypes.byref(attributes),
            ctypes.byref(status),
            None,
            FileAttributeNormal,
            FileShareRead,
            disposition,
            FileSynchronousIoNonAlert | FileNonDirectoryFile | FileFlagOpenReparsePoint,
            None,
            0,
        )
        if result < 0:
            raise OSError(result, "NtCreateFile relative open")
        return handle.value

    def _copy_contents(source_handle: int, destination_handle: int) -> None:
        buffer = ctypes.create_string_buffer(8192)
        while True:
            read = wintypes.DWORD()
            if not kernel32.ReadFile(source_handle, buffer, len(buffer), ctypes.byref(read), None):
                _raise_last_error("ReadFile")
            if read.value == 0:
                break
            offset = 0
            while offset < read.value:
                written = wintypes.DWORD()
                if not kernel32.WriteFile(
                    destination_handle,
                    ctypes.byref(buffer, offset),
                    read.value - offset,
                    ctypes.byref(written),
                    None,
                ):
                    _raise_last_error("WriteFile")
                assert written.value > 0
                offset += written.value
        if not kernel32.FlushFileBuffers(destination_handle):
            _raise_last_error("FlushFileBuffers")

    def model_once_windows(seed: int) -> int:
        rng = random.Random(seed)
        checks = 0
        with tempfile.TemporaryDirectory(prefix="fileop-copy-model-") as temporary:
            root = Path(temporary)
            source = root / "source"
            moved_source = root / "source-moved"
            destination = root / "destination"
            moved_destination = root / "destination-moved"
            source.mkdir()
            destination.mkdir()
            payload = rng.randbytes(rng.randrange(0, 65_536))
            source_item = source / "item.bin"
            source_item.write_bytes(payload)

            expected_source_directory_identity = identity(os.stat(source, follow_symlinks=False))
            expected_destination_identity = identity(os.stat(destination, follow_symlinks=False))

            _rename_with_retry(source, moved_source)
            source.mkdir()
            replaced_source_directory = _open_directory(source)
            try:
                assert identity(os.stat(source, follow_symlinks=False)) != expected_source_directory_identity
                checks += 1
            finally:
                _close(replaced_source_directory)
            os.rmdir(source)
            _rename_with_retry(moved_source, source)

            _rename_with_retry(destination, moved_destination)
            destination.mkdir()
            replaced_destination_directory = _open_directory(destination)
            try:
                assert identity(os.stat(destination, follow_symlinks=False)) != expected_destination_identity
                checks += 1
            finally:
                _close(replaced_destination_directory)
            os.rmdir(destination)
            _rename_with_retry(moved_destination, destination)

            source_directory = _open_directory(source)
            destination_directory = _open_directory(destination)
            try:
                assert _handle_identity(source_directory)[1] != 0
                assert _handle_identity(destination_directory)[1] != 0
                checks += 2
                initial_source_file = _open_relative(source_directory, "item.bin", FileReadData | FileReadAttributes | Synchronize, FileOpen)
                try:
                    expected_source_identity = _handle_identity(initial_source_file)
                finally:
                    _close(initial_source_file)

                moved_source_item = source / "item.original.bin"
                _rename_with_retry(source_item, moved_source_item)
                source_item.write_bytes(b"replacement")
                replacement_source_file = _open_relative(
                    source_directory,
                    "item.bin",
                    FileReadData | FileReadAttributes | Synchronize,
                    FileOpen,
                )
                try:
                    assert _handle_identity(replacement_source_file) != expected_source_identity
                    checks += 1
                finally:
                    _close(replacement_source_file)
                source_item.unlink()
                _rename_with_retry(moved_source_item, source_item)

                source_file = _open_relative(
                    source_directory,
                    "item.bin",
                    FileReadData | FileReadAttributes | Synchronize,
                    FileOpen,
                )
                try:
                    assert _handle_identity(source_file) == expected_source_identity
                    checks += 1
                    _rename_with_retry(destination, moved_destination)
                    destination.mkdir()
                    destination_file = _open_relative(
                        destination_directory,
                        "item.bin",
                        GenericWrite | FileReadAttributes | Synchronize,
                        FileCreate,
                    )
                    try:
                        _copy_contents(source_file, destination_file)
                        checks += 2
                    finally:
                        _close(destination_file)
                    assert (moved_destination / "item.bin").read_bytes() == payload
                    assert not (destination / "item.bin").exists()
                    checks += 2
                    try:
                        _open_relative(destination_directory, "item.bin", GenericWrite | FileReadAttributes | Synchronize, FileCreate)
                    except OSError:
                        checks += 1
                    else:
                        raise AssertionError("exclusive relative create opened or overwrote an existing destination")
                finally:
                    _close(source_file)
            finally:
                _close(destination_directory)
                _close(source_directory)
        return checks
def _rename_with_retry(source: Path, destination: Path) -> None:
    last_error: PermissionError | None = None
    for _ in range(50):
        try:
            os.rename(source, destination)
            return
        except PermissionError as error:
            last_error = error
            if os.name != "nt":
                raise
            time.sleep(0.01)
    assert last_error is not None
    raise last_error

def model_once(seed: int) -> int:
    rng = random.Random(seed)
    checks = 0
    with tempfile.TemporaryDirectory(prefix="fileop-copy-model-") as temporary:
        root = Path(temporary)
        source = root / "source"
        moved_source = root / "source-moved"
        destination = root / "destination"
        moved_destination = root / "destination-moved"
        source.mkdir()
        destination.mkdir()
        payload = rng.randbytes(rng.randrange(0, 65_536))
        source_item = source / "item.bin"
        source_item.write_bytes(payload)

        expected_source_directory_identity = identity(
            os.stat(source, follow_symlinks=False)
        )
        expected_destination_identity = identity(
            os.stat(destination, follow_symlinks=False)
        )
        expected_source_identity = identity(
            os.stat(source_item, follow_symlinks=False)
        )

        # A source-root object swapped after fresh validation must be rejected by
        # stable directory identity before the source leaf is opened.
        _rename_with_retry(source, moved_source)
        source.mkdir()
        replaced_source_directory = os.open(
            source,
            os.O_RDONLY | getattr(os, "O_DIRECTORY", 0),
        )
        try:
            assert (
                identity(os.fstat(replaced_source_directory))
                != expected_source_directory_identity
            )
            checks += 1
        finally:
            os.close(replaced_source_directory)
        os.rmdir(source)
        _rename_with_retry(moved_source, source)

        # A destination-root object swapped after fresh validation must likewise
        # be rejected before exclusive creation.
        _rename_with_retry(destination, moved_destination)
        destination.mkdir()
        replaced_destination_directory = os.open(
            destination,
            os.O_RDONLY | getattr(os, "O_DIRECTORY", 0),
        )
        try:
            assert (
                identity(os.fstat(replaced_destination_directory))
                != expected_destination_identity
            )
            checks += 1
        finally:
            os.close(replaced_destination_directory)
        os.rmdir(destination)
        _rename_with_retry(moved_destination, destination)

        source_directory = os.open(
            source,
            os.O_RDONLY | getattr(os, "O_DIRECTORY", 0),
        )
        destination_directory = os.open(
            destination,
            os.O_RDONLY | getattr(os, "O_DIRECTORY", 0),
        )
        try:
            assert (
                identity(os.fstat(source_directory))
                == expected_source_directory_identity
            )
            assert (
                identity(os.fstat(destination_directory))
                == expected_destination_identity
            )
            checks += 2

            # Replacing the validated source leaf with a different object must be
            # observable through the relative handle open and stable file identity.
            moved_source_item = source / "item.original.bin"
            _rename_with_retry(source_item, moved_source_item)
            source_item.write_bytes(b"replacement")
            replacement_source_file = os.open(
                "item.bin",
                os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0),
                dir_fd=source_directory,
            )
            try:
                assert identity(os.fstat(replacement_source_file)) != expected_source_identity
                checks += 1
            finally:
                os.close(replacement_source_file)
            source_item.unlink()
            _rename_with_retry(moved_source_item, source_item)

            source_file = os.open(
                "item.bin",
                os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0),
                dir_fd=source_directory,
            )
            try:
                assert identity(os.fstat(source_file)) == expected_source_identity
                checks += 1

                # Once the directory handle is acquired, replacing its textual path
                # cannot redirect a relative create through that handle.
                _rename_with_retry(destination, moved_destination)
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
                    assert identity(os.fstat(source_file)) == expected_source_identity
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

    required_tests = [
        "CopyCreatesExclusiveIdentityBoundDestinationAndLeaseBlocksDelete",
        "LeaseBlocksParentDirectoryRenameUntilDisposed",
        "CollisionAppearingAfterValidationNeverOverwritesExistingFile",
        "SourceIdentityReplacementAfterValidationFailsBeforeDestinationCreation",
        "SourceRootReplacementAfterValidationFailsBeforeDestinationCreation",
        "DestinationRootReplacementAfterValidationFailsBeforeCreation",
        "PrimitiveRejectsRootWithoutStableIdentity",
    ]
    for test_name in required_tests:
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
    return len(required_source) + len(required_executor) + len(required_tests) + 6


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--cases", type=int, default=2000)
    parser.add_argument("--self-test-only", action="store_true")
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error("--cases must be greater than zero")

    model = model_once_windows if os.name == "nt" else model_once
    checks = sum(model(20260808 + case) for case in range(args.cases))
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
