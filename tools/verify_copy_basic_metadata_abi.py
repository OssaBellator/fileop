#!/usr/bin/env python3
"""Verify FileOp's Windows Copy basic-metadata interop boundary without Actions."""
from __future__ import annotations

import argparse
import shutil
import subprocess
import tempfile
from pathlib import Path


BASIC_FIELDS = [
    ("CreationTime", 8, 8),
    ("LastAccessTime", 8, 8),
    ("LastWriteTime", 8, 8),
    ("ChangeTime", 8, 8),
    ("FileAttributes", 4, 4),
]
BY_HANDLE_FIELDS = [
    ("FileAttributes", 4, 4),
    ("CreationTime", 8, 4),
    ("LastAccessTime", 8, 4),
    ("LastWriteTime", 8, 4),
    ("VolumeSerialNumber", 4, 4),
    ("FileSizeHigh", 4, 4),
    ("FileSizeLow", 4, 4),
    ("NumberOfLinks", 4, 4),
    ("FileIndexHigh", 4, 4),
    ("FileIndexLow", 4, 4),
]


def align(value: int, alignment: int) -> int:
    return (value + alignment - 1) // alignment * alignment


def layout(fields: list[tuple[str, int, int]], pack: int) -> tuple[int, dict[str, int]]:
    offset = 0
    max_alignment = 1
    offsets: dict[str, int] = {}
    for name, size, natural_alignment in fields:
        field_alignment = min(pack, natural_alignment)
        offset = align(offset, field_alignment)
        offsets[name] = offset
        offset += size
        max_alignment = max(max_alignment, field_alignment)
    return align(offset, max_alignment), offsets


def check_layout_model() -> int:
    basic_size, basic_offsets = layout(BASIC_FIELDS, pack=8)
    by_handle_size, by_handle_offsets = layout(BY_HANDLE_FIELDS, pack=8)
    assert basic_size == 40
    assert basic_offsets == {
        "CreationTime": 0,
        "LastAccessTime": 8,
        "LastWriteTime": 16,
        "ChangeTime": 24,
        "FileAttributes": 32,
    }
    assert by_handle_size == 52
    assert by_handle_offsets == {
        "FileAttributes": 0,
        "CreationTime": 4,
        "LastAccessTime": 12,
        "LastWriteTime": 20,
        "VolumeSerialNumber": 28,
        "FileSizeHigh": 32,
        "FileSizeLow": 36,
        "NumberOfLinks": 40,
        "FileIndexHigh": 44,
        "FileIndexLow": 48,
    }
    return 4


def ordered(block: str, needles: list[str]) -> None:
    cursor = 0
    for needle in needles:
        cursor = block.index(needle, cursor) + len(needle)


def require_pack_8_before(helper: str, struct_start: int) -> None:
    assert "[StructLayout(LayoutKind.Sequential, Pack = 8)]" in helper[max(0, struct_start - 100):struct_start]


def require_kernel32_bool_import(helper: str, method_start: int) -> None:
    attribute_start = helper.rfind("[DllImport(", 0, method_start)
    assert attribute_start >= 0
    attributes = helper[attribute_start:method_start]
    for needle in (
        '"kernel32.dll"',
        "SetLastError = true",
        "CallingConvention = CallingConvention.Winapi",
        "[return: MarshalAs(UnmanagedType.Bool)]",
    ):
        assert needle in attributes, needle


