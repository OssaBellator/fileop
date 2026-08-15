#!/usr/bin/env python3
"""Portable model/source checks for recovery-sensitive shadow index publication."""
from __future__ import annotations

import argparse
import random
import sys
from enum import Enum, auto
from pathlib import Path


class State(Enum):
    PREPARED = auto()
    SWAP_STARTED = auto()
    PUBLISHED = auto()
    ABANDONED_BEFORE_SWAP = auto()
    RECOVERY_REQUIRED = auto()


def can_prepare(*, publishable: bool, nonempty_id: bool) -> bool:
    return publishable and nonempty_id


def can_start_swap(*, state: State, same_binding: bool, publishable_now: bool) -> bool:
    return state is State.PREPARED and same_binding and publishable_now


def can_publish(*, state: State, same_binding: bool) -> bool:
    return state is State.SWAP_STARTED and same_binding


def can_abandon_safely(*, state: State, has_summary: bool) -> bool:
    return state is State.PREPARED and has_summary


def can_require_recovery(*, state: State, has_summary: bool) -> bool:
    return state is State.SWAP_STARTED and has_summary


def swap_may_have_changed_live(state: State) -> bool:
    return state in {State.SWAP_STARTED, State.PUBLISHED, State.RECOVERY_REQUIRED}


def run_model(cases: int) -> int:
    rng = random.Random(0x1982026)
    checks = 0

    assert can_prepare(publishable=True, nonempty_id=True)
    assert not can_prepare(publishable=False, nonempty_id=True)
    assert can_start_swap(state=State.PREPARED, same_binding=True, publishable_now=True)
    assert not can_abandon_safely(state=State.SWAP_STARTED, has_summary=True)
    assert can_require_recovery(state=State.SWAP_STARTED, has_summary=True)
    assert swap_may_have_changed_live(State.RECOVERY_REQUIRED)
    checks += 6

    states = list(State)
    for _ in range(cases):
        state = rng.choice(states)
        same_binding = bool(rng.getrandbits(1))
        publishable_now = bool(rng.getrandbits(1))
        has_summary = bool(rng.getrandbits(1))
        publishable_initial = bool(rng.getrandbits(1))
        nonempty_id = bool(rng.getrandbits(1))

        assert can_prepare(
            publishable=publishable_initial,
            nonempty_id=nonempty_id,
        ) == (publishable_initial and nonempty_id)
        assert can_start_swap(
            state=state,
            same_binding=same_binding,
            publishable_now=publishable_now,
        ) == (state is State.PREPARED and same_binding and publishable_now)
        assert can_publish(
            state=state,
            same_binding=same_binding,
        ) == (state is State.SWAP_STARTED and same_binding)
        assert can_abandon_safely(
            state=state,
            has_summary=has_summary,
        ) == (state is State.PREPARED and has_summary)
        assert can_require_recovery(
            state=state,
            has_summary=has_summary,
        ) == (state is State.SWAP_STARTED and has_summary)
        assert swap_may_have_changed_live(state) == (
            state in {State.SWAP_STARTED, State.PUBLISHED, State.RECOVERY_REQUIRED}
        )
        checks += 6

    return checks


def read(root: Path, relative: str) -> str:
    path = root / relative
    if not path.is_file():
        raise FileNotFoundError(str(path))
    return path.read_text(encoding="utf-8")


def require(text: str, *needles: str) -> int:
    for needle in needles:
        assert needle in text, needle
    return len(needles)


def forbid(text: str, *needles: str) -> int:
    for needle in needles:
        assert needle not in text, needle
    return len(needles)


def check_repository(root: Path) -> int:
    publication = read(root, "src/FileOp.Core/Indexing/IndexRebuildPublicationPolicy.cs")
    transaction = read(root, "src/FileOp.Core/Indexing/IndexRebuildPublicationTransaction.cs")
    tests = read(root, "tests/FileOp.Windows.Tests/IndexRebuildPublicationTransactionTests.cs")

    checks = 0
    checks += require(
        publication,
        "State == IndexRebuildSnapshotState.ShadowCheckpointVerified",
        "LiveSnapshotReadable",
        "ShadowHasValidCheckpoint",
        "ExclusivePublicationLeaseHeld",
        "!string.Equals(LiveDatabasePath, ShadowDatabasePath, StringComparison.OrdinalIgnoreCase)",
    )
    checks += require(
        transaction,
        "IndexRebuildPublicationAttemptState",
        "Prepared",
        "SwapStarted",
        "Published",
        "AbandonedBeforeSwap",
        "RecoveryRequired",
        "Guid AttemptId",
        "LiveSnapshotWasReadableAtPreparation",
        "ShadowCheckpointWasValidAtPreparation",
        "ExclusivePublicationLeaseWasHeldAtPreparation",
        "SwapMayHaveChangedLiveSnapshot",
        "GrantsAutomaticRetryAuthority => false",
        "GrantsRollbackAuthority => false",
        "GrantsCleanupAuthority => false",
        "if (!publicationState.CanPublish)",
        "RequireSamePublicationBinding(attempt, currentPublicationState)",
        "if (!currentPublicationState.CanPublish)",
        "RequireState(attempt, IndexRebuildPublicationAttemptState.Prepared)",
        "RequireState(attempt, IndexRebuildPublicationAttemptState.SwapStarted)",
        "State = IndexRebuildPublicationAttemptState.RecoveryRequired",
        "Index publication failure settlement requires a non-empty summary",
    )
    checks += forbid(
        transaction,
        "File.Move(",
        "File.Replace(",
        "File.Delete(",
        "Directory.Move(",
        "Directory.Delete(",
        "SqliteConnection",
        "Microsoft.Data.Sqlite",
    )
    checks += require(
        tests,
        "VerifiedShadowWithExclusiveLeaseCanPrepareAndPublish",
        "NonPublishableStateCannotPrepareAttempt",
        "EmptyAttemptIdIsRejected",
        "LosingExclusiveLeaseBeforeSwapStartFailsClosed",
        "PublicationPathRebindingIsRejectedBeforeAndAfterSwapStart",
        "PreparedAttemptCanBeSafelyAbandonedBeforeSwap",
        "FailureAfterSwapStartIsRecoverySensitiveAndCannotBeSafelyAbandoned",
        "PublishedOrRecoveryAttemptsCannotTransitionAgain",
        "FailureSettlementRequiresSummary",
        "Assert.IsFalse(published.GrantsAutomaticRetryAuthority)",
        "Assert.IsTrue(recovery.RequiresRecovery)",
    )
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--cases", type=int, default=50000)
    parser.add_argument("--model-only", action="store_true")
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases)
    print(
        f"PASS index rebuild publication transaction model: {model_checks} checks across {args.cases} randomized cases"
    )
    if not args.model_only:
        source_checks = check_repository(args.repo_root.resolve())
        print(f"PASS index rebuild publication transaction source contract: {source_checks} checks")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
