#!/usr/bin/env python3
"""Verify read-only physical-disk failure prediction evidence."""
from __future__ import annotations

import argparse
import random
import struct
from pathlib import Path

PREDICTION_BYTES = 4 + 512
IOCTL_STORAGE_BASE = 0x2D
PREDICT_FAILURE_FUNCTION = 0x0440
METHOD_BUFFERED = 0
FILE_ANY_ACCESS = 0
IOCTL_STORAGE_PREDICT_FAILURE = 0x002D1100


def ctl_code(device_type: int, function: int, method: int, access: int) -> int:
    return (
        (device_type << 16)
        | (access << 14)
        | (function << 2)
        | method
    )


def parse_prediction(data: bytes) -> int:
    if len(data) < PREDICTION_BYTES:
        raise ValueError("short STORAGE_PREDICT_FAILURE")
    return struct.unpack_from("<I", data, 0)[0]


def classify_query(succeeded: bool, win32_error: int) -> str:
    if succeeded:
        return "available"
    if win32_error == 1:
        return "unsupported"
    return "unavailable"


def build_payload(raw: int, vendor: bytes) -> bytes:
    if len(vendor) != 512:
        raise ValueError("vendor payload must contain exactly 512 bytes")
    return struct.pack("<I", raw & 0xFFFFFFFF) + vendor


