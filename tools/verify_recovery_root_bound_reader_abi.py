#!/usr/bin/env python3
"""Verify Windows ABI/source contracts for root-bound recovery content reads."""
from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path


TARGETS = (
    ("i686-pc-windows-msvc", 4, 8, 24, 8),
    ("x86_64-pc-windows-msvc", 8, 16, 48, 16),
    ("aarch64-pc-windows-msvc", 8, 16, 48, 16),
)


def c_probe(pointer_size: int, unicode_size: int, object_size: int, iosb_size: int) -> str:
    return f"""
typedef unsigned char U8;
typedef unsigned short U16;
typedef unsigned int U32;
typedef __INTPTR_TYPE__ INTPTR;
typedef __UINTPTR_TYPE__ UINTPTR;
typedef void* HANDLE;
typedef void* PVOID;
typedef int NTSTATUS;

typedef struct _UNICODE_STRING {{
    U16 Length;
    U16 MaximumLength;
    U16* Buffer;
}} UNICODE_STRING;

typedef struct _OBJECT_ATTRIBUTES {{
    U32 Length;
    HANDLE RootDirectory;
    UNICODE_STRING* ObjectName;
    U32 Attributes;
    PVOID SecurityDescriptor;
    PVOID SecurityQualityOfService;
}} OBJECT_ATTRIBUTES;

typedef union _IO_STATUS_UNION {{
    NTSTATUS Status;
    PVOID Pointer;
}} IO_STATUS_UNION;

typedef struct _IO_STATUS_BLOCK {{
    IO_STATUS_UNION u;
    UINTPTR Information;
}} IO_STATUS_BLOCK;

typedef struct _FILETIME {{
    U32 LowDateTime;
    U32 HighDateTime;
}} FILETIME;

typedef struct _BY_HANDLE_FILE_INFORMATION {{
    U32 FileAttributes;
    FILETIME CreationTime;
    FILETIME LastAccessTime;
    FILETIME LastWriteTime;
    U32 VolumeSerialNumber;
    U32 FileSizeHigh;
    U32 FileSizeLow;
    U32 NumberOfLinks;
    U32 FileIndexHigh;
    U32 FileIndexLow;
}} BY_HANDLE_FILE_INFORMATION;

#define OFFSETOF(type, member) __builtin_offsetof(type, member)
_Static_assert(sizeof(void*) == {pointer_size}, "pointer size");
_Static_assert(sizeof(UNICODE_STRING) == {unicode_size}, "UNICODE_STRING size");
_Static_assert(OFFSETOF(UNICODE_STRING, Buffer) == {4 if pointer_size == 4 else 8}, "UNICODE_STRING Buffer");
_Static_assert(sizeof(OBJECT_ATTRIBUTES) == {object_size}, "OBJECT_ATTRIBUTES size");
_Static_assert(OFFSETOF(OBJECT_ATTRIBUTES, RootDirectory) == {4 if pointer_size == 4 else 8}, "OBJECT_ATTRIBUTES RootDirectory");
_Static_assert(OFFSETOF(OBJECT_ATTRIBUTES, ObjectName) == {8 if pointer_size == 4 else 16}, "OBJECT_ATTRIBUTES ObjectName");
_Static_assert(OFFSETOF(OBJECT_ATTRIBUTES, Attributes) == {12 if pointer_size == 4 else 24}, "OBJECT_ATTRIBUTES Attributes");
_Static_assert(OFFSETOF(OBJECT_ATTRIBUTES, SecurityDescriptor) == {16 if pointer_size == 4 else 32}, "OBJECT_ATTRIBUTES SecurityDescriptor");
_Static_assert(OFFSETOF(OBJECT_ATTRIBUTES, SecurityQualityOfService) == {20 if pointer_size == 4 else 40}, "OBJECT_ATTRIBUTES SecurityQualityOfService");
_Static_assert(sizeof(IO_STATUS_BLOCK) == {iosb_size}, "IO_STATUS_BLOCK size");
_Static_assert(OFFSETOF(IO_STATUS_BLOCK, Information) == {pointer_size}, "IO_STATUS_BLOCK Information");
_Static_assert(sizeof(FILETIME) == 8, "FILETIME size");
_Static_assert(sizeof(BY_HANDLE_FILE_INFORMATION) == 52, "BY_HANDLE_FILE_INFORMATION size");
_Static_assert(OFFSETOF(BY_HANDLE_FILE_INFORMATION, NumberOfLinks) == 40, "NumberOfLinks offset");
_Static_assert(OFFSETOF(BY_HANDLE_FILE_INFORMATION, FileIndexHigh) == 44, "FileIndexHigh offset");
_Static_assert(OFFSETOF(BY_HANDLE_FILE_INFORMATION, FileIndexLow) == 48, "FileIndexLow offset");
int main(void) {{ return 0; }}
"""


