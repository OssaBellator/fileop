#!/usr/bin/env python3
"""Zero-Actions checks for FileOp's handle-bound Copy basic metadata boundary."""
from __future__ import annotations

import argparse
import random
from pathlib import Path

READ_ONLY = 0x00000001
HIDDEN = 0x00000002
SYSTEM = 0x00000004
ARCHIVE = 0x00000020
NORMAL = 0x00000080
TEMPORARY = 0x00000100
SPARSE = 0x00000200
REPARSE_POINT = 0x00000400
COMPRESSED = 0x00000800
OFFLINE = 0x00001000
NOT_CONTENT_INDEXED = 0x00002000
ENCRYPTED = 0x00004000
INTEGRITY_STREAM = 0x00008000
PRESERVED = READ_ONLY | HIDDEN | SYSTEM | ARCHIVE | NOT_CONTENT_INDEXED
DESTINATION_SETTABLE = TEMPORARY | OFFLINE
NON_SETTABLE_STORAGE = SPARSE | REPARSE_POINT | COMPRESSED | ENCRYPTED | INTEGRITY_STREAM
KNOWN_DESTINATION = PRESERVED | NORMAL | DESTINATION_SETTABLE | NON_SETTABLE_STORAGE


def sanitize(attributes: int) -> int:
    preserved = attributes & PRESERVED
    return preserved if preserved else NORMAL


def merge_destination(destination_attributes: int, source_attributes: int) -> int:
    destination_owned = destination_attributes & DESTINATION_SETTABLE
    source_preserved = source_attributes & PRESERVED
    merged = destination_owned | source_preserved
    return merged if merged else NORMAL


def run_model(cases: int) -> int:
    rng = random.Random(20260809)
    checks = 0
    fixed = [
        0,
        NORMAL,
        READ_ONLY,
        HIDDEN | ARCHIVE,
        PRESERVED,
        TEMPORARY,
        SPARSE,
        REPARSE_POINT,
        COMPRESSED,
        OFFLINE,
        ENCRYPTED,
        INTEGRITY_STREAM,
    ]
    values = fixed + [rng.getrandbits(32) for _ in range(cases)]
    for value in values:
        result = sanitize(value)
        assert result & ~(PRESERVED | NORMAL) == 0
        checks += 1
        if value & PRESERVED:
            assert result == value & PRESERVED
            assert result & NORMAL == 0
            checks += 2
        else:
            assert result == NORMAL
            checks += 1

        destination = rng.getrandbits(32) & KNOWN_DESTINATION
        merged = merge_destination(destination, result)
        expected_owned = destination & DESTINATION_SETTABLE
        expected_preserved = value & PRESERVED
        assert merged & DESTINATION_SETTABLE == expected_owned
        assert merged & PRESERVED == expected_preserved
        assert merged & NON_SETTABLE_STORAGE == 0
        assert (merged & NORMAL) == (NORMAL if (expected_owned | expected_preserved) == 0 else 0)
        checks += 4
    return checks


