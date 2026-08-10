#!/usr/bin/env python3
"""Verify FileOp's documented Windows DiskIo ETW payload decoding without GitHub Actions."""
from __future__ import annotations

import argparse
import random
import struct
from pathlib import Path

DISK_IO_GUID = "3d6fa8d4-fe05-11d0-9dda-00c04fd7ba7c"
READ = 10
WRITE = 11
FLUSH = 14


def read_write_length(pointer_size: int) -> int:
    return 36 + 2 * pointer_size


def flush_length(pointer_size: int) -> int:
    return 20 + pointer_size


def encode_read_write(
    pointer_size: int,
    disk: int,
    flags: int,
    transfer: int,
    byte_offset: int,
    response_ticks: int,
    tid: int,
    trailing: int = 0,
) -> bytes:
    highres_offset = 24 + 2 * pointer_size
    tid_offset = highres_offset + 8
    data = bytearray(tid_offset + 4 + trailing)
    struct.pack_into("<III", data, 0, disk, flags, transfer)
    struct.pack_into("<I", data, 12, 0xDEADBEEF)
    struct.pack_into("<q", data, 16, byte_offset)
    data[24:24 + pointer_size] = bytes([0x11]) * pointer_size
    data[24 + pointer_size:24 + 2 * pointer_size] = bytes([0x22]) * pointer_size
    struct.pack_into("<Q", data, highres_offset, response_ticks)
    struct.pack_into("<I", data, tid_offset, tid)
    return bytes(data)


def encode_flush(pointer_size: int, disk: int, flags: int, response_ticks: int, tid: int) -> bytes:
    tid_offset = 16 + pointer_size
    data = bytearray(tid_offset + 4)
    struct.pack_into("<IIQ", data, 0, disk, flags, response_ticks)
    data[16:16 + pointer_size] = bytes([0x33]) * pointer_size
    struct.pack_into("<I", data, tid_offset, tid)
    return bytes(data)


def decode(event_type: int, pointer_size: int, payload: bytes) -> tuple[int, str, int, int, int, int, int | None]:
    if pointer_size not in (4, 8):
        raise ValueError("pointer size")
    if event_type in (READ, WRITE):
        required = read_write_length(pointer_size)
        if len(payload) < required:
            raise ValueError("truncated read/write")
        highres_offset = 24 + 2 * pointer_size
        tid_offset = highres_offset + 8
        disk, flags, transfer = struct.unpack_from("<III", payload, 0)
        byte_offset = struct.unpack_from("<q", payload, 16)[0]
        response = struct.unpack_from("<Q", payload, highres_offset)[0]
        tid = struct.unpack_from("<I", payload, tid_offset)[0]
        return disk, "read" if event_type == READ else "write", transfer, response, tid, flags, byte_offset
    if event_type == FLUSH:
        required = flush_length(pointer_size)
        if len(payload) < required:
            raise ValueError("truncated flush")
        disk, flags, response = struct.unpack_from("<IIQ", payload, 0)
        tid = struct.unpack_from("<I", payload, 16 + pointer_size)[0]
        return disk, "flush", 0, response, tid, flags, None
    raise ValueError("not completion")


