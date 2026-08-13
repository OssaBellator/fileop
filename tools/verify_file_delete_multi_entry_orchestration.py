#!/usr/bin/env python3
"""Verify multi-entry file-delete orchestration and operation completion."""
from __future__ import annotations

import argparse
import random
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class OrchestrationState:
    entries: tuple[str, ...]
    initially_terminal: bool
    cancel_before_ordinal: int
    mutation_failure_ordinal: int
    release_failure_ordinal: int
    completion_behavior: int  # 0 success, 1 throw-before, 2 throw-after-success, 3 return-recovery, 4 throw-after-recovery


@dataclass(frozen=True)
class OrchestrationResult:
    outcome: str
    entries: tuple[str, ...]
    events: tuple[str, ...]
    mutation_counts: tuple[int, ...]
    terminal_state: str | None
    cleanup_owner: bool


def execute(state: OrchestrationState) -> OrchestrationResult:
    entries = list(state.entries)
    events: list[str] = []
    mutation_counts = [0] * len(entries)

    if any(value in ("M", "R") for value in entries):
        return OrchestrationResult(
            "recovery-stop", tuple(entries), (), tuple(mutation_counts), None, False
        )

    if state.initially_terminal:
        if not all(value in ("C", "F") for value in entries):
            return OrchestrationResult(
                "invalid-terminal", tuple(entries), (), tuple(mutation_counts), None, False
            )
        terminal = "Succeeded" if all(value == "C" for value in entries) else "Failed"
        return OrchestrationResult(
            "terminal-observed", tuple(entries), (), tuple(mutation_counts), terminal, False
        )

    for ordinal, value in enumerate(entries):
        if state.cancel_before_ordinal == ordinal:
            events.append(f"cancel:{ordinal}")
            return OrchestrationResult(
                "cancelled", tuple(entries), tuple(events), tuple(mutation_counts), None, False
            )
        if any(current in ("M", "R") for current in entries):
            events.append(f"recovery-stop:{ordinal}")
            return OrchestrationResult(
                "recovery-stop", tuple(entries), tuple(events), tuple(mutation_counts), None, False
            )
        if value in ("C", "F"):
            events.append(f"observe-terminal:{ordinal}")
            continue
        if value != "P":
            return OrchestrationResult(
                "invalid-entry", tuple(entries), tuple(events), tuple(mutation_counts), None, False
            )

        events.extend((f"prepare:{ordinal}", f"barrier:{ordinal}", f"mutate:{ordinal}"))
        mutation_counts[ordinal] += 1
        if state.mutation_failure_ordinal == ordinal:
            entries[ordinal] = "R"
            events.append(f"recovery:{ordinal}")
            return OrchestrationResult(
                "mutation-recovery", tuple(entries), tuple(events), tuple(mutation_counts), None, False
            )
        if state.release_failure_ordinal == ordinal:
            entries[ordinal] = "R"
            events.extend((f"release-failed:{ordinal}", f"cleanup-owner:{ordinal}"))
            return OrchestrationResult(
                "release-recovery", tuple(entries), tuple(events), tuple(mutation_counts), None, True
            )

        entries[ordinal] = "C"
        events.append(f"commit:{ordinal}")

    if not all(value in ("C", "F") for value in entries):
        return OrchestrationResult(
            "not-completable", tuple(entries), tuple(events), tuple(mutation_counts), None, False
        )

    events.append("complete")
    terminal = "Succeeded" if all(value == "C" for value in entries) else "Failed"
    if state.completion_behavior == 1:
        events.append("inspect-nonterminal")
        return OrchestrationResult(
            "completion-unproven", tuple(entries), tuple(events), tuple(mutation_counts), None, False
        )
    if state.completion_behavior == 2:
        events.append("inspect-terminal")
        return OrchestrationResult(
            "completed-after-inspection",
            tuple(entries),
            tuple(events),
            tuple(mutation_counts),
            terminal,
            False,
        )
    if state.completion_behavior in (3, 4):
        entries[0] = "R"
        events.append("completion-recovery")
        if state.completion_behavior == 4:
            events.append("inspect-terminal")
        return OrchestrationResult(
            "completion-recovery-after-inspection" if state.completion_behavior == 4 else "completion-recovery",
            tuple(entries),
            tuple(events),
            tuple(mutation_counts),
            "RecoveryRequired",
            False,
        )
    return OrchestrationResult(
        "completed", tuple(entries), tuple(events), tuple(mutation_counts), terminal, False
    )


def settled_observation_outcome(previous: str, current: str) -> str:
    if previous not in ("C", "F"):
        raise ValueError("previous observation must be safely settled")
    if current in ("M", "R"):
        return "recovery-stop"
    if current == previous:
        return "stable"
    return "history-regression"


