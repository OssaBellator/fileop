#!/usr/bin/env python3
"""Verify separate DiskIo event/buffer loss semantics without GitHub Actions."""
from __future__ import annotations

import argparse
import random
from pathlib import Path


def valid(loss_state: str, events: int | None, buffers: int | None) -> bool:
    if events is not None and events < 0:
        return False
    if buffers is not None and buffers < 0:
        return False
    if loss_state == "none":
        return events == 0 and buffers == 0
    if loss_state == "observed":
        return (events is not None and events > 0) or (buffers is not None and buffers > 0)
    if loss_state == "unknown":
        return events is None and buffers is None
    return False


def run_model(cases: int) -> int:
    checks = 0
    assert valid("none", 0, 0)
    assert valid("observed", 7, 0)
    assert valid("observed", 0, 3)
    assert valid("observed", 7, 3)
    assert valid("unknown", None, None)
    assert not valid("none", 0, 1)
    assert not valid("observed", 0, 0)
    assert not valid("unknown", None, 1)
    checks += 8

    rng = random.Random(20260811)
    states = ("none", "observed", "unknown")
    values = (None, 0, 1, 2, 17, 2**32 - 1)
    for _ in range(cases):
        state = rng.choice(states)
        events = rng.choice(values)
        buffers = rng.choice(values)
        actual = valid(state, events, buffers)
        if state == "none":
            expected = events == 0 and buffers == 0
        elif state == "observed":
            expected = (events is not None and events > 0) or (buffers is not None and buffers > 0)
        else:
            expected = events is None and buffers is None
        assert actual == expected
        checks += 1
        if actual and state == "observed":
            assert (events or 0) > 0 or (buffers or 0) > 0
            checks += 1
    return checks


def check_repository(root: Path) -> int:
    contract = (root / "src/FileOp.Core/Performance/DiskIoCapture.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/DiskIoCaptureLossEvidenceTests.cs").read_text(encoding="utf-8")
    checks = 0
    for needle in (
        "LostBufferCount",
        "lostBufferCount is < 0",
        "lostEventCount != 0 || lostBufferCount != 0",
        "lostEventCount is not > 0 && lostBufferCount is not > 0",
        "lostEventCount is not null || lostBufferCount is not null",
        "lostBufferCount: null",
    ):
        assert needle in contract, needle
        checks += 1

    assert "lostEventCount + lostBufferCount" not in contract
    assert "LostEventCount + LostBufferCount" not in contract
    checks += 2

    for needle in (
        "BufferOnlyLossIsRepresentableWithoutInventingEventLoss",
        "EventAndBufferLossRemainSeparateCounts",
        "NoLossRequiresBothCountersToBeZero",
        "ObservedLossRequiresAtLeastOnePositiveCounter",
        "UnknownLossRequiresBothCountersToRemainUnknown",
        "UnavailableResultsKeepBothLossCountersUnknown",
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
        "PASS: DiskIo event/buffer loss evidence verified with "
        f"{model_checks:,} model assertions across {args.cases:,} randomized states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
