#!/usr/bin/env python3
"""Verify the dormant regular-file Recycle Bin transaction safety boundary."""
from __future__ import annotations

import argparse
import random
from pathlib import Path

PENDING = 0
MUTATION_STARTED = 1
RECYCLED = 2
FAILED = 3
RECOVERY_REQUIRED = 4

SUCCEEDED = "succeeded"
TERMINAL_FAILED = "failed"
CANCELLED = "cancelled"
TERMINAL_RECOVERY = "recovery"

FRONTIER = {MUTATION_STARTED, FAILED, RECOVERY_REQUIRED}


def valid_shape(states: list[int]) -> bool:
    frontier_seen = False
    pending_seen = False
    for state in states:
        if state == RECYCLED:
            if frontier_seen or pending_seen:
                return False
        elif state == PENDING:
            pending_seen = True
        elif state in FRONTIER:
            if frontier_seen or pending_seen:
                return False
            frontier_seen = True
        else:
            return False
    return True


def terminal_valid(states: list[int], terminal: str) -> bool:
    if not valid_shape(states):
        return False
    if terminal == SUCCEEDED:
        return bool(states) and all(state == RECYCLED for state in states)
    if terminal == TERMINAL_FAILED:
        return states.count(FAILED) == 1 and not any(
            state in (MUTATION_STARTED, RECOVERY_REQUIRED) for state in states
        )
    if terminal == CANCELLED:
        return PENDING in states and not any(state in FRONTIER for state in states)
    if terminal == TERMINAL_RECOVERY:
        return sum(state in (MUTATION_STARTED, RECOVERY_REQUIRED) for state in states) == 1 and FAILED not in states
    raise ValueError(terminal)


def can_start_mutation(states: list[int], ordinal: int) -> bool:
    if ordinal < 0 or ordinal >= len(states) or states[ordinal] != PENDING:
        return False
    return all(state == RECYCLED for state in states[:ordinal]) and all(
        state == PENDING for state in states[ordinal + 1 :]
    )


def run_model(cases: int, seed: int) -> int:
    rng = random.Random(seed)
    checks = 0
    for _ in range(cases):
        count = rng.randint(1, 8)
        recycled_prefix = rng.randint(0, count)
        states = [RECYCLED] * recycled_prefix + [PENDING] * (count - recycled_prefix)
        assert valid_shape(states)
        checks += 1

        if recycled_prefix < count:
            ordinal = recycled_prefix
            assert can_start_mutation(states, ordinal)
            checks += 1
            if ordinal + 1 < count:
                assert not can_start_mutation(states, ordinal + 1)
                checks += 1

            frontier_state = rng.choice([MUTATION_STARTED, FAILED, RECOVERY_REQUIRED])
            states[ordinal] = frontier_state
            assert valid_shape(states)
            checks += 1
            assert not any(can_start_mutation(states, later) for later in range(ordinal + 1, count))
            checks += 1

            if frontier_state == MUTATION_STARTED:
                assert terminal_valid(states, TERMINAL_RECOVERY)
                assert not terminal_valid(states, TERMINAL_FAILED)
                checks += 2
            elif frontier_state == FAILED:
                assert terminal_valid(states, TERMINAL_FAILED)
                assert not terminal_valid(states, TERMINAL_RECOVERY)
                checks += 2
            else:
                assert terminal_valid(states, TERMINAL_RECOVERY)
                assert not terminal_valid(states, TERMINAL_FAILED)
                checks += 2
        else:
            assert terminal_valid(states, SUCCEEDED)
            assert not terminal_valid(states, CANCELLED)
            checks += 2

        cancel_prefix = rng.randrange(count)
        cancelled = [RECYCLED] * cancel_prefix + [PENDING] * (count - cancel_prefix)
        assert terminal_valid(cancelled, CANCELLED)
        checks += 1

        if count > 1:
            bad = [PENDING] + [RECYCLED] + [PENDING] * (count - 2)
            assert not valid_shape(bad)
            checks += 1

        forged_ordinal = rng.randrange(count)
        forged = [PENDING] * count
        if forged_ordinal > 0:
            assert not can_start_mutation(forged, forged_ordinal)
            checks += 1

    return checks


def require(text: str, needle: str, description: str) -> int:
    if needle not in text:
        raise AssertionError(f"Missing {description}: {needle!r}")
    return 1


def forbid(text: str, needle: str, description: str) -> int:
    if needle in text:
        raise AssertionError(f"Forbidden {description}: {needle!r}")
    return 1