def run_model(cases: int, seed: int) -> int:
    baseline = OrchestrationState(
        entries=("P", "P", "P"),
        initially_terminal=False,
        cancel_before_ordinal=-1,
        mutation_failure_ordinal=-1,
        release_failure_ordinal=-1,
        completion_behavior=0,
    )
    result = execute(baseline)
    assert result.outcome == "completed"
    assert result.entries == ("C", "C", "C")
    assert result.mutation_counts == (1, 1, 1)
    assert result.terminal_state == "Succeeded"
    assert result.events[-1] == "complete"
    checks = 5

    for previous in ("C", "F"):
        for current in ("P", "C", "F", "M", "R"):
            outcome = settled_observation_outcome(previous, current)
            if current in ("M", "R"):
                assert outcome == "recovery-stop"
            elif current == previous:
                assert outcome == "stable"
            else:
                assert outcome == "history-regression"
            checks += 1

    rng = random.Random(seed)
    for _ in range(cases):
        count = rng.randint(1, 8)
        entries = tuple(rng.choices(("P", "C", "F", "M", "R"), weights=(55, 18, 12, 8, 7), k=count))
        initially_terminal = rng.random() < 0.08
        cancel = rng.randrange(-1, count) if rng.random() < 0.15 else -1
        mutation_failure = rng.randrange(count) if rng.random() < 0.08 else -1
        release_failure = rng.randrange(count) if rng.random() < 0.05 else -1
        completion_behavior = rng.choices((0, 1, 2, 3, 4), weights=(81, 8, 7, 2, 2), k=1)[0]
        state = OrchestrationState(
            entries,
            initially_terminal,
            cancel,
            mutation_failure,
            release_failure,
            completion_behavior,
        )
        result = execute(state)

        assert all(count_value in (0, 1) for count_value in result.mutation_counts)
        assert sum(result.mutation_counts) <= count
        assert result.events.count("complete") <= 1
        assert not result.cleanup_owner or result.outcome == "release-recovery"
        checks += 4

        for ordinal, initial in enumerate(entries):
            if initial in ("C", "F", "M", "R"):
                assert result.mutation_counts[ordinal] == 0
                checks += 1

        first_recovery = next((i for i, value in enumerate(entries) if value in ("M", "R")), None)
        if first_recovery is not None:
            assert result.outcome == "recovery-stop"
            assert sum(result.mutation_counts) == 0
            assert "complete" not in result.events
            checks += 3

        if result.terminal_state in ("Succeeded", "Failed"):
            assert all(value in ("C", "F") for value in result.entries)
            expected = "Succeeded" if all(value == "C" for value in result.entries) else "Failed"
            assert result.terminal_state == expected
            checks += 2

        if result.terminal_state == "RecoveryRequired":
            assert "R" in result.entries
            assert result.outcome in ("completion-recovery", "completion-recovery-after-inspection")
            assert "complete" in result.events
            checks += 3

        if result.outcome == "completion-unproven":
            assert all(value in ("C", "F") for value in result.entries)
            assert result.terminal_state is None
            assert result.events[-1] == "inspect-nonterminal"
            checks += 3

        if result.outcome in ("mutation-recovery", "release-recovery"):
            recovery_index = next(i for i, value in enumerate(result.entries) if value == "R")
            assert all(result.mutation_counts[index] == 0 for index in range(recovery_index + 1, count))
            assert "complete" not in result.events
            checks += 2

    return checks


def require(text: str, needle: str, label: str) -> int:
    if needle not in text:
        raise AssertionError(f"missing {label}: {needle}")
    return 1


def forbid(text: str, needle: str, label: str) -> int:
    if needle in text:
        raise AssertionError(f"forbidden {label}: {needle}")
    return 1


