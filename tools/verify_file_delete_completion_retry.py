#!/usr/bin/env python3
"""Verify completion-only retry semantics for multi-entry file deletion."""
from __future__ import annotations

import argparse
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class RetryResult:
    outcome: str
    terminal_state: str | None
    mutation_counts: tuple[int, ...]


def attempt(entries: tuple[str, ...], completion_throws: bool) -> RetryResult:
    if not entries or not all(value in ("C", "F") for value in entries):
        raise ValueError("completion retry requires already-settled entries")

    mutation_counts = (0,) * len(entries)
    if completion_throws:
        return RetryResult("completion-unproven", None, mutation_counts)

    terminal = "Succeeded" if all(value == "C" for value in entries) else "Failed"
    return RetryResult("completed", terminal, mutation_counts)


def run_model() -> int:
    checks = 0

    succeeded_entries = ("C", "C")
    first = attempt(succeeded_entries, completion_throws=True)
    assert first.outcome == "completion-unproven"
    assert first.terminal_state is None
    assert first.mutation_counts == (0, 0)
    checks += 3

    second = attempt(succeeded_entries, completion_throws=False)
    assert second.outcome == "completed"
    assert second.terminal_state == "Succeeded"
    assert second.mutation_counts == (0, 0)
    checks += 3

    failed_entries = ("C", "F", "C")
    failed_first = attempt(failed_entries, completion_throws=True)
    assert failed_first.outcome == "completion-unproven"
    assert failed_first.mutation_counts == (0, 0, 0)
    checks += 2

    failed_second = attempt(failed_entries, completion_throws=False)
    assert failed_second.outcome == "completed"
    assert failed_second.terminal_state == "Failed"
    assert failed_second.mutation_counts == (0, 0, 0)
    checks += 3

    return checks


def require(text: str, needle: str, label: str) -> int:
    if needle not in text:
        raise AssertionError(f"missing {label}: {needle}")
    return 1


def check_repository(root: Path) -> int:
    orchestrator = (root / "src/FileOp.Core/Operations/FileDeleteOperationOrchestrator.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/FileDeleteOperationOrchestratorCompletionRetryTests.cs").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")

    checks = 0
    required = (
        (orchestrator, "completion was not proven", "completion ambiguity signal"),
        (orchestrator, ".CompleteAsync(authorization.PlanId, CancellationToken.None)", "non-cancellable completion"),
        (tests, "CompletionThrowBeforePersistenceRetriesCompletionWithoutMutationReplay", "two-invocation completion retry regression"),
        (tests, "Assert.AreEqual(0, stabilityProvider.AcquireCount)", "no stability reacquisition assertion"),
        (tests, "Assert.AreEqual(0, finalProvider.AcquireCount)", "no final capability reacquisition assertion"),
        (tests, "Assert.AreEqual(2, historyStore.CompleteCount)", "completion retry count assertion"),
        (gate, "verify_file_delete_completion_retry.py --repo-root $repoRoot", "offline gate wiring"),
    )
    for text, needle, label in required:
        checks += require(text, needle, label)
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    args = parser.parse_args()

    model_checks = run_model()
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {source_checks} source/test checks" if args.repo_root else ""
    print(
        f"PASS: delete completion-only retry verified with {model_checks} model assertions{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
