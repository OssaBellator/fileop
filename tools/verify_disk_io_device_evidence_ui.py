#!/usr/bin/env python3
"""Verify bounded post-capture physical-disk evidence presentation."""
from __future__ import annotations

import argparse
import random
from pathlib import Path

INT_MAX = 2_147_483_647
MAX_QUERIED = 32


def collect(numbers: list[int]):
    if len(set(numbers)) != len(numbers):
        raise ValueError("duplicate disk number")
    queried = 0
    rows = []
    calls = []
    for number in numbers:
        if number > INT_MAX:
            rows.append((number, "out_of_range"))
        elif queried >= MAX_QUERIED:
            rows.append((number, "budget"))
        else:
            rows.append((number, "queried"))
            calls.append(number)
            queried += 1
    return rows, calls


def run_model(cases: int, seed: int) -> int:
    rng = random.Random(seed)
    checks = 0

    rows, calls = collect([2, 0, 7])
    assert rows == [(2, "queried"), (0, "queried"), (7, "queried")]
    assert calls == [2, 0, 7]
    checks += 2

    try:
        collect([1, 2, 1])
        raise AssertionError("duplicate disk was accepted")
    except ValueError:
        checks += 1

    for _ in range(cases):
        count = rng.randint(0, 55)
        values = set()
        while len(values) < count:
            if rng.random() < 0.16:
                values.add(rng.randint(INT_MAX + 1, 0xFFFFFFFF))
            else:
                values.add(rng.randint(0, 5000))
        numbers = list(values)
        rng.shuffle(numbers)

        rows, calls = collect(numbers)
        assert [number for number, _ in rows] == numbers
        assert len(calls) <= MAX_QUERIED
        assert calls == [number for number, status in rows if status == "queried"]
        assert all(number <= INT_MAX for number in calls)
        assert all(
            status == "out_of_range"
            for number, status in rows
            if number > INT_MAX
        )
        assert sum(status == "queried" for _, status in rows) == min(
            MAX_QUERIED,
            sum(number <= INT_MAX for number in numbers),
        )
        if len(numbers) > 1:
            assert len(set(numbers)) == len(numbers)
        checks += 7
    return checks


def method_body(source: str, signature_fragment: str) -> str:
    start = source.index(signature_fragment)
    brace = source.index("{", start)
    depth = 0
    for index in range(brace, len(source)):
        if source[index] == "{":
            depth += 1
        elif source[index] == "}":
            depth -= 1
            if depth == 0:
                return source[brace : index + 1]
    raise AssertionError(signature_fragment)


def require(text: str, needle: str, label: str) -> int:
    if needle not in text:
        raise AssertionError("missing %s: %s" % (label, needle))
    return 1


def forbid(text: str, needle: str, label: str) -> int:
    if needle in text:
        raise AssertionError("forbidden %s: %s" % (label, needle))
    return 1


