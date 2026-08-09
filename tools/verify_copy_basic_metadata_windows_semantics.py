#!/usr/bin/env python3
"""Model the MS-FSA FileBasicInformation semantics used by Copy metadata."""
from __future__ import annotations

import argparse
import random
from dataclasses import dataclass
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
DESTINATION_OWNED = TEMPORARY | OFFLINE
VALID_SET_ATTRIBUTES = PRESERVED | DESTINATION_OWNED
NON_SETTABLE_STORAGE = SPARSE | REPARSE_POINT | COMPRESSED | ENCRYPTED | INTEGRITY_STREAM
ALLOWED_INPUT = VALID_SET_ATTRIBUTES | NORMAL
UNMANAGED = 0xFFFFFFFF & ~(VALID_SET_ATTRIBUTES | NORMAL)
MASK32 = 0xFFFFFFFF


def merge_destination(destination_attributes: int, source_attributes: int) -> int:
    """Mirror WindowsFileCopyBasicMetadata.MergeDestinationAttributes."""
    destination_owned = destination_attributes & DESTINATION_OWNED
    source_preserved = source_attributes & PRESERVED
    merged = destination_owned | source_preserved
    return merged if merged else NORMAL


def apply_file_basic_attributes(original_attributes: int, input_attributes: int) -> int:
    """Model MS-FSA 2.1.5.15.2 FileBasicInformation attribute replacement."""
    original_attributes &= MASK32
    input_attributes &= MASK32
    if input_attributes == 0:
        return original_attributes
    return (
        (original_attributes & (~VALID_SET_ATTRIBUTES & MASK32))
        | (input_attributes & VALID_SET_ATTRIBUTES)
    ) & MASK32


@dataclass(frozen=True)
class TimestampState:
    value: int
    user_set: bool


def apply_timestamp(state: TimestampState, requested: int) -> TimestampState:
    """Model the LastAccess/LastWrite sentinel behavior relevant to this Copy path."""
    if requested == 0:
        return state
    if requested == -2:
        return TimestampState(state.value, False)
    if requested == -1:
        return TimestampState(state.value, True)
    if requested < -2:
        raise ValueError("FileBasicInformation timestamps less than -2 are invalid")
    return TimestampState(requested, True)


def automatic_io_update(state: TimestampState, new_value: int) -> TimestampState:
    """Automatic handle-local access/write updates are suppressed after a user-set value."""
    if state.user_set:
        return state
    return TimestampState(new_value, False)


def check_source_contract(repo_root: Path) -> int:
    helper_path = repo_root / "src/FileOp.Windows/Operations/WindowsFileCopyBasicMetadata.cs"
    if not helper_path.is_file():
        raise FileNotFoundError(helper_path)
    helper = helper_path.read_text(encoding="utf-8")

    checks = 0
    for method_name in (
        "GetFileInformationByHandle",
        "SetFileInformationByHandle",
    ):
        method_start = helper.index(f"private static extern bool {method_name}(")
        attribute_start = helper.rfind("[DllImport(", 0, method_start)
        assert attribute_start >= 0, method_name
        attributes = helper[attribute_start:method_start]
        for needle in (
            '"kernel32.dll"',
            "SetLastError = true",
            "ExactSpelling = true",
            "CallingConvention = CallingConvention.Winapi",
            "[return: MarshalAs(UnmanagedType.Bool)]",
        ):
            assert needle in attributes, f"{method_name}: {needle}"
            checks += 1

    assert "ExactSpelling = false" not in helper
    checks += 1
    return checks


def run_fixed_cases() -> int:
    checks = 0

    destination = (
        READ_ONLY
        | SYSTEM
        | TEMPORARY
        | OFFLINE
        | SPARSE
        | COMPRESSED
        | ENCRYPTED
        | INTEGRITY_STREAM
        | 0x40000000
    )
    source = HIDDEN | ARCHIVE | TEMPORARY | COMPRESSED | 0x20000000
    basic_input = merge_destination(destination, source)
    result = apply_file_basic_attributes(destination, basic_input)

    assert basic_input == (TEMPORARY | OFFLINE | HIDDEN | ARCHIVE)
    checks += 1
    assert result & PRESERVED == HIDDEN | ARCHIVE
    checks += 1
    assert result & DESTINATION_OWNED == TEMPORARY | OFFLINE
    checks += 1
    assert result & NON_SETTABLE_STORAGE == destination & NON_SETTABLE_STORAGE
    checks += 1
    assert result & UNMANAGED == destination & UNMANAGED
    checks += 1

    clear_input = merge_destination(HIDDEN | ARCHIVE, NORMAL)
    assert clear_input == NORMAL
    checks += 1
    cleared = apply_file_basic_attributes(HIDDEN | ARCHIVE, clear_input)
    assert cleared & VALID_SET_ATTRIBUTES == 0
    checks += 1

    unchanged = apply_file_basic_attributes(destination, 0)
    assert unchanged == destination
    checks += 1

    initial = TimestampState(100, False)
    suppressed = apply_timestamp(initial, -1)
    assert suppressed == TimestampState(100, True)
    checks += 1
    after_copy_io = automatic_io_update(suppressed, 200)
    assert after_copy_io == suppressed
    checks += 1
    explicit = apply_timestamp(after_copy_io, 300)
    assert explicit == TimestampState(300, True)
    checks += 1
    after_flush = automatic_io_update(explicit, 400)
    assert after_flush == explicit
    checks += 1
    zero = apply_timestamp(explicit, 0)
    assert zero == explicit
    checks += 1
    reenabling = apply_timestamp(explicit, -2)
    assert reenabling == TimestampState(300, False)
    checks += 1
    assert automatic_io_update(reenabling, 500) == TimestampState(500, False)
    checks += 1

    try:
        apply_timestamp(initial, -3)
    except ValueError:
        checks += 1
    else:
        raise AssertionError("timestamps below -2 must be rejected")

    return checks


def run_randomized(cases: int) -> int:
    rng = random.Random(20260809)
    checks = 0
    for _ in range(cases):
        destination = rng.getrandbits(32)
        source = rng.getrandbits(32)
        basic_input = merge_destination(destination, source)
        result = apply_file_basic_attributes(destination, basic_input)

        assert basic_input & ~ALLOWED_INPUT == 0
        checks += 1
        assert basic_input & NON_SETTABLE_STORAGE == 0
        checks += 1
        assert result & PRESERVED == source & PRESERVED
        checks += 1
        assert result & DESTINATION_OWNED == destination & DESTINATION_OWNED
        checks += 1
        assert result & NON_SETTABLE_STORAGE == destination & NON_SETTABLE_STORAGE
        checks += 1
        assert result & UNMANAGED == destination & UNMANAGED
        checks += 1

        expected_input = (destination & DESTINATION_OWNED) | (source & PRESERVED)
        if expected_input == 0:
            assert basic_input == NORMAL
            checks += 1
        else:
            assert basic_input == expected_input
            checks += 1
            assert basic_input & NORMAL == 0
            checks += 1

    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--cases", type=int, default=100_000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error("--cases must be greater than zero")

    source_checks = check_source_contract(args.repo_root.resolve())
    fixed_checks = run_fixed_cases()
    randomized_checks = run_randomized(args.cases)
    print(
        "PASS Windows FileBasicInformation semantics/source contract: "
        f"{source_checks + fixed_checks + randomized_checks:,} checks across "
        f"{args.cases:,} randomized cases"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
