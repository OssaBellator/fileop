#!/usr/bin/env python3
"""Verify read-only physical-disk device context without hosted Actions."""
from __future__ import annotations

import argparse
import random
import struct
from pathlib import Path
from typing import Optional

FIXED_BYTES = 36
BOOLEAN_MINIMUM_BYTES = 9
MAX_RESERVED_BUS_TYPE = 0x7F

BUS_LABELS = {
    0: "Unknown",
    1: "SCSI",
    2: "ATAPI",
    3: "ATA",
    4: "IEEE 1394",
    5: "SSA",
    6: "Fibre Channel",
    7: "USB",
    8: "RAID",
    9: "iSCSI",
    10: "SAS",
    11: "SATA",
    12: "SD",
    13: "MMC",
    14: "Virtual",
    15: "File-backed virtual",
    16: "Storage Spaces",
    17: "NVMe",
    18: "SCM",
    19: "UFS",
    20: "NVMe-oF",
    21: "BusTypeMax sentinel",
    0x7F: "BusTypeMaxReserved sentinel",
}


def bus_label(raw: int) -> str:
    if raw < 0 or raw > MAX_RESERVED_BUS_TYPE:
        raise ValueError("bus type outside reserved range")
    return BUS_LABELS.get(raw, "Unrecognized bus type %d" % raw)


def _ascii_bytes(value: Optional[str]) -> bytes:
    return b"" if value is None else value.encode("ascii") + b"\x00"


def build_descriptor(
    raw_bus: int,
    vendor: Optional[str],
    product: Optional[str],
    revision: Optional[str],
    serial: Optional[str],
    removable: bool,
    queueing: bool,
) -> bytes:
    strings = [vendor, product, revision, serial]
    encoded = [_ascii_bytes(value) for value in strings]
    size = FIXED_BYTES + sum(len(value) for value in encoded)
    data = bytearray(size)
    struct.pack_into("<II", data, 0, 37, size)
    data[10] = int(removable)
    data[11] = int(queueing)
    struct.pack_into("<I", data, 28, raw_bus & 0xFFFFFFFF)
    struct.pack_into("<I", data, 32, 0)
    cursor = FIXED_BYTES
    for field_offset, value in zip((12, 16, 20, 24), encoded):
        if not value:
            struct.pack_into("<I", data, field_offset, 0)
            continue
        struct.pack_into("<I", data, field_offset, cursor)
        data[cursor : cursor + len(value)] = value
        cursor += len(value)
    return bytes(data)


def _read_ascii(data: bytes, field_offset: int) -> Optional[str]:
    (offset,) = struct.unpack_from("<I", data, field_offset)
    if offset == 0:
        return None
    if offset < FIXED_BYTES or offset >= len(data):
        raise ValueError("string offset outside payload")
    end = data.find(b"\x00", offset)
    if end < 0:
        raise ValueError("unterminated string")
    raw = data[offset:end]
    if any(value < 0x20 or value > 0x7E for value in raw):
        raise ValueError("non-printable ascii")
    text = raw.decode("ascii").strip()
    return text or None


def parse_descriptor(data: bytes):
    if len(data) < FIXED_BYTES:
        raise ValueError("short descriptor")
    version, size = struct.unpack_from("<II", data, 0)
    if version < FIXED_BYTES:
        raise ValueError("short version")
    if size < FIXED_BYTES or size > len(data):
        raise ValueError("invalid size")
    data = data[:size]
    (raw_bus,) = struct.unpack_from("<I", data, 28)
    if raw_bus > MAX_RESERVED_BUS_TYPE:
        raise ValueError("bus type outside reserved range")
    return {
        "raw_bus": raw_bus,
        "label": bus_label(raw_bus),
        "strings": tuple(_read_ascii(data, offset) for offset in (12, 16, 20, 24)),
        "removable": data[10] != 0,
        "queueing": data[11] != 0,
    }


def parse_boolean_descriptor(data: bytes) -> bool:
    if len(data) < BOOLEAN_MINIMUM_BYTES:
        raise ValueError("short boolean descriptor")
    version, size = struct.unpack_from("<II", data, 0)
    if version < BOOLEAN_MINIMUM_BYTES:
        raise ValueError("short boolean version")
    if size < BOOLEAN_MINIMUM_BYTES or size > len(data):
        raise ValueError("invalid boolean size")
    return data[8] != 0


