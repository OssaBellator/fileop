#!/usr/bin/env python3
"""Verify standardized read-only NVMe SMART/Health evidence without hosted Actions."""
from __future__ import annotations

import argparse
import random
import struct
from pathlib import Path

QUERY_HEADER_BYTES = 8
PROTOCOL_BYTES = 40
DESCRIPTOR_BYTES = 48
HEALTH_BYTES = 512
BUFFER_BYTES = QUERY_HEADER_BYTES + PROTOCOL_BYTES + HEALTH_BYTES

STORAGE_DEVICE_PROTOCOL_SPECIFIC_PROPERTY = 50
PROPERTY_STANDARD_QUERY = 0
PROTOCOL_TYPE_NVME = 3
NVME_DATA_TYPE_LOG_PAGE = 2
NVME_LOG_PAGE_HEALTH_INFO = 2


def build_query() -> bytes:
    data = bytearray(BUFFER_BYTES)
    struct.pack_into("<II", data, 0, STORAGE_DEVICE_PROTOCOL_SPECIFIC_PROPERTY, PROPERTY_STANDARD_QUERY)
    struct.pack_into(
        "<IIIIIIIIII",
        data,
        QUERY_HEADER_BYTES,
        PROTOCOL_TYPE_NVME,
        NVME_DATA_TYPE_LOG_PAGE,
        NVME_LOG_PAGE_HEALTH_INFO,
        0,
        PROTOCOL_BYTES,
        HEALTH_BYTES,
        0,
        0,
        0,
        0,
    )
    return bytes(data)


def write_u128(data: bytearray, offset: int, value: int) -> None:
    if value < 0 or value >= 1 << 128:
        raise ValueError("counter outside uint128")
    data[offset : offset + 16] = value.to_bytes(16, "little")


def build_health(
    critical: int,
    temperature: int,
    spare: int,
    spare_threshold: int,
    percentage_used: int,
    power_cycles: int,
    power_on_hours: int,
    unsafe_shutdowns: int,
    media_errors: int,
    error_entries: int,
    warning_temp_minutes: int,
    critical_temp_minutes: int,
) -> bytes:
    data = bytearray(HEALTH_BYTES)
    data[0] = critical
    struct.pack_into("<H", data, 1, temperature)
    data[3] = spare
    data[4] = spare_threshold
    data[5] = percentage_used
    write_u128(data, 112, power_cycles)
    write_u128(data, 128, power_on_hours)
    write_u128(data, 144, unsafe_shutdowns)
    write_u128(data, 160, media_errors)
    write_u128(data, 176, error_entries)
    struct.pack_into("<I", data, 192, warning_temp_minutes)
    struct.pack_into("<I", data, 196, critical_temp_minutes)
    return bytes(data)


def parse_health(data: bytes):
    if len(data) < HEALTH_BYTES:
        raise ValueError("short health log")
    spare = data[3]
    spare_threshold = data[4]
    if spare > 100 or spare_threshold > 100:
        raise ValueError("normalized spare outside 0..100")
    return {
        "critical": data[0],
        "temperature": struct.unpack_from("<H", data, 1)[0],
        "spare": spare,
        "spare_threshold": spare_threshold,
        "percentage_used": data[5],
        "power_cycles": int.from_bytes(data[112:128], "little"),
        "power_on_hours": int.from_bytes(data[128:144], "little"),
        "unsafe_shutdowns": int.from_bytes(data[144:160], "little"),
        "media_errors": int.from_bytes(data[160:176], "little"),
        "error_entries": int.from_bytes(data[176:192], "little"),
        "warning_temp_minutes": struct.unpack_from("<I", data, 192)[0],
        "critical_temp_minutes": struct.unpack_from("<I", data, 196)[0],
    }


def build_response(health: bytes) -> bytes:
    if len(health) != HEALTH_BYTES:
        raise ValueError("health response must be exactly 512 bytes")
    data = bytearray(BUFFER_BYTES)
    struct.pack_into("<II", data, 0, DESCRIPTOR_BYTES, DESCRIPTOR_BYTES)
    struct.pack_into(
        "<IIIIIIIIII",
        data,
        QUERY_HEADER_BYTES,
        PROTOCOL_TYPE_NVME,
        NVME_DATA_TYPE_LOG_PAGE,
        NVME_LOG_PAGE_HEALTH_INFO,
        0,
        PROTOCOL_BYTES,
        HEALTH_BYTES,
        0,
        0,
        0,
        0,
    )
    data[DESCRIPTOR_BYTES : DESCRIPTOR_BYTES + HEALTH_BYTES] = health
    return bytes(data)


