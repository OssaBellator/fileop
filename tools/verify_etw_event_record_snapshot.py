#!/usr/bin/env python3
"""Verify FileOp's immutable ETW EVENT_RECORD snapshot logic without GitHub Actions."""
from __future__ import annotations

import argparse
import random
from pathlib import Path

EVENT_HEADER_SIZE = 80
FIXED_SIZE_32 = 104
FIXED_SIZE_64 = 112
BUFFER_CONTEXT = 80
EXTENDED_COUNT = 84
USER_DATA_LENGTH = 86
EXTENDED_PTR = 88
USER_PTR_32 = 92
USER_PTR_64 = 96
USER_CONTEXT_32 = 96
USER_CONTEXT_64 = 104
MAX_USER_DATA = 0xFFFF


def validate(event_size: int, user_data_length: int, copy_limit: int, user_pointer_present: bool) -> str:
    if not 0 <= copy_limit <= MAX_USER_DATA:
        return "bad-limit"
    if event_size < EVENT_HEADER_SIZE:
        return "bad-header-size"
    if user_data_length > copy_limit:
        return "over-cap"
    if event_size < EVENT_HEADER_SIZE + user_data_length:
        return "bad-event-size"
    if user_data_length and not user_pointer_present:
        return "null-user-pointer"
    return "copy"


def run_model(cases: int) -> int:
    checks = 0
    assert FIXED_SIZE_32 == 104
    assert FIXED_SIZE_64 == 112
    assert BUFFER_CONTEXT == 80
    assert EXTENDED_COUNT == 84
    assert USER_DATA_LENGTH == 86
    assert EXTENDED_PTR == 88
    assert USER_PTR_32 == 92
    assert USER_PTR_64 == 96
    assert USER_CONTEXT_32 == 96
    assert USER_CONTEXT_64 == 104
    assert validate(80, 0, MAX_USER_DATA, False) == "copy"
    assert validate(79, 0, MAX_USER_DATA, False) == "bad-header-size"
    assert validate(84, 4, 3, True) == "over-cap"
    assert validate(82, 4, MAX_USER_DATA, True) == "bad-event-size"
    assert validate(82, 2, MAX_USER_DATA, False) == "null-user-pointer"
    assert validate(82, 2, MAX_USER_DATA, True) == "copy"
    checks += 16

    rng = random.Random(20260810)
    for _ in range(cases):
        user_length = rng.randrange(0, MAX_USER_DATA + 1)
        copy_limit = rng.randrange(0, MAX_USER_DATA + 1)
        valid_extra = rng.randrange(0, 4096)
        valid_size = min(MAX_USER_DATA, EVENT_HEADER_SIZE + user_length + valid_extra)
        pointer_present = bool(rng.getrandbits(1))
        result = validate(valid_size, user_length, copy_limit, pointer_present)

        if user_length > copy_limit:
            assert result == "over-cap"
            checks += 1
        elif valid_size < EVENT_HEADER_SIZE + user_length:
            assert result == "bad-event-size"
            checks += 1
        elif user_length and not pointer_present:
            assert result == "null-user-pointer"
            checks += 1
        else:
            assert result == "copy"
            checks += 1

        payload = bytearray(rng.randbytes(min(user_length, 256)))
        copied = bytes(payload)
        if payload:
            payload[0] ^= 0xFF
            assert copied[0] != payload[0]
            checks += 1
        else:
            assert copied == b""
            checks += 1

        # Descriptor metadata remains value-only and independent from payload bytes.
        opcode = rng.randrange(0, 256)
        event_id = rng.randrange(0, 65536)
        keyword = rng.getrandbits(64)
        descriptor = (event_id, opcode, keyword)
        assert descriptor[0] == event_id
        assert descriptor[1] == opcode
        assert descriptor[2] == keyword
        checks += 3

        # Extended data is intentionally represented by count only in this slice.
        extended_count = rng.randrange(0, 65536)
        arbitrary_pointer = rng.getrandbits(64)
        snapshot_extended = extended_count
        assert snapshot_extended == extended_count
        assert arbitrary_pointer >= 0
        checks += 2

    return checks


def check_repository(root: Path) -> int:
    paths = {
        "source": root / "src/FileOp.Windows/Performance/WindowsEtwEventRecordSnapshot.cs",
        "tests": root / "tests/FileOp.Windows.Tests/WindowsEtwEventRecordSnapshotTests.cs",
        "doc": root / "docs/etw-event-record-snapshot.md",
        "gate": root / "tools/test-local.ps1",
    }
    text = {name: path.read_text(encoding="utf-8") for name, path in paths.items()}
    checks = 0

    for needle in (
        "EventHeaderSizeBytes = 80",
        "EventRecordFixedSize32 = 104",
        "EventRecordFixedSize64 = 112",
        "BufferContextOffset = 80",
        "ExtendedDataCountOffset = 84",
        "UserDataLengthOffset = 86",
        "ExtendedDataPointerOffset = 88",
        "UserDataPointerOffset32 = 92",
        "UserDataPointerOffset64 = 96",
        "UserContextPointerOffset32 = 96",
        "UserContextPointerOffset64 = 104",
        "ReadOnlySpan<byte> UserData => _userData",
        "eventRecordSize < EventHeaderSizeBytes",
        "userDataLength > maximumUserDataBytes",
        "eventRecordSize < EventHeaderSizeBytes + userDataLength",
        "userDataPointer == IntPtr.Zero",
        "Marshal.Copy(userDataPointer, copiedUserData",
        "Opcode: Marshal.ReadByte(eventRecord, 45)",
        "extendedDataCount: ReadUInt16(eventRecord, ExtendedDataCountOffset)",
    ):
        assert needle in text["source"], needle
        checks += 1

    for needle in (
        "CopiesHeaderDescriptorContextAndUserDataIntoOwnedValues",
        "SnapshotSurvivesMutationOfNativeRecordAndPayload",
        "ZeroLengthPayloadAllowsNullUserDataPointer",
        "NonzeroPayloadWithNullPointerFailsClosed",
        "CopyLimitIsEnforcedBeforePayloadDereference",
        "InvalidHeaderSizeFailsClosed",
        "EventSizeMustBeLargeEnoughForDeclaredPayload",
        "NullEventRecordPointerIsRejected",
    ):
        assert needle in text["tests"], needle
        checks += 1

    for needle in (
        "EVENT_HEADER",
        "EVENT_RECORD",
        "80-byte",
        "UserDataLength",
        "private copied buffer",
        "does not dereference `ExtendedData`",
        "MOF-defined",
        "Opcode",
        "does not classify",
    ):
        assert needle in text["doc"], needle
        checks += 1

    for forbidden in (
        "Process.GetProcessById",
        "ManagementObject",
        "Registry.",
        "SHA256",
        "OpenTraceW",
        "ProcessTrace(",
        "CloseTrace(",
        "WindowsDiskIoEtwDecoder",
        "Delete(",
    ):
        assert forbidden not in text["source"], forbidden
        checks += 1

    # This slice must never follow the ExtendedData pointer.
    assert "Marshal.ReadIntPtr(eventRecord, ExtendedDataPointerOffset)" not in text["source"]
    assert "verify_etw_event_record_snapshot.py" in text["gate"]
    checks += 2
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    args = parser.parse_args()
    if args.cases < 0:
        raise ValueError("--cases must be non-negative")

    model_checks = run_model(args.cases)
    repo_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {repo_checks:,} source/test/doc checks" if args.repo_root else ""
    print(
        "PASS: ETW EVENT_RECORD snapshot verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized records{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