def check_repository(root: Path) -> int:
    orchestrator = (root / "src/FileOp.Core/Operations/FileDeleteOperationOrchestrator.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/FileDeleteOperationOrchestratorTests.cs").read_text(encoding="utf-8")
    recovery_tests = (root / "tests/FileOp.Windows.Tests/FileDeleteOperationOrchestratorRecoveryTerminalTests.cs").read_text(encoding="utf-8")
    regression_tests = (root / "tests/FileOp.Windows.Tests/FileDeleteOperationOrchestratorHistoryRegressionTests.cs").read_text(encoding="utf-8")
    native_tests = (root / "tests/FileOp.Windows.Tests/WindowsFileDeleteOperationOrchestratorTests.cs").read_text(encoding="utf-8")
    docs = (root / "docs/file-delete-multi-entry-orchestration.md").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    plan = (root / "src/FileOp.Core/Operations/FileOperationPlan.cs").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")

    checks = 0
    required = (
        (orchestrator, "public static class FileDeleteOperationOrchestrator", "Core orchestrator"),
        (orchestrator, ".GetAsync(authorization.PlanId, cancellationToken)", "existing history requirement"),
        (orchestrator, "FileDeleteOperationActionEntryState.Committed", "committed observation"),
        (orchestrator, "FileDeleteOperationActionEntryState.Failed", "failed observation"),
        (orchestrator, "CreateSettledEntryObservations", "settled-entry observation memory"),
        (orchestrator, "destructive replay is refused", "settled-entry regression rejection"),
        (orchestrator, "ThrowIfRecoverySensitive", "operation-wide recovery stop"),
        (orchestrator, "FileDeleteOperationPreMutationPreparation", "reviewed read-only preparation"),
        (orchestrator, "FileDeleteOperationFinalMutationLeasePreparation", "reviewed final lease acquisition"),
        (orchestrator, "FileDeleteOperationMutationBarrier", "reviewed durable barrier"),
        (orchestrator, "FileDeleteOperationMutationCommit", "reviewed one-file mutation/settlement"),
        (orchestrator, "CancellationToken.None)", "post-barrier non-cancellable boundary"),
        (orchestrator, ".CompleteAsync(authorization.PlanId, CancellationToken.None)", "non-cancellable operation completion"),
        (orchestrator, "catch (FileDeleteOperationOrchestrationRecoveryRequiredException)", "recovery completion passthrough"),
        (orchestrator, "completion was not proven", "completion-only retry semantics"),
        (orchestrator, "FileDeleteOperationOrchestrationRecoveryRequiredException", "explicit recovery signal"),
        (tests, "PendingEntriesMutateOnceInOrderAndCompleteSucceeded", "all-pending orchestration regression"),
        (tests, "ExistingCommittedAndFailedEntriesAreNeverReplayed", "terminal-entry idempotency regression"),
        (tests, "RecoverySensitiveHistoryStopsBeforeAnyNewProviderAcquisition", "recovery-stop regression"),
        (tests, "CancellationAfterFirstCommitStopsBeforeNextOrdinal", "between-entry cancellation regression"),
        (tests, "CompletionThrowAfterPersistenceIsInspectedWithoutMutationReplay", "completion ambiguity regression"),
        (tests, "PostMutationReleaseFailureStopsBeforeNextOrdinalAndRetainsCleanupOwner", "cleanup ownership regression"),
        (recovery_tests, "CompletionReturningRecoveryRequiredIsSurfacedAsRecovery", "direct recovery completion regression"),
        (recovery_tests, "CompletionThrowThenObservedRecoveryRequiredIsSurfacedAsRecovery", "inspected recovery completion regression"),
        (regression_tests, "PreviouslyCommittedEntryRegressionToPendingIsRejectedBeforeProviderAcquisition", "committed history regression guard"),
        (regression_tests, "PreviouslyFailedEntryRegressionToPendingIsRejectedBeforeProviderAcquisition", "failed history regression guard"),
        (native_tests, "TwoAuthorizedFilesDeleteSequentiallyAndCompleteSucceededHistory", "native two-file orchestration regression"),
        (docs, "Persisted history never recreates any of these live capabilities", "history-not-authority documentation"),
        (docs, "destructive replay authority", "settled-history immutability documentation"),
        (docs, "does not translate arbitrary pre-barrier exceptions into durable `Failed` entries", "conservative failure classification documentation"),
        (gate, "verify_file_delete_multi_entry_orchestration.py --repo-root $repoRoot --cases 50000", "offline gate wiring"),
        (plan, "public enum FileOperationKind\n{\n    Copy,\n    Move,", "generic Delete remains absent"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 unchanged"),
    )
    for text, needle, label in required:
        checks += require(text, needle, label)

    for needle, label in (
        (".BeginAsync(", "orchestrator-owned history begin"),
        (".MarkFailedAsync(", "implicit pre-barrier failure classification"),
        ("DeleteFileW", "path delete"),
        ("File.Delete(", "managed path delete"),
        ("Directory.Delete(", "managed directory delete"),
        ("SafeFileHandle", "raw Windows handle"),
    ):
        checks += forbid(orchestrator, needle, label)

    claim_pos = orchestrator.index(".ClaimAsync(")
    commit_pos = orchestrator.index(".ExecuteAsync(\n                    barrierScope")
    if not claim_pos < commit_pos:
        raise AssertionError("orchestrator must claim MutationStarted before invoking one-file mutation")
    checks += 1
    post_claim = orchestrator[claim_pos:]
    required_none = "barrierScope,\n                    historyStore,\n                    CancellationToken.None"
    checks += require(post_claim, required_none, "caller cancellation cutoff after durable barrier")

    if orchestrator.count(".CompleteAsync(") != 1:
        raise AssertionError("orchestrator must contain exactly one direct CompleteAsync call")
    checks += 1
    if gate.count("verify_file_delete_multi_entry_orchestration.py") != 1:
        raise AssertionError("multi-entry orchestration verifier must be wired exactly once")
    checks += 1

    consumers: list[str] = []
    for subtree in ("src/FileOp.App", "src/FileOp.Indexer"):
        directory = root / subtree
        if not directory.exists():
            continue
        for path in directory.rglob("*.cs"):
            if "FileDeleteOperationOrchestrator" in path.read_text(encoding="utf-8"):
                consumers.append(path.relative_to(root).as_posix())
    allowed_consumers = {"src/FileOp.App/FilesView.Delete.cs"}
    unexpected_consumers = sorted(set(consumers) - allowed_consumers)
    if unexpected_consumers:
        raise AssertionError(
            "delete orchestrator must remain unwired outside the reviewed Files delete session: "
            + ", ".join(unexpected_consumers)
        )
    checks += 1
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0x1450C7E)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {source_checks:,} source/test checks" if args.repo_root else ""
    print(
        f"PASS: multi-entry delete orchestration verified with {model_checks:,} model assertions "
        f"across {args.cases:,} randomized operation states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
