#!/usr/bin/env python3
"""Zero-Actions model/source checks for Copy recovery basic-metadata evidence."""
from __future__ import annotations

import argparse
import random
import sys
from enum import Enum, auto
from pathlib import Path


STABLE_MASK = 0x00002027


class Status(Enum):
    NO_RECORDED = auto()
    SAME = auto()
    DIFFERENT = auto()
    UNAVAILABLE = auto()


def compare(recorded: tuple[int, int, int, int] | None,
            current: tuple[int, int, int, int] | None) -> tuple[Status, tuple[bool, bool, bool, bool] | None]:
    if recorded is None:
        return Status.NO_RECORDED, None
    if current is None:
        return Status.UNAVAILABLE, None
    rc, ra, rw, rattrs = recorded
    cc, ca, cw, cattrs = current
    fields = (
        rc == cc,
        rw == cw,
        (rattrs & STABLE_MASK) == (cattrs & STABLE_MASK),
        ra == ca,
    )
    return (Status.SAME if all(fields[:3]) else Status.DIFFERENT), fields


def run_model(cases: int) -> int:
    rng = random.Random(20260819)
    checks = 0
    ignored_mask = (~STABLE_MASK) & 0xFFFFFFFF
    stable_bits = [1 << bit for bit in range(32) if STABLE_MASK & (1 << bit)]

    for _ in range(cases):
        recorded = (
            rng.getrandbits(63),
            rng.getrandbits(63),
            rng.getrandbits(63),
            rng.getrandbits(32),
        )
        current = list(recorded)

        # Last-access may change without changing stable status.
        if rng.random() < 0.5:
            current[1] ^= rng.randrange(1, 1 << 24)
        # Arbitrary ignored attribute bits may change without changing stable status.
        ignored_flip = rng.getrandbits(32) & ignored_mask
        current[3] ^= ignored_flip

        status, fields = compare(recorded, tuple(current))
        assert fields is not None
        assert status is Status.SAME
        assert fields[0] and fields[1] and fields[2]
        assert fields[3] == (recorded[1] == current[1])
        checks += 5

        # Each stable dimension independently invalidates the aggregate.
        changed_creation = (recorded[0] ^ 1, recorded[1], recorded[2], recorded[3])
        assert compare(recorded, changed_creation)[0] is Status.DIFFERENT
        checks += 1

        changed_write = (recorded[0], recorded[1], recorded[2] ^ 1, recorded[3])
        assert compare(recorded, changed_write)[0] is Status.DIFFERENT
        checks += 1

        bit = rng.choice(stable_bits)
        changed_attr = (recorded[0], recorded[1], recorded[2], recorded[3] ^ bit)
        assert compare(recorded, changed_attr)[0] is Status.DIFFERENT
        checks += 1

        assert compare(None, tuple(current))[0] is Status.NO_RECORDED
        assert compare(recorded, None)[0] is Status.UNAVAILABLE
        checks += 2

    return checks


def check_repository(root: Path) -> int:
    core = root / "src/FileOp.Core/Operations/FileOperationBasicMetadataEvidence.cs"
    tests = root / "tests/FileOp.Windows.Tests/FileOperationRecoveryBasicMetadataComparerTests.cs"
    if not core.is_file() or not tests.is_file():
        raise FileNotFoundError("basic-metadata evidence source/tests are missing")
    source = core.read_text(encoding="utf-8")
    test_source = tests.read_text(encoding="utf-8")

    needles = (
        "public const uint StableCopiedAttributesMask",
        "0x00000001u",
        "0x00000002u",
        "0x00000004u",
        "0x00000020u",
        "0x00002000u",
        "LastAccessTimeMatchesDiagnostic",
        "creationMatches && lastWriteMatches && attributesMatch",
        "FileOperationRecoveryBasicMetadataStatus.NoRecordedEvidence",
        "FileOperationRecoveryBasicMetadataStatus.Unavailable",
        "read/filesystem/provider may update last-access",
    )
    for needle in needles:
        assert needle.casefold() in source.casefold(), needle

    for forbidden in ("CanDelete", "CanUndo", "File.Delete(", "File.Move("):
        assert forbidden not in source, forbidden

    for test_name in (
        "EqualStableMetadataIsSameEvenWhenIgnoredBitsMatchOrDiffer",
        "LastAccessDifferenceIsDiagnosticOnly",
        "CreationDifferenceIsStableMetadataDifference",
        "LastWriteDifferenceIsStableMetadataDifference",
        "CopiedSafeAttributeDifferenceIsStableMetadataDifference",
        "NoRecordedEvidenceDoesNotUpgradeCurrentObservation",
        "MissingCurrentObservationIsUnavailable",
        "StableAttributeMaskMatchesCopyContract",
    ):
        assert test_name in test_source, test_name

    return len(needles) + 4 + 8


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--self-test-only", action="store_true")
    parser.add_argument("--cases", type=int, default=50000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error("--cases must be greater than zero")

    checks = run_model(args.cases)
    print(f"PASS recovery basic-metadata model: {checks} checks across {args.cases} randomized cases")
    if not args.self_test_only:
        print(f"PASS recovery basic-metadata source wiring: {check_repository(args.repo_root.resolve())} checks")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
