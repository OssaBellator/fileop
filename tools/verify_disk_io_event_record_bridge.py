#!/usr/bin/env python3
"""Verify the immutable EVENT_RECORD to DiskIo decoder bridge without GitHub Actions."""
from __future__ import annotations

import argparse
import random
from pathlib import Path

DISK_PROVIDER = "3d6fa8d4-fe05-11d0-9dda-00c04fd7ba7c"
READ = 10
WRITE = 11
FLUSH = 14
FLAG32 = 0x0020
FLAG64 = 0x0040
CLASSIC = 0x0100
COMPLETIONS = {READ, WRITE, FLUSH}


def classify(provider: str, opcode: int, flags: int) -> tuple[str, int | None]:
    if provider != DISK_PROVIDER or opcode not in COMPLETIONS:
        return "ignore", None
    if flags & CLASSIC == 0:
        return "missing-classic", None
    has32 = bool(flags & FLAG32)
    has64 = bool(flags & FLAG64)
    if has32 == has64:
        return "bad-pointer-flags", None
    return "decode", 4 if has32 else 8


def required_payload(opcode: int, pointer_size: int) -> int:
    if opcode in (READ, WRITE):
        return 36 + 2 * pointer_size
    if opcode == FLUSH:
        return 20 + pointer_size
    raise ValueError(opcode)


def convert_ticks(response_ticks: int, frequency: int) -> int:
    if frequency <= 0:
        raise ValueError("frequency")
    # Model TimeSpan ticks (10,000,000/sec), away-from-zero is ordinary nearest
    # rounding for these non-negative values.
    numerator = response_ticks * 10_000_000
    quotient, remainder = divmod(numerator, frequency)
    if remainder * 2 >= frequency:
        quotient += 1
    return quotient


def run_model(cases: int) -> int:
    checks = 0
    assert classify(DISK_PROVIDER, READ, CLASSIC | FLAG32) == ("decode", 4)
    assert classify(DISK_PROVIDER, WRITE, CLASSIC | FLAG64) == ("decode", 8)
    assert classify(DISK_PROVIDER, FLUSH, CLASSIC | FLAG32) == ("decode", 4)
    assert classify("other", READ, 0) == ("ignore", None)
    assert classify(DISK_PROVIDER, 99, 0) == ("ignore", None)
    assert classify(DISK_PROVIDER, READ, FLAG32) == ("missing-classic", None)
    assert classify(DISK_PROVIDER, READ, CLASSIC) == ("bad-pointer-flags", None)
    assert classify(DISK_PROVIDER, READ, CLASSIC | FLAG32 | FLAG64) == ("bad-pointer-flags", None)
    assert required_payload(READ, 4) == 44
    assert required_payload(READ, 8) == 52
    assert required_payload(FLUSH, 4) == 24
    assert required_payload(FLUSH, 8) == 28
    assert convert_ticks(25_000, 10_000_000) == 25_000
    assert convert_ticks(5_000, 1_000_000) == 50_000
    checks += 14

    rng = random.Random(20260810)
    opcodes = (READ, WRITE, FLUSH)
    for _ in range(cases):
        opcode = rng.choice(opcodes)
        pointer_size = rng.choice((4, 8))
        flags = CLASSIC | (FLAG32 if pointer_size == 4 else FLAG64)
        result, resolved_pointer = classify(DISK_PROVIDER, opcode, flags)
        assert result == "decode"
        assert resolved_pointer == pointer_size
        checks += 2

        required = required_payload(opcode, pointer_size)
        assert required in (44, 52, 24, 28)
        assert required_payload(opcode, pointer_size) == required
        checks += 2

        # Extra bytes are allowed by #71; one-byte truncation is not.
        actual = required + rng.randrange(0, 128)
        assert actual >= required
        assert required - 1 < required
        checks += 2

        frequency = rng.randrange(1, 100_000_001)
        response = rng.randrange(0, 10_000_000_000)
        converted = convert_ticks(response, frequency)
        assert converted >= 0
        if response == 0:
            assert converted == 0
        checks += 2

        # Id is deliberately irrelevant to MOF DiskIo identity.
        event_id = rng.randrange(0, 65536)
        assert classify(DISK_PROVIDER, opcode, flags)[0] == "decode"
        assert 0 <= event_id <= 65535
        checks += 2

        wrong_provider = f"other-{rng.getrandbits(64):016x}"
        assert classify(wrong_provider, opcode, rng.randrange(0, 0x10000)) == ("ignore", None)
        wrong_opcode = rng.randrange(0, 256)
        while wrong_opcode in COMPLETIONS:
            wrong_opcode = rng.randrange(0, 256)
        assert classify(DISK_PROVIDER, wrong_opcode, rng.randrange(0, 0x10000)) == ("ignore", None)
        checks += 2

    return checks