def check_repository(root: Path) -> int:
    helper_path = root / "src/FileOp.Windows/Operations/WindowsFileCopyBasicMetadata.cs"
    interop_tests_path = root / "tests/FileOp.Windows.Tests/WindowsFileCopyBasicMetadataInteropTests.cs"
    dotnet_probe_path = root / "tools/verify_copy_basic_metadata_dotnet.py"
    windows_wrapper_path = root / "tools/test-windows-copy-local.ps1"
    for path in (helper_path, interop_tests_path, dotnet_probe_path, windows_wrapper_path):
        if not path.is_file():
            raise FileNotFoundError(path)

    helper = helper_path.read_text(encoding="utf-8")
    interop_tests = interop_tests_path.read_text(encoding="utf-8")
    dotnet_probe = dotnet_probe_path.read_text(encoding="utf-8")
    windows_wrapper = windows_wrapper_path.read_text(encoding="utf-8")

    basic_start = helper.index("private struct FileBasicInformation")
    basic_end = helper.index("[StructLayout", basic_start)
    basic_block = helper[basic_start:basic_end]
    ordered(
        basic_block,
        [
            "public long CreationTime;",
            "public long LastAccessTime;",
            "public long LastWriteTime;",
            "public long ChangeTime;",
            "public uint FileAttributes;",
        ],
    )
    require_pack_8_before(helper, basic_start)

    by_handle_start = helper.index("private struct ByHandleFileInformation")
    by_handle_end = helper.index("[StructLayout", by_handle_start)
    by_handle_block = helper[by_handle_start:by_handle_end]
    ordered(
        by_handle_block,
        [
            "public uint FileAttributes;",
            "public FileTime CreationTime;",
            "public FileTime LastAccessTime;",
            "public FileTime LastWriteTime;",
            "public uint VolumeSerialNumber;",
            "public uint FileSizeHigh;",
            "public uint FileSizeLow;",
            "public uint NumberOfLinks;",
            "public uint FileIndexHigh;",
            "public uint FileIndexLow;",
        ],
    )
    require_pack_8_before(helper, by_handle_start)

    file_time_start = helper.index("private struct FileTime")
    file_time_end = helper.index("[DllImport", file_time_start)
    file_time_block = helper[file_time_start:file_time_end]
    ordered(file_time_block, ["public uint LowDateTime;", "public uint HighDateTime;"])
    require_pack_8_before(helper, file_time_start)

    enum_start = helper.index("private enum FileInfoByHandleClass")
    enum_end = helper.index("[StructLayout", enum_start)
    enum_block = helper[enum_start:enum_end]
    assert "private enum FileInfoByHandleClass : int" in enum_block
    assert "FileBasicInfo = 0" in enum_block

    suppress_start = helper.index("internal static void SuppressAutomaticTimestampUpdates")
    suppress_end = helper.index("internal static void Apply(", suppress_start)
    suppress = helper[suppress_start:suppress_end]
    for needle in (
        "CreationTime = 0",
        "LastAccessTime = -1",
        "LastWriteTime = -1",
        "ChangeTime = 0",
        "FileAttributes = 0",
    ):
        assert needle in suppress, needle
    assert "-2" not in suppress

    apply_start = suppress_end
    apply_end = helper.index("internal static uint MergeDestinationAttributes", apply_start)
    apply = helper[apply_start:apply_end]
    ordered(
        apply,
        [
            "GetFileInformationByHandle(destinationHandle, out var destinationInformation)",
            "CreationTime = snapshot.CreationTime",
            "LastAccessTime = snapshot.LastAccessTime",
            "LastWriteTime = snapshot.LastWriteTime",
            "ChangeTime = 0",
            "FileAttributes = MergeDestinationAttributes(",
            "SetBasicInformation(",
        ],
    )

    wrapper_start = helper.index("private static void SetBasicInformation(")
    wrapper_end = helper.index("private static void ValidateHandle(", wrapper_start)
    wrapper = helper[wrapper_start:wrapper_end]
    ordered(
        wrapper,
        [
            "SetFileInformationByHandle(",
            "handle",
            "FileInfoByHandleClass.FileBasicInfo",
            "ref information",
            "(uint)Marshal.SizeOf<FileBasicInformation>()",
        ],
    )

    set_method = helper.index("private static extern bool SetFileInformationByHandle(")
    set_signature = helper[set_method:set_method + 450]
    ordered(
        set_signature,
        [
            "SafeFileHandle hFile",
            "FileInfoByHandleClass fileInformationClass",
            "ref FileBasicInformation lpFileInformation",
            "uint dwBufferSize",
        ],
    )
    require_kernel32_bool_import(helper, set_method)

    get_method = helper.index("private static extern bool GetFileInformationByHandle(")
    get_signature = helper[get_method:get_method + 300]
    ordered(
        get_signature,
        [
            "SafeFileHandle hFile",
            "out ByHandleFileInformation lpFileInformation",
        ],
    )
    require_kernel32_bool_import(helper, get_method)

    for needle in (
        "FileBasicInfoInteropContractMatchesWindowsAbi",
        "ByHandleInformationInteropContractMatchesWindowsAbi",
        "Marshal.SizeOf",
        "Marshal.OffsetOf",
        "BindingFlags.NonPublic",
        "DllImportAttribute",
        "MarshalAsAttribute",
        "CallingConvention.Winapi",
        "FileBasicInfo",
        "SetLastError",
        "FileIndexLow",
        "HighDateTime",
    ):
        assert needle in interop_tests, needle

    for needle in (
        "<TargetFramework>net10.0</TargetFramework>",
        "<LangVersion>14.0</LangVersion>",
        "<EnableNETAnalyzers>false</EnableNETAnalyzers>",
        "<NuGetAudit>false</NuGetAudit>",
        "shutil.copy2(helper",
        "SanitizeAttributes",
        "MergeDestinationAttributes",
        "implementationChecks",
        "Marshal.SizeOf(basic)",
        "Marshal.OffsetOf(type, field)",
        "DllImportAttribute",
        "MarshalAsAttribute",
        "CallingConvention.Winapi",
        "SetFileInformationByHandle",
        "GetFileInformationByHandle",
        "dotnet, \"restore\"",
        "dotnet, \"build\"",
        "dotnet, \"run\"",
    ):
        assert needle in dotnet_probe, needle
    assert "PackageReference" not in dotnet_probe

    assert "verify_copy_basic_metadata_dotnet.py" in windows_wrapper
    assert "WindowsFileCopyBasicMetadataInteropTests" in windows_wrapper
    return 68