def parse_response(data: bytes, bytes_returned: int):
    if bytes_returned < DESCRIPTOR_BYTES or bytes_returned > len(data):
        raise ValueError("returned length outside response")
    returned = data[:bytes_returned]
    version, size = struct.unpack_from("<II", returned, 0)
    if version != DESCRIPTOR_BYTES or size != DESCRIPTOR_BYTES:
        raise ValueError("bad descriptor version/size")
    fields = struct.unpack_from("<IIIIIIIIII", returned, QUERY_HEADER_BYTES)
    protocol_type, data_type, request_value = fields[0], fields[1], fields[2]
    data_offset, data_length = fields[4], fields[5]
    if (
        protocol_type != PROTOCOL_TYPE_NVME
        or data_type != NVME_DATA_TYPE_LOG_PAGE
        or request_value != NVME_LOG_PAGE_HEALTH_INFO
    ):
        raise ValueError("wrong protocol metadata")
    if data_offset < PROTOCOL_BYTES or data_length < HEALTH_BYTES:
        raise ValueError("bad protocol data range")
    start = QUERY_HEADER_BYTES + data_offset
    end = start + HEALTH_BYTES
    if start < DESCRIPTOR_BYTES or end > bytes_returned:
        raise ValueError("health payload outside returned range")
    return parse_health(returned[start:end])


