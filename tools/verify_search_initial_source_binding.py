#!/usr/bin/env python3
"""Verify initial Search source binding cannot erase startup results."""
from __future__ import annotations

import argparse
from pathlib import Path
import random


def source_event(
    generation: int,
    source_change_sequence: int,
    established: bool,
    last_token: str | None,
    new_token: str | None,
    busy: bool,
) -> tuple[int, int, bool, str | None, bool, tuple[int, str | None] | None]:
    if busy:
        generation += 1

    changed = new_token != last_token
    clear: tuple[int, str | None] | None = None
    if not changed:
        return generation, source_change_sequence, established, last_token, False, clear

    generation += 1
    last_token = new_token
    if not established and new_token is not None:
        established = True
        return generation, source_change_sequence, established, last_token, True, clear

    source_change_sequence += 1
    clear = (source_change_sequence, new_token)
    return generation, source_change_sequence, established, last_token, True, clear


def can_apply_clear(
    source_change_sequence: int,
    current_token: str | None,
    clear: tuple[int, str | None] | None,
) -> bool:
    return clear == (source_change_sequence, current_token)


def run_model(cases: int, seed: int) -> int:
    checks = 0

    for initial in ("native:1", "fallback:1"):
        generation, sequence, established, token, changed, clear = source_event(
            0, 0, False, None, initial, busy=False
        )
        assert generation == 1
        assert sequence == 0
        assert established
        assert token == initial
        assert changed
        assert clear is None
        checks += 6

    generation, sequence, established, token, changed, clear = source_event(
        7, 3, False, None, None, busy=True
    )
    assert generation == 8 and sequence == 3
    assert not established and token is None and not changed and clear is None
    checks += 4

    generation, sequence, established, token, changed, clear = source_event(
        0, 0, False, None, "native:1", busy=True
    )
    assert generation == 2 and sequence == 0 and established and changed and clear is None
    checks += 3

    generation, sequence, established, token, changed, clear = source_event(
        generation, sequence, established, token, "native:2", busy=False
    )
    assert changed and can_apply_clear(sequence, token, clear)
    checks += 2

    generation, sequence, established, token, changed, clear = source_event(
        generation, sequence, established, token, None, busy=False
    )
    assert changed and can_apply_clear(sequence, token, clear)
    checks += 2

    generation, sequence, established, token, changed, clear = source_event(
        generation, sequence, established, token, "fallback:2", busy=False
    )
    assert changed and can_apply_clear(sequence, token, clear)
    checks += 2

    rng = random.Random(seed)
    for index in range(cases):
        generation = rng.randint(0, 1_000_000)
        sequence = rng.randint(0, 100_000)
        first = f"native:first:{index}" if rng.randrange(2) else f"fallback:first:{index}"
        busy = bool(rng.randrange(2))
        initial_generation = generation
        generation, sequence_after, established, token, changed, clear = source_event(
            generation, sequence, False, None, first, busy
        )
        assert generation == initial_generation + 1 + (1 if busy else 0)
        assert sequence_after == sequence
        assert established and token == first and changed and clear is None
        checks += 4

        # Once established, every actual source transition owns a clear, including
        # loss to null and a later replacement after that unavailable interval.
        next_token = f"native:next:{index}"
        generation, sequence_after, established, token, changed, clear = source_event(
            generation, sequence_after, established, token, next_token, False
        )
        assert changed and can_apply_clear(sequence_after, token, clear)
        checks += 1

        generation, sequence_after, established, token, changed, lost_clear = source_event(
            generation, sequence_after, established, token, None, False
        )
        assert changed and can_apply_clear(sequence_after, token, lost_clear)
        checks += 1

        replacement = f"fallback:replacement:{index}"
        generation, sequence_after, established, token, changed, replacement_clear = source_event(
            generation, sequence_after, established, token, replacement, False
        )
        assert changed and can_apply_clear(sequence_after, token, replacement_clear)
        assert not can_apply_clear(sequence_after, token, clear)
        checks += 2

    return checks


def check_repository(root: Path) -> int:
    source = (root / "src/FileOp.App/MainWindow.SearchSourceIdentity.cs").read_text(encoding="utf-8")
    checks = 0
    required = (
        ("private bool _searchSourceIdentityEstablished;", "established-source marker"),
        ("_searchSourceIdentityEstablished = _lastSearchSourceIdentityKey is not null;", "initial marker binding"),
        ("if (!_searchSourceIdentityEstablished && sourceIdentityKey is not null)", "first usable-source exception"),
        ("_searchSourceIdentityEstablished = true;", "first-source establishment"),
        ("var sourceChangeSequence = Interlocked.Increment(", "subsequent source-change sequence"),
        ("QueueSearchPresentationInvalidation(", "subsequent stale-presentation clear"),
    )
    for needle, label in required:
        if needle not in source:
            raise AssertionError(f"missing {label}: {needle}")
        checks += 1

    token_exchange = source.index("var previousSourceIdentityKey = Interlocked.Exchange(")
    initial_guard = source.index("if (!_searchSourceIdentityEstablished && sourceIdentityKey is not null)")
    sequence = source.index("var sourceChangeSequence = Interlocked.Increment(", initial_guard)
    queue = source.index("QueueSearchPresentationInvalidation(", sequence)
    if not token_exchange < initial_guard < sequence < queue:
        raise AssertionError("initial source establishment must return before stale-presentation sequencing")
    checks += 1
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0x159)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {source_checks:,} source checks" if args.repo_root else ""
    print(
        "PASS: Search initial-source binding verifier: "
        f"{model_checks:,} model checks across {args.cases:,} randomized states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
