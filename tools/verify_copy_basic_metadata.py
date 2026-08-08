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
NOT_CONTENT_INDEXED = 0x00002000
PRESERVED = READ_ONLY | HIDDEN | SYSTEM | ARCHIVE | NOT_CONTENT_INDEXED


def sanitize(attributes: int) -> int:
    preserved = attributes & PRESERVED
    return preserved if preserved else NORMAL


def run_model(cases: int) -> int:
    rng = random.Random(20260809)
    checks = 0
    fixed = [
        0,
        NORMAL,
        READ_ONLY,
        HIDDEN | ARCHIVE,
        PRESERVED,
        0x00000100,  # Temporary is deliberately not preserved.
        0x00000800,  # Compressed is deliberately not preserved.
        0x00004000,  # Encrypted is deliberately not preserved.
        0x00001000,  # Offline is deliberately not preserved.
    ]
    for value in fixed + [rng.getrandbits(32) for _ in range(cases)]:
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
    return checks


def check_repository(root: Path) -> int:
    helper_path = root / "src/FileOp.Windows/Operations/WindowsFileCopyBasicMetadata.cs"
    primitive_path = root / "src/FileOp.Windows/Operations/WindowsFileCopyMutationPrimitive.cs"
    tests_path = root / "tests/FileOp.Windows.Tests/WindowsFileCopyMutationPrimitiveMetadataTests.cs"
    wrapper_path = root / "tools/test-copy-executor-local.ps1"
    windows_wrapper_path = root / "tools/test-windows-copy-local.ps1"
    missing = [
        str(path)
        for path in (
            helper_path,
            primitive_path,
            tests_path,
            wrapper_path,
            windows_wrapper_path,
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

    required_helper = [
        "WindowsFileCopyBasicMetadata",
        "Capture(SafeFileHandle sourceHandle)",
        "SuppressAutomaticTimestampUpdates(SafeFileHandle destinationHandle)",
        "Apply(SafeFileHandle destinationHandle, Snapshot snapshot)",
        "GetFileInformationByHandle(",
        "SetFileInformationByHandle(",
        "FileBasicInfo",
        "LastAccessTime = -1",
        "LastWriteTime = -1",
        "ChangeTime = 0",
        "FileAttributes = 0",
        "StructLayout(LayoutKind.Sequential, Pack = 8)",
        "FileAttributeReadOnly",
        "FileAttributeHidden",
        "FileAttributeSystem",
        "FileAttributeArchive",
        "FileAttributeNotContentIndexed",
        "sourceAttributes & PreservedAttributeMask",
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
    suppress = primitive.index(
        "WindowsFileCopyBasicMetadata.SuppressAutomaticTimestampUpdates(destinationFile)",
        capture,
    )
    copy = primitive.index("CopyContents(sourceFile, destinationFile)", suppress)
    data_flush = primitive.index("FlushFileBuffers(destinationFile)", copy)
    apply = primitive.index("WindowsFileCopyBasicMetadata.Apply(destinationFile, sourceMetadata)", data_flush)
    metadata_flush = primitive.index("FlushFileBuffers(destinationFile)", apply)
    validate_destination = primitive.index("ValidateCreatedFileHandle(", metadata_flush)
    assert capture < suppress < copy < data_flush < apply < metadata_flush < validate_destination

    for test_name in [
        "BasicMetadataHelperPreservesTimestampsAndSafeAttributes",
        "BasicMetadataCaptureDropsUnsupportedStorageStateAttributes",
        "CopyPrimitivePreservesSafeBasicMetadata",
    ]:
        assert test_name in tests, test_name

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
    return len(required_helper) + len(forbidden_helper) + len(preserved_test_attributes) + 12


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--self-test-only", action="store_true")
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error("--cases must be greater than zero")

    checks = run_model(args.cases)
    print(f"PASS Copy basic metadata mask model: {checks} checks across {args.cases} randomized cases")
    if not args.self_test_only:
        print(f"PASS Copy basic metadata source wiring: {check_repository(args.repo_root.resolve())} checks")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
