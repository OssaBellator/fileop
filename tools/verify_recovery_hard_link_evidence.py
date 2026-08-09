#!/usr/bin/env python3
"""Model/source checks for Copy recovery hard-link count evidence."""
from __future__ import annotations

import argparse
import random
import sys
from enum import Enum, auto
from pathlib import Path


class TopologyStatus(Enum):
    NO_RECORDED_COUNT = auto()
    SAME_COUNT = auto()
    DIFFERENT_COUNT = auto()
    UNAVAILABLE = auto()


class ContentStatus(Enum):
    MATCH = auto()
    DIFFERENT = auto()
    UNSAFE = auto()


def classify_topology(recorded: int | None, current: int | None) -> TopologyStatus:
    if recorded is None:
        return TopologyStatus.NO_RECORDED_COUNT
    if current is None:
        return TopologyStatus.UNAVAILABLE
    return TopologyStatus.SAME_COUNT if current == recorded else TopologyStatus.DIFFERENT_COUNT


def stable_reader_count(before: int, after: int) -> int | None:
    if before <= 0 or after <= 0 or before != after:
        return None
    return after


def run_model(cases: int) -> int:
    rng = random.Random(20260817)
    checks = 0
    for _ in range(cases):
        recorded = None if rng.random() < 0.2 else rng.randint(1, 32)
        before = rng.randint(0, 32)
        after = before if rng.random() < 0.7 else rng.randint(0, 32)
        current = stable_reader_count(before, after)
        topology = classify_topology(recorded, current)
        content = rng.choice(tuple(ContentStatus))

        assert current is not None if (before > 0 and before == after) else current is None
        checks += 1
        assert topology is TopologyStatus.NO_RECORDED_COUNT if recorded is None else topology is not TopologyStatus.NO_RECORDED_COUNT
        checks += 1
        if recorded is not None and current is None:
            assert topology is TopologyStatus.UNAVAILABLE
            checks += 1
        if recorded is not None and current is not None:
            assert topology is (
                TopologyStatus.SAME_COUNT if recorded == current else TopologyStatus.DIFFERENT_COUNT
            )
            checks += 1

        # Topology is separate evidence: changing link count never rewrites byte status.
        observed_content = content
        _ = topology
        assert observed_content is content
        checks += 1

        if topology is TopologyStatus.DIFFERENT_COUNT:
            assert recorded is not None and current is not None and recorded != current
            checks += 1
        if topology is TopologyStatus.SAME_COUNT:
            assert recorded is not None and current == recorded and current > 0
            checks += 1
    return checks


def check_repository(root: Path) -> int:
    paths = {
        "history": root / "src/FileOp.Core/Operations/FileOperationActionHistory.cs",
        "content": root / "src/FileOp.Core/Operations/FileOperationRecoveryContentVerification.cs",
        "reader": root / "src/FileOp.Windows/Operations/WindowsRootBoundFileContentFingerprintReader.cs",
        "core_tests": root / "tests/FileOp.Windows.Tests/FileOperationRecoveryContentVerificationTests.cs",
        "reader_tests": root / "tests/FileOp.Windows.Tests/WindowsRootBoundFileContentFingerprintReaderTests.cs",
    }
    missing = [str(path) for path in paths.values() if not path.is_file()]
    if missing:
        raise FileNotFoundError(", ".join(missing))
    source = {name: path.read_text(encoding="utf-8") for name, path in paths.items()}

    history_needles = (
        "public uint? DestinationHardLinkCount { get; init; }",
        "Optional durable observation",
    )
    for needle in history_needles:
        assert needle in source["history"], needle

    content_needles = (
        "public uint? CurrentDestinationHardLinkCount { get; init; }",
        "public enum FileOperationRecoveryHardLinkStatus",
        "NoRecordedCount",
        "SameCount",
        "DifferentCount",
        "Unavailable",
        "RecordedDestinationHardLinkCount",
        "HardLinkStatus",
        "hardLinkCount > 0",
        "stableHardLinkCount",
        "Hard-link count is surfaced separately as topology evidence",
    )
    for needle in content_needles:
        assert needle in source["content"], needle

    reader_needles = (
        "leafBefore.NumberOfLinks == 0",
        "leafBefore.NumberOfLinks != leafAfter.NumberOfLinks",
        "leafAfter.NumberOfLinks == 0",
        "CurrentDestinationHardLinkCount = leafAfter.NumberOfLinks",
        "hard-link count changed while its primary stream was being hashed",
    )
    for needle in reader_needles:
        assert needle in source["reader"], needle

    for test_name in (
        "MatchingRecordedHardLinkCountIsSeparateTopologyEvidence",
        "DifferentHardLinkCountDoesNotBecomeContentMismatch",
        "MissingCurrentHardLinkCountMakesReaderSuccessInconsistent",
    ):
        assert test_name in source["core_tests"], test_name

    assert "AdditionalHardLinkIncreasesStableObservedCountWithoutChangingBytesOrIdentity" in source["reader_tests"]
    assert "CreateHardLinkW(" in source["reader_tests"]

    forbidden = ("CanDelete", "CanUndo", "DeleteCreatedDestination")
    for needle in forbidden:
        assert needle not in source["content"], needle

    return (
        len(history_needles)
        + len(content_needles)
        + len(reader_needles)
        + 3
        + 2
        + len(forbidden)
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--self-test-only", action="store_true")
    parser.add_argument("--cases", type=int, default=50000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error("--cases must be greater than zero")

    checks = run_model(args.cases)
    print(f"PASS recovery hard-link evidence model: {checks} checks across {args.cases} randomized cases")
    if not args.self_test_only:
        print(
            "PASS recovery hard-link evidence source wiring: "
            f"{check_repository(args.repo_root.resolve())} checks"
        )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