def check_repository(root: Path) -> int:
    paths = {
        "bridge": root / "src/FileOp.Windows/Performance/WindowsDiskIoEventRecordBridge.cs",
        "decoder": root / "src/FileOp.Windows/Performance/WindowsDiskIoEventDecoder.cs",
        "metadata": root / "src/FileOp.Windows/Performance/WindowsDiskIoTraceMetadata.cs",
        "snapshot": root / "src/FileOp.Windows/Performance/WindowsEtwEventRecordSnapshot.cs",
        "tests": root / "tests/FileOp.Windows.Tests/WindowsDiskIoEventRecordBridgeTests.cs",
        "doc": root / "docs/disk-io-event-record-bridge.md",
        "gate": root / "tools/test-local.ps1",
    }
    text = {name: path.read_text(encoding="utf-8") for name, path in paths.items()}
    checks = 0

    for needle in (
        "EventHeaderFlag32Bit = 0x0020",
        "EventHeaderFlag64Bit = 0x0040",
        "EventHeaderFlagClassic = 0x0100",
        "record.Descriptor.Opcode",
        "WindowsDiskIoEventDecoder.DiskIoProviderId",
        "WindowsDiskIoEventDecoder.ReadEventType",
        "WindowsDiskIoEventDecoder.WriteEventType",
        "WindowsDiskIoEventDecoder.FlushEventType",
        "EVENT_HEADER_FLAG_CLASSIC_HEADER",
        "WindowsDiskIoTraceMetadata.FromEventHeaderFlags",
        "WindowsDiskIoEventDecoder.TryDecodeCompletion",
        "record.UserData",
        "metadata.ConvertHighResolutionResponseTime",
        "EventTimestamp: record.Timestamp",
        "ProcessorIndex: record.ProcessorIndex",
        "LoggerId: record.LoggerId",
    ):
        assert needle in text["bridge"], needle
        checks += 1

    for forbidden in (
        "BinaryPrimitives",
        "Marshal.",
        "BitConverter",
        "Process.GetProcessById",
        "Registry.",
        "OpenTraceW",
        "ProcessTrace(",
        "CloseTrace(",
        "DiskIoEventObservation",
        "DateTimeOffset.FromFileTime",
    ):
        assert forbidden not in text["bridge"], forbidden
        checks += 1

    assert 'new("3d6fa8d4-fe05-11d0-9dda-00c04fd7ba7c")' in text["decoder"]
    assert "ReadEventType = 10" in text["decoder"]
    assert "WriteEventType = 11" in text["decoder"]
    assert "FlushEventType = 14" in text["decoder"]
    assert "FromEventHeaderFlags" in text["metadata"]
    assert "ReadOnlySpan<byte> UserData => _userData" in text["snapshot"]
    checks += 6

    for needle in (
        "Classic32BitReadUsesOpcodeAndExistingDecoder",
        "Classic64BitWriteUsesPayloadPointerWidth",
        "ClassicFlushKeepsZeroTransferBytes",
        "UnrelatedProviderOrOpcodeIsIgnoredBeforeMetadataValidation",
        "DiskIoIdentityUsesOpcodeNotEventId",
        "TargetCompletionRequiresClassicHeader",
        "TargetCompletionRequiresExactlyOnePointerWidthFlag",
        "TargetCompletionPropagatesTruncatedPayloadFailure",
        "TargetCompletionRequiresPositivePerfFrequency",
    ):
        assert needle in text["tests"], needle
        checks += 1

    for needle in (
        "MOF-based",
        "Opcode",
        "classic-header",
        "wrong provider",
        "wrong opcode",
        "#71",
        "#72",
        "does not parse payload bytes",
        "does not resolve processes",
    ):
        assert needle in text["doc"], needle
        checks += 1

    assert "verify_disk_io_event_record_bridge.py" in text["gate"]
    checks += 1
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
        "PASS: DiskIo EVENT_RECORD bridge verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized completion records{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