def check_repository(root: Path) -> int:
    helper_path = root / "src/FileOp.Windows/Operations/WindowsFileCopyBasicMetadata.cs"
    primitive_path = root / "src/FileOp.Windows/Operations/WindowsFileCopyMutationPrimitive.cs"
    tests_path = root / "tests/FileOp.Windows.Tests/WindowsFileCopyMutationPrimitiveMetadataTests.cs"
    wrapper_path = root / "tools/test-copy-executor-local.ps1"
    windows_wrapper_path = root / "tools/test-windows-copy-local.ps1"
    windows_cmd_path = root / "tools/test-windows-copy-local.cmd"
    missing = [
        str(path)
        for path in (
            helper_path,
            primitive_path,
            tests_path,
            wrapper_path,
            windows_wrapper_path,
            windows_cmd_path,
        )
        if not path.is_file()
    ]
    if missing:
        raise FileNotFoundError(", ".join(missing))

    helper = helper_path.read_text(encoding="utf-8")
    primitive = primitive_path.read_text(encoding="utf-8")
    tests = tests_path.read_text(encoding="utf-8")
    wrapper = wrapper_path.read_text(encoding="utf-8")
    windows_wrapper = windows_wrapper_path.read_text(encoding="utf-8")
    windows_cmd = windows_cmd_path.read_text(encoding="utf-8")

    required_helper = [
        "WindowsFileCopyBasicMetadata",
        "Capture(SafeFileHandle sourceHandle)",
        "SuppressAutomaticTimestampUpdates(SafeFileHandle destinationHandle)",
        "Apply(SafeFileHandle destinationHandle, Snapshot snapshot)",
        "MergeDestinationAttributes(",
        "DestinationOwnedSettableAttributeMask",
        "GetFileInformationByHandle(",
        "SetFileInformationByHandle(",
        "FileBasicInfo",
        "LastAccessTime = -1",
        "LastWriteTime = -1",
        "ChangeTime = 0",
        "FileAttributes = 0",
        "destinationInformation.FileAttributes",
        "destinationAttributes & DestinationOwnedSettableAttributeMask",
        "StructLayout(LayoutKind.Sequential, Pack = 8)",
        "FileAttributeReadOnly",
        "FileAttributeHidden",
        "FileAttributeSystem",
        "FileAttributeArchive",
        "FileAttributeTemporary",
        "FileAttributeOffline",
        "FileAttributeNotContentIndexed",
        "sourceAttributes & PreservedAttributeMask",
        "(uint)FileAttributes.Directory",
        "(uint)FileAttributes.ReparsePoint",
    ]
    for needle in required_helper:
        assert needle in helper, needle

    forbidden_helper = [
        "LastAccessTime = -2",
        "LastWriteTime = -2",
        "File.SetCreationTime",
        "File.SetLastAccessTime",
        "File.SetLastWriteTime",
        "File.SetAttributes",
        "SetFileAttributesW",
        "CreateFileW(",
    ]
    for needle in forbidden_helper:
        assert needle not in helper, needle

    capture = primitive.index("WindowsFileCopyBasicMetadata.Capture(sourceFile)")
    destination_read_access = primitive.index(
        "GenericWrite | FileReadAttributes | Synchronize",
        capture,
    )
    suppress = primitive.index(
        "WindowsFileCopyBasicMetadata.SuppressAutomaticTimestampUpdates(destinationFile)",
        destination_read_access,
    )
    copy = primitive.index("CopyContents(sourceFile, destinationFile)", suppress)
    data_flush = primitive.index("FlushFileBuffers(destinationFile)", copy)
    apply = primitive.index("WindowsFileCopyBasicMetadata.Apply(destinationFile, sourceMetadata)", data_flush)
    metadata_flush = primitive.index("FlushFileBuffers(destinationFile)", apply)
    validate_destination = primitive.index("ValidateCreatedFileHandle(", metadata_flush)
    assert capture < destination_read_access < suppress < copy < data_flush < apply < metadata_flush < validate_destination

    for test_name in [
        "BasicMetadataHelperPreservesTimestampsAndSafeAttributes",
        "BasicMetadataCaptureDropsUnsupportedStorageStateAttributes",
        "BasicMetadataMergePreservesDestinationOwnedAttributes",
        "CopyPrimitivePreservesSafeBasicMetadata",
    ]:
        assert test_name in tests, test_name

    helper_test_start = tests.index("public void BasicMetadataHelperPreservesTimestampsAndSafeAttributes()")
    helper_test_end = tests.index("public void BasicMetadataCaptureDropsUnsupportedStorageStateAttributes()", helper_test_start)
    helper_test = tests[helper_test_start:helper_test_end]
    assert "FileAccess.ReadWrite" in helper_test
    assert "File.SetAttributes(destinationPath, FileAttributes.Temporary)" in helper_test
    assert "SuppressAutomaticTimestampUpdates(destinationHandle)" in helper_test
    assert "FileAttributes.Temporary) != 0" in helper_test

    merge_test_start = tests.index("public void BasicMetadataMergePreservesDestinationOwnedAttributes()")
    merge_test_end = tests.index("public async Task CopyPrimitivePreservesSafeBasicMetadata()", merge_test_start)
    merge_test = tests[merge_test_start:merge_test_end]
    for attribute in [
        "FileAttributes.Temporary",
        "FileAttributes.Offline",
        "FileAttributes.SparseFile",
        "FileAttributes.Compressed",
        "FileAttributes.Encrypted",
    ]:
        assert attribute in merge_test, attribute
    assert "Assert.AreEqual(0u, merged & destinationNonSettableStorage)" in merge_test

    metadata_setup_start = tests.index("private static void SetExpectedMetadata(")
    metadata_setup_end = tests.index("private static void AssertMetadata(", metadata_setup_start)
    metadata_setup = tests[metadata_setup_start:metadata_setup_end]
    preserved_test_attributes = [
        "FileAttributes.ReadOnly",
        "FileAttributes.Hidden",
        "FileAttributes.System",
        "FileAttributes.Archive",
        "FileAttributes.NotContentIndexed",
    ]
    for attribute in preserved_test_attributes:
        assert attribute in metadata_setup, attribute

    primitive_test_start = tests.index("public async Task CopyPrimitivePreservesSafeBasicMetadata()")
    primitive_test_end = tests.index("private static void SetExpectedMetadata(", primitive_test_start)
    primitive_test = tests[primitive_test_start:primitive_test_end]
    validation = primitive_test.index("WindowsFileOperationExecutionValidator().ValidateAsync(plan)")
    establish_metadata = primitive_test.index(
        "SetExpectedMetadata(sourcePath, expectedCreation, expectedAccess, expectedWrite)",
        validation,
    )
    mutation = primitive_test.index("CopyNewFileAsync(", establish_metadata)
    assert validation < establish_metadata < mutation

    metadata_assertion = tests.index("AssertMetadata(", tests.index("CopyPrimitivePreservesSafeBasicMetadata"))
    content_read = tests.index("File.ReadAllTextAsync(destinationPath)", metadata_assertion)
    assert metadata_assertion < content_read

    assert "verify_copy_basic_metadata.py" in wrapper
    assert "WindowsFileCopyMutationPrimitiveMetadataTests" in windows_wrapper
    assert "powershell.exe -NoProfile -ExecutionPolicy Bypass -File" in windows_cmd
    assert "test-windows-copy-local.ps1" in windows_cmd
    assert "gh workflow" not in windows_cmd.lower()
    assert "gh run" not in windows_cmd.lower()
    return len(required_helper) + len(forbidden_helper) + len(preserved_test_attributes) + 35


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--self-test-only", action="store_true")
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error("--cases must be greater than zero")

    checks = run_model(args.cases)
    print(f"PASS Copy basic metadata mask/merge model: {checks} checks across {args.cases} randomized cases")
    if not args.self_test_only:
        print(f"PASS Copy basic metadata source wiring: {check_repository(args.repo_root.resolve())} checks")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