def check_repository(root: Path) -> int:
    core = (root / "src/FileOp.Core/Performance/DiskIoDeviceEvidence.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/DiskIoDeviceEvidenceTests.cs").read_text(encoding="utf-8")
    engine = (root / "src/FileOp.App/DesktopSearchEngine.DiskIoAttribution.cs").read_text(encoding="utf-8")
    view = (root / "src/FileOp.App/DiskIoAttributionView.DeviceEvidence.cs").read_text(encoding="utf-8")
    performance = (root / "src/FileOp.App/PerformanceDiagnosticsView.DeviceEvidence.cs").read_text(encoding="utf-8")
    storage = (root / "src/FileOp.App/StorageOptimizationView.xaml.cs").read_text(encoding="utf-8")
    docs = (root / "docs/disk-io-device-evidence-ui.md").read_text(encoding="utf-8")
    parent = (root / "tools/verify_performance_disk_io_ui.py").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")

    checks = 0
    required = [
        (core, "public const int MaximumQueriedPhysicalDisks = 32", "device query budget"),
        (core, "DiskIoDeviceEvidenceQueryStatus.DiskNumberOutOfRange", "signed-range state"),
        (core, "DiskIoDeviceEvidenceQueryStatus.QueryBudgetExceeded", "budget state"),
        (core, "if (!seen.Add(number))", "duplicate precheck"),
        (core, "queriedCount >= MaximumQueriedPhysicalDisks", "bounded provider calls"),
        (core, "deviceContext.PhysicalDiskNumber != queryNumber", "device result identity"),
        (core, "nvmeHealth.PhysicalDiskNumber != queryNumber", "NVMe result identity"),
        (tests, "QueryPreservesDiskOrderAndCallsEachProviderOnce", "order/call regression"),
        (tests, "DuplicateDiskNumbersFailBeforeAnyDeviceQuery", "duplicate regression"),
        (tests, "QueryBudgetKeepsLaterObservedDisksExplicitWithoutCallingProviders", "budget regression"),
        (tests, "MismatchedProviderEvidenceFailsClosed", "identity regression"),
        (engine, "CaptureDiskIoAttributionAsync", "existing ETW bridge"),
        (view, "Reported device context for observed disks", "device panel heading"),
        (view, "not an SSD/HDD classification, health score, bottleneck verdict or recommendation", "no verdict disclaimer"),
        (view, "life-used estimate >254% (raw 255)", "raw PercentageUsed 255 rendering"),
        (view, "no currently defined warning bits set", "neutral zero-warning wording"),
        (view, "not a FileOp health verdict", "NVMe no-verdict wording"),
        (performance, "ApplyDiskIoDeviceEvidence", "performance forwarding"),
        (storage, "new WindowsPhysicalDiskDeviceContextProvider()", "physical provider composition"),
        (storage, "new WindowsNvmeHealthEvidenceProvider()", "NVMe provider composition"),
        (storage, "DiskIoDeviceEvidenceCollector.Query", "bounded enrichment call"),
        (storage, "PerformanceDiagnostics.ApplyDiskIoCapture(result);", "ETW result rendered first"),
        (storage, "SetDiskIoDeviceEvidenceUnavailable", "supplementary failure isolation"),
        (docs, "At most **32 queryable physical disks**", "documented query budget"),
        (docs, "does not extend the ETW session", "post-capture lifecycle"),
        (docs, "must not acquire DiskIo capture/device-query behavior", "storage independence"),
        (parent, "from verify_disk_io_device_evidence_ui import (", "parent imports child verifier"),
        (parent, "run_disk_io_device_evidence_model(args.cases", "parent runs child model"),
        (parent, "check_disk_io_device_evidence_repository(root)", "parent runs child source checks"),
        (gate, "verify_performance_disk_io_ui.py --repo-root $repoRoot --cases 50000", "existing offline parent gate"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
    ]
    for text, needle, label in required:
        checks += require(text, needle, label)

    capture_method = method_body(engine, "public ValueTask<DiskIoCaptureResult> CaptureDiskIoAttributionAsync")
    for needle in (
        "PhysicalDisk",
        "NvmeHealth",
        "DiskIoDeviceEvidence",
        "Task.Run",
    ):
        checks += forbid(capture_method, needle, "unchanged ETW bridge")

    apply_method = method_body(storage, "public void ApplyDiskIoCapture")
    if apply_method.index("PerformanceDiagnostics.ApplyDiskIoCapture(result);") > apply_method.index("DiskIoDeviceEvidenceCollector.Query"):
        raise AssertionError("device query runs before ETW result rendering")
    checks += 1
    checks += require(apply_method, "result.Status == DiskIoCaptureStatus.Completed", "completed-result prerequisite")
    checks += require(apply_method, "catch (Exception exception)", "supplementary failure isolation")
    checks += forbid(apply_method, "OrderBy", "device evidence ranking")
    checks += forbid(apply_method, "Task.Run", "detached device enrichment")

    set_unavailable = method_body(storage, "public void SetUnavailable")
    set_ready = method_body(storage, "public void SetReadyForRefresh")
    checks += forbid(set_unavailable, "DiskIo", "native storage unavailable affecting system-wide DiskIo")
    checks += forbid(set_ready, "DiskIo", "native storage readiness affecting system-wide DiskIo")

    combined = core + "\n" + view + "\n" + storage
    for needle in (
        "OrderBy(",
        "OrderByDescending(",
        "HealthScore",
        "ReliabilityScore",
        "RemainingHealth",
        "PeriodicTimer",
        "DispatcherQueueTimer",
        "FileSystemWatcher",
        "Task.Delay",
        "IOCTL_STORAGE_PROTOCOL_COMMAND",
        "FSCTL_FILE_LEVEL_TRIM",
        "Defrag",
        "OptimizeVolume",
    ):
        checks += forbid(combined, needle, "ranking/score/poller/storage action")
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0xD15CDE01)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = " and %s source/UI checks" % format(source_checks, ",") if args.repo_root else ""
    print(
        "PASS: DiskIo device evidence UI verified with %s model assertions across %s randomized captures%s."
        % (format(model_checks, ","), format(args.cases, ","), suffix)
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