C_PROBE = r"""
typedef signed long long i64;
typedef unsigned int u32;
typedef struct {
    i64 CreationTime;
    i64 LastAccessTime;
    i64 LastWriteTime;
    i64 ChangeTime;
    u32 FileAttributes;
} FILE_BASIC_INFO_MODEL;
typedef struct { u32 LowDateTime; u32 HighDateTime; } FILETIME_MODEL;
typedef struct {
    u32 FileAttributes;
    FILETIME_MODEL CreationTime;
    FILETIME_MODEL LastAccessTime;
    FILETIME_MODEL LastWriteTime;
    u32 VolumeSerialNumber;
    u32 FileSizeHigh;
    u32 FileSizeLow;
    u32 NumberOfLinks;
    u32 FileIndexHigh;
    u32 FileIndexLow;
} BY_HANDLE_FILE_INFORMATION_MODEL;
_Static_assert(sizeof(FILE_BASIC_INFO_MODEL) == 40, "FILE_BASIC_INFO size");
_Static_assert(__builtin_offsetof(FILE_BASIC_INFO_MODEL, FileAttributes) == 32, "FILE_BASIC_INFO FileAttributes offset");
_Static_assert(sizeof(BY_HANDLE_FILE_INFORMATION_MODEL) == 52, "BY_HANDLE_FILE_INFORMATION size");
_Static_assert(__builtin_offsetof(BY_HANDLE_FILE_INFORMATION_MODEL, FileIndexLow) == 48, "BY_HANDLE_FILE_INFORMATION FileIndexLow offset");
int fileop_copy_metadata_abi_probe;
"""


def run_clang_probe() -> int:
    clang = shutil.which("clang")
    if clang is None:
        raise RuntimeError("--clang requested but clang was not found")
    targets = (
        "x86_64-pc-windows-msvc",
        "i686-pc-windows-msvc",
        "aarch64-pc-windows-msvc",
    )
    with tempfile.TemporaryDirectory(prefix="fileop-copy-metadata-abi-") as temp_dir:
        source = Path(temp_dir) / "probe.c"
        source.write_text(C_PROBE, encoding="utf-8")
        for target in targets:
            output = Path(temp_dir) / (target + ".obj")
            subprocess.run(
                [clang, "-target", target, "-std=c11", "-c", str(source), "-o", str(output)],
                check=True,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
            )
            assert output.is_file() and output.stat().st_size > 0
    return len(targets) * 4


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument(
        "--clang",
        action="store_true",
        help="also cross-compile x64/x86/ARM64 Windows COFF ABI probes",
    )
    parser.add_argument("--self-test-only", action="store_true")
    args = parser.parse_args()

    layout_checks = check_layout_model()
    print(f"PASS Copy basic metadata ABI layout model: {layout_checks} checks")
    if not args.self_test_only:
        source_checks = check_repository(args.repo_root.resolve())
        print(f"PASS Copy basic metadata C#/PInvoke source contract: {source_checks} checks")
    if args.clang:
        clang_checks = run_clang_probe()
        print(f"PASS Copy basic metadata x64/x86/ARM64 Windows COFF ABI probe: {clang_checks} checks")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
