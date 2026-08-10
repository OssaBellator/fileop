#!/usr/bin/env python3
"""Verify FileOp's DiskIo trace metadata conversion without GitHub Actions."""
from __future__ import annotations

import argparse
import random
from pathlib import Path

TIMESPAN_TICKS_PER_SECOND = 10_000_000
LONG_MAX = (1 << 63) - 1
ULONG_MAX = (1 << 64) - 1


def pointer_size(has_32: bool, has_64: bool) -> int:
    if has_32 == has_64:
        raise ValueError("ambiguous pointer flags")
    return 4 if has_32 else 8


def convert_response_ticks(response_ticks: int, frequency: int) -> int:
    if not 0 <= response_ticks <= ULONG_MAX:
        raise ValueError("response ticks")
    if frequency <= 0 or frequency > LONG_MAX:
        raise ValueError("frequency")
    if response_ticks == 0:
        return 0

    numerator = response_ticks * TIMESPAN_TICKS_PER_SECOND
    # Positive nearest-integer rounding with midpoint values away from zero.
    converted = (2 * numerator + frequency) // (2 * frequency)
    if converted > LONG_MAX:
        raise OverflowError("TimeSpan")
    return converted


def run_model(cases: int) -> int:
    checks = 0
    assert pointer_size(True, False) == 4
    assert pointer_size(False, True) == 8
    checks += 2

    for flags in ((False, False), (True, True)):
        try:
            pointer_size(*flags)
            raise AssertionError("ambiguous pointer flags accepted")
        except ValueError:
            checks += 1

    assert convert_response_ticks(0, 10_000_000) == 0
    assert convert_response_ticks(1, 10_000_000) == 1
    assert convert_response_ticks(12_345_678, 10_000_000) == 12_345_678
    assert convert_response_ticks(1, 3) == 3_333_333
    assert convert_response_ticks(2, 3) == 6_666_667
    assert convert_response_ticks(1, 20_000_000) == 1
    checks += 6

    try:
        convert_response_ticks(ULONG_MAX, 1)
        raise AssertionError("overflowing response accepted")
    except OverflowError:
        checks += 1

    for invalid_frequency in (0, -1, -(1 << 63)):
        try:
            convert_response_ticks(1, invalid_frequency)
            raise AssertionError("invalid frequency accepted")
        except ValueError:
            checks += 1

    rng = random.Random(20260810)
    for _ in range(cases):
        has_32 = rng.choice((True, False))
        resolved_pointer = pointer_size(has_32, not has_32)
        assert resolved_pointer == (4 if has_32 else 8)
        checks += 1

        response = rng.getrandbits(64)
        frequency = rng.randint(1, LONG_MAX)
        numerator = response * TIMESPAN_TICKS_PER_SECOND
        expected = (2 * numerator + frequency) // (2 * frequency)
        try:
            actual = convert_response_ticks(response, frequency)
        except OverflowError:
            assert expected > LONG_MAX
            checks += 1
        else:
            assert expected <= LONG_MAX
            assert actual == expected
            assert actual >= 0
            checks += 3

        # Scaling both response ticks and frequency by the same safe factor
        # preserves the converted duration exactly.
        factor = rng.randint(1, 16)
        small_response = rng.randint(0, ULONG_MAX // factor)
        small_frequency = rng.randint(1, LONG_MAX // factor)
        base = convert_response_ticks(small_response, small_frequency)
        scaled = convert_response_ticks(small_response * factor, small_frequency * factor)
        assert scaled == base
        checks += 1

    return checks


def check_repository(root: Path) -> int:
    paths = {
        "metadata": root / "src/FileOp.Windows/Performance/WindowsDiskIoTraceMetadata.cs",
        "decoder": root / "src/FileOp.Windows/Performance/WindowsDiskIoEventDecoder.cs",
        "tests": root / "tests/FileOp.Windows.Tests/WindowsDiskIoTraceMetadataTests.cs",
        "doc": root / "docs/disk-io-trace-metadata.md",
        "gate": root / "tools/test-local.ps1",
    }
    text: dict[str, str] = {}
    for name, path in paths.items():
        if not path.is_file():
            raise FileNotFoundError(path)
        text[name] = path.read_text(encoding="utf-8")

    checks = 0
    for needle in (
        "pointerSize is not (4 or 8)",
        "performanceCounterFrequency <= 0",
        "has32BitHeaderFlag == has64BitHeaderFlag",
        "has32BitHeaderFlag ? 4 : 8",
        "responseTicks * (decimal)TimeSpan.TicksPerSecond / PerformanceCounterFrequency",
        "MidpointRounding.AwayFromZero",
        "timeSpanTicks > TimeSpan.MaxValue.Ticks",
        "TimeSpan.FromTicks((long)timeSpanTicks)",
    ):
        assert needle in text["metadata"], needle
        checks += 1

    assert "TryDecodeCompletion" in text["decoder"]
    checks += 1

    for needle in (
        "HeaderFlagsResolveExactPointerWidth",
        "AmbiguousOrMissingPointerFlagsFailClosed",
        "InvalidPointerSizeOrFrequencyFailsClosed",
        "TenMegahertzFrequencyMapsDirectlyToTimeSpanTicks",
        "FractionalTimeSpanTicksUseDeterministicNearestRounding",
        "ResponseConversionFailsClosedOnTimeSpanOverflow",
        "MetadataPointerWidthFeedsDiskIoDecoderWithoutArchitectureGuess",
        "metadata.PointerSize",
        "metadata.ConvertHighResolutionResponseTime",
    ):
        assert needle in text["tests"], needle
        checks += 1

    for needle in (
        "requires **exactly one**",
        "does not use that fallback",
        "`TRACE_LOGFILE_HEADER.PerfFreq`",
        "does not use `Stopwatch.Frequency`",
        "deterministic nearest rounding",
        "separate from event timestamp conversion",
        "does not convert event timestamps",
    ):
        assert needle in text["doc"], needle
        checks += 1

    source = text["metadata"]
    for forbidden in (
        "DllImport",
        "StartTrace(",
        "OpenTrace(",
        "ProcessTrace(",
        "ControlTrace(",
        "CloseTrace(",
        "Stopwatch.Frequency",
        "ProcessorFrequency",
        "CpuSpeed",
        "DispatcherQueueTimer",
        "PeriodicTimer",
    ):
        assert forbidden not in source, forbidden
        checks += 1

    assert "verify_disk_io_trace_metadata.py" in text["gate"]
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
        "PASS: Windows DiskIo trace metadata verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized cases{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