def check_repository(repo_root: Path) -> int:
    core_path = repo_root / "src/FileOp.Core/Operations/FileRecycleOperation.cs"
    store_path = repo_root / "src/FileOp.Core/Operations/SqliteFileRecycleActionHistoryStore.cs"
    tests_path = repo_root / "tests/FileOp.Windows.Tests/FileRecycleTransactionTests.cs"
    gate_path = repo_root / "tools/test-local.ps1"

    core = core_path.read_text(encoding="utf-8")
    store = store_path.read_text(encoding="utf-8")
    tests = tests_path.read_text(encoding="utf-8")
    gate = gate_path.read_text(encoding="utf-8")

    checks = 0
    checks += require(core, "public interface IFileRecycleMutationProvider", "separate recycle provider interface")
    checks += require(core, "string ProviderName { get; }", "provider identity bound at composition")
    checks += require(core, "var providerName = mutationProvider.ProviderName;", "single provider-name read at composition")
    checks += require(core, "_providerName = providerName;", "captured provider identity")
    checks += require(core, "Recycle provider returned no mutation result after the durable mutation barrier.", "null provider-result recovery boundary")
    checks += require(core, "RecycleProviderNameMismatch", "forged provider-name recovery path")
    checks += require(core, "RecycleProviderDispositionUnknown", "unknown provider-disposition recovery path")
    checks += require(core, "ValidateEntryOrdering(snapshot);", "history ordering validation")
    checks += require(core, "frontierSeen", "single-frontier history model")
    checks += require(core, "public bool RestoreAuthorized => false;", "non-authorizing restore evidence")
    checks += require(core, "CancellationToken.None", "post-barrier non-cancellable settlement")

    checks += require(store, "@require_frontier", "SQLite mutation-frontier guard")
    checks += require(store, "prior.ordinal < @ordinal", "SQLite recycled-prefix guard")
    checks += require(store, "prior.state <> @recycled", "SQLite prior-state guard")
    checks += require(store, "later.ordinal > @ordinal", "SQLite pending-suffix guard")
    checks += require(store, "later.state <> @pending", "SQLite later-state guard")
    checks += require(store, "PRAGMA synchronous=FULL", "durable SQLite synchronization")
    checks += require(store, "PRAGMA journal_mode=WAL", "durable SQLite journal policy")

    checks += require(tests, "SqliteMutationBarrierRejectsSkippedOrdinal", "skipped-ordinal regression test")
    checks += require(tests, "ExecutorObservesDurableBarrierBeforeProviderInvocation", "barrier-before-provider regression test")
    checks += require(tests, "ExecutorCapturesProviderNameExactlyOnceAtComposition", "single provider-name capture regression test")
    checks += require(tests, "ExecutorRejectsForgedProviderNameAsRecoveryRequired", "provider provenance regression test")
    checks += require(tests, "ProviderThrowAfterBarrierUsesBoundProviderNameAndRequiresRecovery", "provider throw recovery test")
    checks += require(tests, "NullProviderResultAfterBarrierRequiresRecovery", "null provider-result recovery test")
    checks += require(tests, "HistoryRejectsPendingBeforeLaterRecycledEvidence", "history ordering regression test")
    checks += require(tests, "Assert.Throws<ArgumentException>", "current MSTest assertion API")
    checks += forbid(tests, "Assert.ThrowsException", "obsolete MSTest assertion API")

    checks += forbid(core, "_providerName = mutationProvider.ProviderName;", "second provider-name read during composition")
    checks += forbid(core, "SHFileOperation", "legacy shell path mutation")
    checks += forbid(core, "IFileOperation", "native shell mutation in dormant Core transaction")
    checks += forbid(core, "FileSystem.DeleteFile", "path-only recycle shortcut")
    checks += forbid(core, "Directory.Delete", "directory mutation in regular-file transaction")
    checks += forbid(core, "File.Delete", "permanent-delete shortcut")

    checks += require(gate, "verify_file_recycle_transaction.py", "authoritative local-gate integration")
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0x5EC1C1E)
    args = parser.parse_args()
    if args.cases <= 0:
        raise SystemExit("--cases must be positive")

    repo_root = args.repo_root.resolve()
    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(repo_root)
    print(
        f"file recycle transaction verifier passed: {model_checks:,} model checks "
        f"across {args.cases:,} randomized cases; {source_checks} source/gate checks"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