def run_model(cases: int) -> int:
    checks = 0
    assert read_write_length(4) == 44
    assert read_write_length(8) == 52
    assert flush_length(4) == 24
    assert flush_length(8) == 28
    checks += 4

    rng = random.Random(20260810)
    for _ in range(cases):
        pointer_size = rng.choice((4, 8))
        disk = rng.getrandbits(32)
        flags = rng.getrandbits(32)
        transfer = rng.getrandbits(32)
        byte_offset = rng.randint(-(1 << 63), (1 << 63) - 1)
        response = rng.getrandbits(64)
        tid = rng.getrandbits(32)
        event_type = rng.choice((READ, WRITE))
        trailing = rng.randint(0, 32)
        payload = encode_read_write(pointer_size, disk, flags, transfer, byte_offset, response, tid, trailing)
        decoded = decode(event_type, pointer_size, payload)
        assert decoded[0] == disk
        assert decoded[1] == ("read" if event_type == READ else "write")
        assert decoded[2] == transfer
        assert decoded[3] == response
        assert decoded[4] == tid
        assert decoded[5] == flags
        assert decoded[6] == byte_offset
        assert len(payload) >= read_write_length(pointer_size)
        checks += 8

        flush_payload = encode_flush(pointer_size, disk, flags, response, tid)
        flushed = decode(FLUSH, pointer_size, flush_payload)
        assert flushed == (disk, "flush", 0, response, tid, flags, None)
        assert len(flush_payload) == flush_length(pointer_size)
        checks += 2

        for truncated in (
            payload[: read_write_length(pointer_size) - 1],
            flush_payload[: flush_length(pointer_size) - 1],
        ):
            try:
                decode(event_type if len(truncated) > flush_length(pointer_size) else FLUSH, pointer_size, truncated)
                raise AssertionError("truncated payload accepted")
            except ValueError:
                checks += 1

        try:
            decode(event_type, rng.choice((0, 1, 2, 16)), payload)
            raise AssertionError("invalid pointer size accepted")
        except ValueError:
            checks += 1
    return checks


def check_repository(root: Path) -> int:
    paths = {
        "decoder": root / "src/FileOp.Windows/Performance/WindowsDiskIoEventDecoder.cs",
        "tests": root / "tests/FileOp.Windows.Tests/WindowsDiskIoEventDecoderTests.cs",
        "doc": root / "docs/disk-io-etw-decoder.md",
        "gate": root / "tools/test-local.ps1",
    }
    text: dict[str, str] = {}
    for name, path in paths.items():
        if not path.is_file():
            raise FileNotFoundError(path)
        text[name] = path.read_text(encoding="utf-8")

    checks = 0
    for needle in (
        'new("3d6fa8d4-fe05-11d0-9dda-00c04fd7ba7c")',
        "ReadEventType = 10",
        "WriteEventType = 11",
        "FlushEventType = 14",
        "pointerSize is not (4 or 8)",
        "24 + (2 * pointerSize)",
        "16 + pointerSize",
        "DiskIoOperationKind.Flush,\n            0,",
        "ReadUInt64LittleEndian",
        "ReadUInt32LittleEndian",
        "ReadInt64LittleEndian",
        "userData.Length < requiredLength",
    ):
        assert needle in text["decoder"], needle
        checks += 1

    for needle in (
        "ReadCompletionDecodesDocumentedFieldsForPointerWidth",
        "WriteCompletionUsesSameTypeGroup1Layout",
        "FlushCompletionDecodesWithoutInventingTransferBytes",
        "NonDiskProviderAndNonCompletionTypesAreIgnored",
        "RecognizedCompletionRequiresUnambiguousPointerWidth",
        "TruncatedReadWritePayloadFailsClosed",
        "TruncatedFlushPayloadFailsClosed",
        "AdditionalTrailingPayloadBytesDoNotShiftDocumentedFields",
        "[DataRow(4)]",
        "[DataRow(8)]",
    ):
        assert needle in text["tests"], needle
        checks += 1

    assert "DataTestMethod" not in text["tests"]
    checks += 1

    for needle in (
        "HighResResponseTime` is a performance-counter tick count",
        "not milliseconds",
        "44 bytes",
        "52 bytes",
        "24 bytes",
        "28 bytes",
        "does not guess based on the FileOp process architecture",
        "does not resolve it to a process",
    ):
        assert needle in text["doc"], needle
        checks += 1

    for forbidden in (
        "DllImport",
        "StartTrace(",
        "OpenTrace(",
        "ProcessTrace(",
        "ControlTrace(",
        "CloseTrace(",
        "DispatcherQueueTimer",
        "PeriodicTimer",
        "Process.GetProcessById",
    ):
        assert forbidden not in text["decoder"], forbidden
        checks += 1

    assert "verify_disk_io_etw_decoder.py" in text["gate"]
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
    repository_checks = 0
    if args.repo_root is not None:
        repository_checks = check_repository(args.repo_root.resolve())
    suffix = f" and {repository_checks:,} source/test/doc checks" if args.repo_root else ""
    print(
        "PASS: Windows DiskIo ETW completion decoding verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized cases{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
