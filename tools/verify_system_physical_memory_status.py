#!/usr/bin/env python3
"""Verify read-only system physical-memory evidence."""
from __future__ import annotations

import argparse
import random
from pathlib import Path

from verify_system_physical_memory_status_ui import (
    check_repository as check_system_physical_memory_ui_repository,
    run_model as run_system_physical_memory_ui_model,
)


def valid_memory(total: int, available: int, load: int) -> bool:
    return total > 0 and 0 <= available <= total and 0 <= load <= 100


def run_model(cases: int, seed: int) -> int:
    rng = random.Random(seed)
    checks = 0

    assert valid_memory(100, 90, 73)
    assert not valid_memory(0, 0, 0)
    assert not valid_memory(100, 101, 0)
    assert not valid_memory(100, 0, 101)
    checks += 4

    for _ in range(cases):
        total = rng.randint(1, 2**50)
        available = rng.randint(0, total)
        load = rng.randint(0, 100)
        assert valid_memory(total, available, load)
        checks += 1

        used = total - available
        assert 0 <= used <= total
        checks += 1

        # Windows documents dwMemoryLoad as approximate. Deliberately choose a
        # value independently from the byte-derived ratio and require validity.
        unrelated_load = rng.randint(0, 100)
        assert valid_memory(total, available, unrelated_load)
        checks += 1

        assert not valid_memory(total, total + 1, load)
        assert not valid_memory(total, available, 101)
        assert not valid_memory(0, available if available == 0 else 0, load)
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
    core = (root / "src/FileOp.Core/Performance/SystemPhysicalMemoryStatus.cs").read_text(encoding="utf-8")
    provider = (root / "src/FileOp.Windows/Performance/WindowsSystemPhysicalMemoryStatusProvider.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/SystemPhysicalMemoryStatusTests.cs").read_text(encoding="utf-8")
    docs = (root / "docs/system-physical-memory-status.md").read_text(encoding="utf-8")
    parent = (root / "tools/verify_performance_diagnostics.py").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")

    checks = 0
    required = [
        (core, "TotalPhysicalBytes", "total physical bytes"),
        (core, "AvailablePhysicalBytes", "available physical bytes"),
        (core, "UsedPhysicalBytes => TotalPhysicalBytes - AvailablePhysicalBytes", "derived used bytes"),
        (core, "WindowsMemoryLoadPercent", "Windows approximate load"),
        (core, "windowsMemoryLoadPercent > 100", "load range validation"),
        (core, "availablePhysicalBytes > totalPhysicalBytes", "available range validation"),
        (core, "rather than requiring it to equal a ratio derived from the byte counters", "approximation boundary"),
        (provider, "GlobalMemoryStatusEx(ref native)", "single native memory query"),
        (provider, "Marshal.SizeOf<MemoryStatusEx>()", "native structure length"),
        (provider, "native.TotalPhysical", "native total physical field"),
        (provider, "native.AvailablePhysical", "native available physical field"),
        (provider, "native.MemoryLoad", "native memory load field"),
        (provider, "Stopwatch.GetElapsedTime(started)", "query elapsed evidence"),
        (provider, "Win32 error", "native error evidence"),
        (tests, "ContractPreservesApproximateWindowsLoadSeparatelyFromByteRatio", "approximation regression"),
        (tests, "ProviderFailsClosedOnMalformedNativeSnapshotOnWindows", "malformed native regression"),
        (tests, "NativeProviderReturnsConservativeEvidenceWhenAvailable", "native sanity regression"),
        (docs, "approximate percentage of physical memory in use", "documented Windows load semantics"),
        (docs, "deliberately does not expose those fields", "page-file/virtual boundary"),
        (docs, "not treated as wasted or recoverable RAM", "no cleanup heuristic"),
        (parent, "from verify_system_physical_memory_status import (", "parent imports memory verifier"),
        (parent, "run_system_physical_memory_model(args.cases", "parent runs memory model"),
        (parent, "check_system_physical_memory_repository(root)", "parent runs memory source checks"),
        (gate, "verify_performance_diagnostics.py --repo-root $repoRoot --cases 50000", "existing offline gate"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
    ]
    for text, needle, label in required:
        checks += require(text, needle, label)

    # The page-file and virtual-address members exist only inside the private
    # P/Invoke layout; no Core/public evidence contract may expose them.
    for needle in (
        "PageFile",
        "VirtualBytes",
        "TotalVirtual",
        "AvailableVirtual",
        "ExtendedVirtual",
    ):
        checks += forbid(core, needle, "page-file/virtual public evidence")

    combined = core + "\n" + provider + "\n" + docs
    for needle in (
        "EmptyWorkingSet",
        "SetProcessWorkingSetSize",
        "GC.Collect",
        "MemoryHealthScore",
        "MemoryPressureScore",
        "PeriodicTimer",
        "DispatcherQueueTimer",
        "FileSystemWatcher",
    ):
        checks += forbid(combined, needle, "memory action/score/poller")

    if provider.count("GlobalMemoryStatusEx(ref native)") != 1:
        raise AssertionError("physical-memory provider must issue exactly one native query")
    checks += 1
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0x4D454D)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    ui_model_checks = run_system_physical_memory_ui_model(args.cases, 0x4D454D55)
    source_checks = 0
    if args.repo_root:
        root = args.repo_root.resolve()
        source_checks = (
            check_repository(root) +
            check_system_physical_memory_ui_repository(root)
        )
    suffix = " and %s source/UI checks" % format(source_checks, ",") if args.repo_root else ""
    print(
        "PASS: system physical-memory status verified with %s provider-model assertions and %s UI-model assertions across %s randomized states%s."
        % (
            format(model_checks, ","),
            format(ui_model_checks, ","),
            format(args.cases, ","),
            suffix,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