def run_model(cases: int, seed: int) -> int:
    rng = random.Random(seed)
    checks = 0

    invalid_bus = build_descriptor(0x80, None, None, None, None, False, False)
    try:
        parse_descriptor(invalid_bus)
        raise AssertionError("bus type above 0x7F was accepted")
    except ValueError:
        checks += 1

    true_descriptor = struct.pack("<IIB3x", 12, 12, 1)
    false_descriptor = struct.pack("<IIB3x", 12, 12, 0)
    assert parse_boolean_descriptor(true_descriptor) is True
    assert parse_boolean_descriptor(false_descriptor) is False
    checks += 2

    alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_"
    for _ in range(cases):
        raw_bus = rng.randint(0, MAX_RESERVED_BUS_TYPE)
        values = []
        for _field in range(4):
            if rng.random() < 0.35:
                values.append(None)
            else:
                length = rng.randint(1, 16)
                values.append("".join(rng.choice(alphabet) for _ in range(length)))
        removable = bool(rng.getrandbits(1))
        queueing = bool(rng.getrandbits(1))
        data = build_descriptor(
            raw_bus,
            values[0],
            values[1],
            values[2],
            values[3],
            removable,
            queueing,
        )
        actual = parse_descriptor(data)

        assert actual["raw_bus"] == raw_bus
        assert actual["label"] == BUS_LABELS.get(
            raw_bus, "Unrecognized bus type %d" % raw_bus
        )
        assert actual["strings"] == tuple(values)
        assert actual["removable"] == removable
        assert actual["queueing"] == queueing
        checks += 5

        malformed = bytearray(data)
        struct.pack_into("<I", malformed, 28, rng.randint(0x80, 0xFFFFFFFF))
        try:
            parse_descriptor(bytes(malformed))
            raise AssertionError("out-of-range raw bus type was accepted")
        except ValueError:
            checks += 1

    return checks


def require(text: str, needle: str, label: str) -> int:
    if needle not in text:
        raise AssertionError("missing %s: %s" % (label, needle))
    return 1


def forbid(text: str, needle: str, label: str) -> int:
    if needle in text:
        raise AssertionError("forbidden %s: %s" % (label, needle))
    return 1


def check_repository(root: Path) -> int:
    core = (root / "src/FileOp.Core/Performance/PhysicalDiskDeviceContext.cs").read_text(
        encoding="utf-8"
    )
    provider = (
        root
        / "src/FileOp.Windows/Performance/WindowsPhysicalDiskDeviceContextProvider.cs"
    ).read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/PhysicalDiskDeviceContextTests.cs").read_text(
        encoding="utf-8"
    )
    docs = (root / "docs/physical-disk-device-context.md").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    protocol = (
        root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs"
    ).read_text(encoding="utf-8")

    checks = 0
    required = [
        (core, "public enum PhysicalDiskCapabilityStatus", "capability evidence state"),
        (core, "Available,\n    Unsupported,\n    Unavailable", "three-state evidence"),
        (core, "public uint RawBusType", "raw unsigned bus evidence"),
        (core, "PhysicalDiskBooleanCapability SeekPenalty", "seek evidence"),
        (core, "PhysicalDiskBooleanCapability Trim", "trim evidence"),
        (provider, "desiredAccess: 0", "zero desired access"),
        (provider, "IoctlStorageQueryProperty = 0x002D1400", "storage property IOCTL"),
        (provider, "StorageDeviceProperty = 0", "device descriptor property"),
        (provider, "StorageDeviceSeekPenaltyProperty = 7", "seek penalty property"),
        (provider, "StorageDeviceTrimProperty = 8", "trim property"),
        (provider, "MaximumPropertyBytes = 64 * 1024", "bounded output buffer"),
        (provider, "MaximumReservedBusType = 0x7F", "reserved bus bound"),
        (provider, "rawBusType > MaximumReservedBusType", "out-of-range bus rejection"),
        (provider, '=> $"Unrecognized bus type {rawBusType}"', "future bus preservation"),
        (provider, "ReadOptionalAscii(descriptor, 12", "vendor offset parsing"),
        (provider, "ReadOptionalAscii(descriptor, 24", "serial offset parsing"),
        (provider, "IsUnsupportedPropertyError", "unsupported capability classification"),
        (tests, "BusValueAboveReservedMaximumFailsClosed", "bus hardening regression"),
        (tests, "DescriptorRejectsUnterminatedAndNonPrintableAscii", "ASCII regression"),
        (tests, "BooleanDescriptorsPreserveReportedValues", "boolean descriptor regression"),
        (docs, "does not infer SSD, HDD", "no media inference documentation"),
        (docs, "Unsupported` and `Unavailable` are never converted to `false`", "unknown state semantics"),
        (gate, "verify_physical_disk_device_context.py --repo-root $repoRoot --cases 50000", "offline gate entry"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
    ]
    for text, needle, label in required:
        checks += require(text, needle, label)

    code = core + "\n" + provider
    for needle in (
        "IsSsd",
        "IsSSD",
        "IsHdd",
        "IsHDD",
        "SolidState",
        "RotationalMedia",
        "GENERIC_WRITE",
        "FSCTL_FILE_LEVEL_TRIM",
        "FSCTL_SET",
        "Defrag",
        "OptimizeVolume",
        "ManagementObject",
        "Microsoft.Win32.Registry",
        "PeriodicTimer",
        "FileSystemWatcher",
    ):
        checks += forbid(code, needle, "media inference/write/control/poller")

    checks += forbid(provider, "checked((int)rawBusType)", "raw bus narrowing")
    checks += forbid(provider, "(int)rawBusType", "raw bus narrowing")
    if provider.count("StorageDeviceSeekPenaltyProperty") < 2:
        raise AssertionError("seek-penalty property is not queried")
    checks += 1
    if provider.count("StorageDeviceTrimProperty") < 2:
        raise AssertionError("TRIM property is not queried")
    checks += 1
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0xD15C)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = " and %s source checks" % format(source_checks, ",") if args.repo_root else ""
    print(
        "PASS: physical-disk device context verified with %s model assertions across %s randomized cases%s."
        % (format(model_checks, ","), format(args.cases, ","), suffix)
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