def run_model(cases: int, seed: int) -> int:
    rng = random.Random(seed)
    checks = 0

    assert ctl_code(
        IOCTL_STORAGE_BASE,
        PREDICT_FAILURE_FUNCTION,
        METHOD_BUFFERED,
        FILE_ANY_ACCESS,
    ) == IOCTL_STORAGE_PREDICT_FAILURE
    assert parse_prediction(build_payload(0, bytes(512))) == 0
    assert classify_query(False, 1) == "unsupported"
    assert classify_query(False, 5) == "unavailable"
    assert classify_query(True, 0) == "available"
    try:
        parse_prediction(bytes(PREDICTION_BYTES - 1))
        raise AssertionError("short failure-prediction payload was accepted")
    except ValueError:
        pass
    checks += 6

    for _ in range(cases):
        raw = rng.getrandbits(32)
        vendor = rng.randbytes(512)
        payload = build_payload(raw, vendor)
        parsed = parse_prediction(payload)
        assert parsed == raw
        assert (parsed != 0) == (raw != 0)
        assert len(payload) == PREDICTION_BYTES
        checks += 3

        replacement_vendor = rng.randbytes(512)
        assert parse_prediction(build_payload(raw, replacement_vendor)) == raw
        checks += 1

        mutated = bytearray(payload)
        vendor_index = 4 + rng.randrange(512)
        mutated[vendor_index] ^= 0xFF
        assert parse_prediction(bytes(mutated)) == raw
        checks += 1

        assert classify_query(True, rng.randint(0, 1000)) == "available"
        assert classify_query(False, 1) == "unsupported"
        other_error = rng.randint(2, 1000)
        assert classify_query(False, other_error) == "unavailable"
        checks += 3

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
    core = (root / "src/FileOp.Core/Performance/PhysicalDiskFailurePrediction.cs").read_text(encoding="utf-8")
    provider = (root / "src/FileOp.Windows/Performance/WindowsPhysicalDiskFailurePredictionProvider.cs").read_text(encoding="utf-8")
    open_authority = (root / "src/FileOp.Windows/Performance/WindowsPhysicalDiskDeviceContextProvider.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/PhysicalDiskFailurePredictionTests.cs").read_text(encoding="utf-8")
    docs = (root / "docs/physical-disk-failure-prediction.md").read_text(encoding="utf-8")
    parent = (root / "tools/verify_disk_io_bottleneck_evidence.py").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")

    checks = 0
    required = [
        (core, "WindowsPredictFailureValue", "raw Windows prediction value"),
        (core, "FailurePredicted => WindowsPredictFailureValue != 0", "documented nonzero interpretation"),
        (core, "TimeSpan elapsed", "probe elapsed evidence"),
        (core, "Unsupported,", "unsupported state"),
        (provider, "IWindowsPhysicalDiskStorageApi _storageApi", "existing open authority dependency"),
        (provider, "new WindowsPhysicalDiskStorageApi()", "existing zero-access open implementation"),
        (open_authority, "dwDesiredAccess: 0", "zero desired access"),
        (provider, "IoctlStoragePredictFailure = 0x002D1100", "prediction IOCTL"),
        (provider, "StoragePredictFailureBytes = 4 + 512", "fixed native output size"),
        (provider, "IntPtr.Zero,\n                0,", "no input buffer"),
        (provider, "new byte[StoragePredictFailureBytes]", "bounded output buffer"),
        (provider, "query.Win32Error == ErrorInvalidFunction", "unsupported Win32 classification"),
        (provider, "Stopwatch.GetElapsedTime(started)", "elapsed measurement"),
        (provider, "WindowsPhysicalDiskFailurePredictionParser.Parse", "single parser authority"),
        (provider, "data.Length < RequiredBytes", "short-output rejection"),
        (provider, "BinaryPrimitives.ReadUInt32LittleEndian(data[..4])", "only standardized ULONG parsed"),
        (provider, "VendorSpecific[512] is deliberately ignored", "vendor payload documented as ignored"),
        (tests, "ParserIgnoresVendorSpecificPayload", "vendor independence regression"),
        (tests, "ProviderUsesExistingZeroAccessOpenBoundaryAndOnePredictionQueryOnWindows", "open/query count regression"),
        (tests, "ProviderMapsInvalidFunctionToUnsupportedOnWindows", "unsupported mapping regression"),
        (tests, "ProviderFailsClosedOnShortSuccessfulPayloadOnWindows", "short-payload regression"),
        (docs, "does **not currently report a predicted failure**", "neutral zero wording"),
        (docs, "deliberately discards those bytes", "vendor payload boundary"),
        (docs, "not proof that the medium has no defects", "no healthy inference"),
        (parent, "from verify_physical_disk_failure_prediction import (", "parent imports prediction verifier"),
        (parent, "run_physical_disk_failure_prediction_model(", "parent runs prediction model"),
        (parent, "check_physical_disk_failure_prediction_repository(root)", "parent runs prediction source checks"),
        (gate, "verify_disk_io_bottleneck_evidence.py --repo-root $repoRoot --cases 50000", "existing offline gate"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
    ]
    for text, needle, label in required:
        checks += require(text, needle, label)

    executable = core + "\n" + provider
    for needle in (
        "data[4",
        "data[4..",
        "data.Slice(4",
        "SMART_RCV_DRIVE_DATA",
        "SMART_SEND_DRIVE_COMMAND",
        "ATA_PASS_THROUGH",
        "IOCTL_ATA_PASS_THROUGH",
        "IOCTL_SCSI_PASS_THROUGH",
        "NVME_PASS_THROUGH",
        "HealthScore",
        "FailureScore",
        "RemainingLife",
        "PeriodicTimer",
        "DispatcherQueueTimer",
        "FileSystemWatcher",
        "RegistryKey",
    ):
        checks += forbid(executable, needle, "vendor parser/action/score/poller")

    if provider.count("_predictionApi.Query(handle)") != 1:
        raise AssertionError("provider must issue exactly one failure-prediction query")
    checks += 1
    checks += forbid(provider, "_storageApi.Query(", "storage-property query reuse")
    checks += forbid(provider, "CreateFileW", "second physical-disk open implementation")
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0xFA117E)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = " and %s source checks" % format(source_checks, ",") if args.repo_root else ""
    print(
        "PASS: physical-disk failure prediction verified with %s model assertions across %s randomized cases%s."
        % (format(model_checks, ","), format(args.cases, ","), suffix)
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