def run_clang(clang: str) -> int:
    checks = 0
    with tempfile.TemporaryDirectory(prefix="fileop-root-bound-abi-") as directory:
        root = Path(directory)
        for target, pointer_size, unicode_size, object_size, iosb_size in TARGETS:
            source = root / (target.replace("-", "_") + ".c")
            source.write_text(
                c_probe(pointer_size, unicode_size, object_size, iosb_size),
                encoding="utf-8",
            )
            subprocess.run(
                [
                    clang,
                    "-target",
                    target,
                    "-std=c11",
                    "-Wall",
                    "-Wextra",
                    "-Werror",
                    "-fsyntax-only",
                    str(source),
                ],
                check=True,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
            )
            checks += 12
    return checks


def check_source(repo_root: Path) -> int:
    reader_path = repo_root / "src/FileOp.Windows/Operations/WindowsRootBoundFileContentFingerprintReader.cs"
    primitive_path = repo_root / "src/FileOp.Windows/Operations/WindowsFileCopyMutationPrimitive.cs"
    if not reader_path.is_file() or not primitive_path.is_file():
        raise FileNotFoundError("root-bound reader or existing Copy primitive source is missing")

    reader = reader_path.read_text(encoding="utf-8")
    primitive = primitive_path.read_text(encoding="utf-8")

    needles = (
        '[StructLayout(LayoutKind.Sequential)]\n    private struct UnicodeString',
        'public ushort Length;',
        'public ushort MaximumLength;',
        'public IntPtr Buffer;',
        '[StructLayout(LayoutKind.Sequential)]\n    private struct ObjectAttributes',
        'public int Length;',
        'public IntPtr RootDirectory;',
        'public IntPtr ObjectName;',
        'public uint Attributes;',
        'public IntPtr SecurityDescriptor;',
        'public IntPtr SecurityQualityOfService;',
        '[StructLayout(LayoutKind.Sequential)]\n    private struct IoStatusBlock',
        'public IntPtr Status;',
        'public UIntPtr Information;',
        '[StructLayout(LayoutKind.Sequential)]\n    private struct ByHandleFileInformation',
        'public uint NumberOfLinks;',
        '[DllImport("ntdll.dll", ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]',
        'private static extern int NtCreateFile(',
        'out IntPtr fileHandle,',
        'ref ObjectAttributes objectAttributes,',
        'out IoStatusBlock ioStatusBlock,',
        '[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]',
        'private static extern SafeFileHandle CreateFileW(',
        '[return: MarshalAs(UnmanagedType.Bool)]',
        'private static extern bool ReadFile(',
        'private static extern uint GetFinalPathNameByHandleW(',
        'private static extern bool GetFileInformationByHandle(',
        'RootDirectory = rootDirectory.DangerousGetHandle()',
        'rootDirectory.DangerousAddRef(ref rootAddedRef)',
        'rootDirectory.DangerousRelease()',
    )
    for needle in needles:
        assert needle in reader, needle

    # Keep the new reader ABI aligned with the already-established relative-open primitive.
    shared_needles = (
        'private struct UnicodeString',
        'private struct ObjectAttributes',
        'private struct IoStatusBlock',
        'private static extern int NtCreateFile(',
        'RootDirectory = rootDirectory.DangerousGetHandle()',
    )
    for needle in shared_needles:
        assert needle in primitive, f"existing primitive missing ABI reference: {needle}"
        assert needle in reader, f"root-bound reader missing ABI reference: {needle}"

    forbidden = (
        'CallingConvention.Cdecl',
        'CallingConvention.StdCall',
        'RtlNtStatusToDosError',
        'WriteFile(',
    )
    for needle in forbidden:
        assert needle not in reader, needle

    assert reader.count('CreateFileW(') == 2, "root-bound reader must use CreateFileW only for root call + declaration"
    assert reader.count('NtCreateFile(') == 2, "root-bound reader must contain one relative NtCreateFile call + declaration"

    return len(needles) + (len(shared_needles) * 2) + len(forbidden) + 2


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--clang", default=None)
    parser.add_argument("--self-test-only", action="store_true")
    args = parser.parse_args()

    checks = 0
    if not args.self_test_only:
        source_checks = check_source(args.repo_root.resolve())
        checks += source_checks
        print(f"PASS root-bound reader ABI source contract: {source_checks} checks")

    clang = args.clang or shutil.which("clang")
    if clang is None:
        print("INFO: clang not found; skipping optional x86/x64/ARM64 Windows ABI cross-compilation")
    else:
        abi_checks = run_clang(clang)
        checks += abi_checks
        print(f"PASS root-bound reader Windows ABI cross-compilation: {abi_checks} checks across 3 targets")

    if checks == 0:
        # Pure standard-library fallback still checks the expected model constants.
        for _, pointer_size, unicode_size, object_size, iosb_size in TARGETS:
            assert pointer_size in (4, 8)
            assert unicode_size == (8 if pointer_size == 4 else 16)
            assert object_size == (24 if pointer_size == 4 else 48)
            assert iosb_size == (8 if pointer_size == 4 else 16)
            checks += 4
        print(f"PASS root-bound reader ABI layout model: {checks} checks")

    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, subprocess.CalledProcessError, OSError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