def run_model(cases: int, seed: int) -> int:
    rng = random.Random(seed)
    checks = 0

    query = build_query()
    assert len(query) == BUFFER_BYTES
    query_fields = struct.unpack_from("<IIIIIIIIIIII", query, 0)
    assert query_fields[0] == STORAGE_DEVICE_PROTOCOL_SPECIFIC_PROPERTY
    assert query_fields[1] == PROPERTY_STANDARD_QUERY
    assert query_fields[2] == PROTOCOL_TYPE_NVME
    assert query_fields[3] == NVME_DATA_TYPE_LOG_PAGE
    assert query_fields[4] == NVME_LOG_PAGE_HEALTH_INFO
    assert query_fields[5] == 0
    assert query_fields[6] == PROTOCOL_BYTES
    assert query_fields[7] == HEALTH_BYTES
    assert query_fields[8:] == (0, 0, 0, 0)
    checks += 9

    for _ in range(cases):
        critical = rng.randrange(256)
        temperature = rng.randrange(1 << 16)
        spare = rng.randint(0, 100)
        spare_threshold = rng.randint(0, 100)
        percentage_used = rng.randrange(256)
        counters = [rng.getrandbits(128) for _ in range(5)]
        warning_minutes = rng.getrandbits(32)
        critical_minutes = rng.getrandbits(32)
        health = build_health(
            critical,
            temperature,
            spare,
            spare_threshold,
            percentage_used,
            counters[0],
            counters[1],
            counters[2],
            counters[3],
            counters[4],
            warning_minutes,
            critical_minutes,
        )
        parsed = parse_health(health)
        assert parsed["critical"] == critical
        assert parsed["temperature"] == temperature
        assert parsed["spare"] == spare
        assert parsed["spare_threshold"] == spare_threshold
        assert parsed["percentage_used"] == percentage_used
        assert parsed["power_cycles"] == counters[0]
        assert parsed["power_on_hours"] == counters[1]
        assert parsed["unsafe_shutdowns"] == counters[2]
        assert parsed["media_errors"] == counters[3]
        assert parsed["error_entries"] == counters[4]
        assert parsed["warning_temp_minutes"] == warning_minutes
        assert parsed["critical_temp_minutes"] == critical_minutes
        checks += 12

        response = build_response(health)
        assert parse_response(response, len(response)) == parsed
        checks += 1

        malformed_spare = bytearray(health)
        malformed_spare[3 if rng.getrandbits(1) else 4] = rng.randint(101, 255)
        try:
            parse_health(bytes(malformed_spare))
            raise AssertionError("out-of-range normalized spare was accepted")
        except ValueError:
            checks += 1

        malformed_response = bytearray(response)
        field = rng.choice((0, 4, 8, 12, 16, 24))
        current = struct.unpack_from("<I", malformed_response, field)[0]
        struct.pack_into("<I", malformed_response, field, current + 1)
        try:
            parse_response(bytes(malformed_response), len(malformed_response))
            raise AssertionError("malformed protocol descriptor/metadata was accepted")
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
    core = (root / "src/FileOp.Core/Performance/NvmeHealthEvidence.cs").read_text(encoding="utf-8")
    provider = (root / "src/FileOp.Windows/Performance/WindowsNvmeHealthEvidenceProvider.cs").read_text(encoding="utf-8")
    physical_provider = (root / "src/FileOp.Windows/Performance/WindowsPhysicalDiskDeviceContextProvider.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/NvmeHealthEvidenceTests.cs").read_text(encoding="utf-8")
    docs = (root / "docs/nvme-health-evidence.md").read_text(encoding="utf-8")
    parent = (root / "tools/verify_disk_io_bottleneck_evidence.py").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")

    checks = 0
    required = [
        (core, "public sealed record NvmeCriticalWarningEvidence", "critical warning contract"),
        (core, "ReservedOrFutureBits", "reserved warning preservation"),
        (core, "public byte PercentageUsedEstimate", "raw percentage-used evidence"),
        (core, "public UInt128 PowerCycles", "128-bit power-cycle evidence"),
        (core, "public UInt128 MediaErrors", "128-bit media-error evidence"),
        (core, "public UInt128 ErrorInfoLogEntryCount", "128-bit error-log evidence"),
        (provider, "StorageDeviceProtocolSpecificProperty = 50", "device protocol property"),
        (provider, "ProtocolTypeNvme = 3", "NVMe protocol type"),
        (provider, "NvmeDataTypeLogPage = 2", "NVMe log-page data type"),
        (provider, "NvmeLogPageHealthInfo = 2", "SMART/Health log page"),
        (provider, "ProtocolSpecificDataBytes = 40", "protocol-specific structure size"),
        (provider, "ProtocolDataDescriptorBytes = 48", "protocol descriptor size"),
        (provider, "NvmeHealthLogBytes = 512", "standardized health size"),
        (provider, "_physicalDiskApi.Open(physicalDiskNumber)", "shared zero-access open boundary"),
        (physical_provider, "dwDesiredAccess: 0", "zero desired access authority"),
        (provider, "version != ProtocolDataDescriptorBytes", "exact returned descriptor version"),
        (provider, "size != ProtocolDataDescriptorBytes", "exact returned descriptor size"),
        (provider, "protocolType != ProtocolTypeNvme", "returned protocol validation"),
        (provider, "dataType != NvmeDataTypeLogPage", "returned data type validation"),
        (provider, "requestValue != NvmeLogPageHealthInfo", "returned log page validation"),
        (provider, "healthEndLong > bytesReturned", "overflow-safe returned payload bound"),
        (provider, "ReadUInt128LittleEndian", "full counter parsing"),
        (tests, "QueryBufferMatchesDocumentedProtocolSpecificHealthRequest", "query layout regression"),
        (tests, "HealthLogParsesStandardizedWarningsAndLifetimeCounters", "health parser regression"),
        (tests, "PercentageUsed255AndReservedWarningBitsRemainRawEvidence", "raw semantics regression"),
        (tests, "UInt128CountersPreserveHighBits", "128-bit regression"),
        (docs, "PercentageUsed` is **not a health percentage**", "no percentage inversion"),
        (docs, "does not by itself indicate device failure", "100-percent semantics"),
        (docs, "deliberately supports only the standardized NVMe query", "protocol scope"),
        (parent, "from verify_nvme_health_evidence import (", "parent imports NVMe verifier"),
        (parent, "run_nvme_health_model(args.cases", "parent runs NVMe model"),
        (parent, "check_nvme_health_repository(root)", "parent runs NVMe source checks"),
        (gate, "verify_disk_io_bottleneck_evidence.py --repo-root $repoRoot --cases 50000", "transitive offline gate entry"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
    ]
    for text, needle, label in required:
        checks += require(text, needle, label)

    code = core + "\n" + provider
    for needle in (
        "HealthScore",
        "ReliabilityScore",
        "RemainingHealth",
        "IOCTL_STORAGE_PROTOCOL_COMMAND",
        "IOCTL_STORAGE_SET",
        "StorageAdapterProtocolSpecificProperty",
        "NVMeDataTypeFeature",
        "NVMeDataTypeLogPageEx",
        "PeriodicTimer",
        "FileSystemWatcher",
        "Task.Delay",
        "Microsoft.Win32.Registry",
    ):
        checks += forbid(code, needle, "score/pass-through/mutation/extra-protocol/poller")

    checks += forbid(provider, "PercentageUsedEstimate = 100 -", "health remaining inversion")
    checks += forbid(provider, "100 - healthLog[5]", "health remaining inversion")
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0x4E564D45)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = " and %s source checks" % format(source_checks, ",") if args.repo_root else ""
    print(
        "PASS: standardized NVMe health evidence verified with %s model assertions across %s randomized cases%s."
        % (format(model_checks, ","), format(args.cases, ","), suffix)
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
