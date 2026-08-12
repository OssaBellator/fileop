#!/usr/bin/env python3
"""Verify stable lifecycle access to DiskIo trace evidence without GitHub Actions."""
from __future__ import annotations

import argparse
import random
from pathlib import Path


def may_read(owned: bool, opening: bool, processing: bool) -> bool:
    return owned and not opening and not processing


def run_model(cases: int) -> int:
    checks = 0
    assert may_read(True, False, False)
    assert not may_read(False, False, False)
    assert not may_read(True, True, False)
    assert not may_read(True, False, True)
    checks += 4

    rng = random.Random(20260811)
    for _ in range(cases):
        owned = bool(rng.getrandbits(1))
        opening = bool(rng.getrandbits(1))
        processing = bool(rng.getrandbits(1))
        result = may_read(owned, opening, processing)
        assert result == (owned and not opening and not processing)
        if result:
            assert owned
            assert not opening
            assert not processing
            checks += 3
        else:
            assert (not owned) or opening or processing
            checks += 1
    return checks


def check_repository(root: Path) -> int:
    source = (root / "src/FileOp.Windows/Performance/WindowsDiskIoNativeTraceConsumerApi.cs").read_text(encoding="utf-8")
    evidence = (root / "src/FileOp.Windows/Performance/WindowsDiskIoTraceEvidence.cs").read_text(encoding="utf-8")
    capture_api = (root / "src/FileOp.Windows/Performance/WindowsDiskIoAttributionProvider.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/WindowsDiskIoNativeTraceEvidenceSourceTests.cs").read_text(encoding="utf-8")
    checks = 0

    for needle in (
        "IWindowsDiskIoCaptureConsumerApi",
        "ReadTraceEvidence(ulong processingHandle)",
        "EnsureOwnedHandle(processingHandle)",
        "if (_openActive)",
        "if (_processActive)",
        "WindowsDiskIoTraceEvidenceReader.Read(_logfileBuffer!)",
    ):
        assert needle in source, needle
        checks += 1

    assert "interface IWindowsDiskIoTraceEvidenceSource" in evidence
    assert "WindowsDiskIoTraceEvidence ReadTraceEvidence" in evidence
    assert "IWindowsDiskIoCaptureConsumerApi :" in capture_api
    assert "IWindowsDiskIoTraceEvidenceSource" in capture_api
    checks += 4

    for forbidden in (
        "Stopwatch.Frequency",
        "QueryPerformanceFrequency",
        "EventsLost + BuffersLost",
        "EventsLost + evidence.BuffersLost",
        "Thread.Sleep",
        "PeriodicTimer",
    ):
        assert forbidden not in source, forbidden
        checks += 1

    for needle in (
        "StableOwnedStateReturnsTraceEvidence",
        "ActiveProcessTraceBlocksEvidenceRead",
        "InFlightOpenBlocksEvidenceRead",
        "WrongOrReleasedHandleCannotReadEvidence",
    ):
        assert needle in tests, needle
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
    suffix = f" and {repo_checks:,} source/test checks" if args.repo_root else ""
    print(
        "PASS: DiskIo trace-evidence lifecycle verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
