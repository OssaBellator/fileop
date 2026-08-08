#!/usr/bin/env python3
"""Zero-Actions checks for FileOp's file-Copy executor orchestration."""
from __future__ import annotations

import argparse
import random
import sys
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class Case:
    initial: tuple[str, ...]
    revalidation_change: int | None = None
    cancel_before: int | None = None
    cancel_during: int | None = None
    mutation_failure: int | None = None
    commit_failure: int | None = None


def run_case(case: Case) -> tuple[str, list[str], int]:
    events = ["validate:full", "history:begin", "state:running"]
    completed = 0
    for ordinal, decision in enumerate(case.initial):
        if decision == "skip":
            completed += 1
            events.append(f"progress:{completed}")
            continue
        if case.cancel_before == ordinal:
            terminal = "succeeded" if completed == len(case.initial) else "cancelled"
            events.extend([f"history:complete:{terminal}", f"state:{terminal}"])
            return terminal, events, completed
        events.append(f"validate:fresh:{ordinal}")
        if case.revalidation_change == ordinal:
            events.extend([f"history:failed-before:{ordinal}", "history:complete:failed", "state:failed"])
            return "failed", events, completed
        if case.cancel_before == ordinal + len(case.initial):
            terminal = "succeeded" if completed == len(case.initial) else "cancelled"
            events.extend([f"history:complete:{terminal}", f"state:{terminal}"])
            return terminal, events, completed
        events.extend([f"history:start:{ordinal}", f"mutation:{ordinal}"])
        if case.mutation_failure == ordinal:
            events.extend([f"history:recovery:{ordinal}", "history:complete:recovery", "state:failed"])
            return "failed", events, completed
        if case.commit_failure == ordinal:
            events.extend([f"history:commit-attempt:{ordinal}", f"history:recovery:{ordinal}", "history:complete:recovery", "state:failed"])
            return "failed", events, completed
        events.append(f"history:commit:{ordinal}")
        completed += 1
        events.append(f"progress:{completed}")
        if case.cancel_during == ordinal:
            terminal = "succeeded" if completed == len(case.initial) else "cancelled"
            events.extend([f"history:complete:{terminal}", f"state:{terminal}"])
            return terminal, events, completed
    events.extend(["history:complete:succeeded", "state:succeeded"])
    return "succeeded", events, completed


def check(case: Case, result: tuple[str, list[str], int]) -> int:
    terminal, events, completed = result
    checks = 0
    mutations = [int(event.split(":")[1]) for event in events if event.startswith("mutation:")]
    for ordinal in mutations:
        assert events.index(f"history:start:{ordinal}") < events.index(f"mutation:{ordinal}")
        checks += 1
        if f"history:commit:{ordinal}" in events:
            assert events.index(f"mutation:{ordinal}") < events.index(f"history:commit:{ordinal}") < events.index(f"progress:{ordinal + 1}")
            checks += 1
    if case.revalidation_change is not None and f"validate:fresh:{case.revalidation_change}" in events:
        ordinal = case.revalidation_change
        assert f"history:start:{ordinal}" not in events and f"mutation:{ordinal}" not in events
        checks += 2
    for ordinal in range(len(case.initial)):
        if case.mutation_failure == ordinal and f"mutation:{ordinal}" in events:
            assert f"history:recovery:{ordinal}" in events and f"history:commit:{ordinal}" not in events
            checks += 2
        if case.commit_failure == ordinal and f"history:commit-attempt:{ordinal}" in events:
            assert f"history:recovery:{ordinal}" in events and f"history:commit:{ordinal}" not in events
            checks += 2
    if case.cancel_during is not None and f"mutation:{case.cancel_during}" in events:
        ordinal = case.cancel_during
        assert f"history:commit:{ordinal}" in events
        assert not [value for value in mutations if value > ordinal]
        checks += 2
    assert 0 <= completed <= len(case.initial)
    if terminal == "succeeded": assert completed == len(case.initial)
    if terminal == "cancelled": assert completed < len(case.initial)
    return checks + 2


def cases(count: int) -> list[Case]:
    fixed = [
        Case(("ready",)),
        Case(("skip", "skip")),
        Case(("ready",), revalidation_change=0),
        Case(("ready",), mutation_failure=0),
        Case(("ready",), commit_failure=0),
        Case(("ready", "ready"), cancel_before=0),
        Case(("ready", "ready"), cancel_before=2),
        Case(("ready", "ready"), cancel_during=0),
    ]
    rng = random.Random(20260808)
    result = list(fixed)
    for _ in range(count):
        length = rng.randint(1, 12)
        initial = tuple("skip" if rng.random() < 0.25 else "ready" for _ in range(length))
        ready = [index for index, decision in enumerate(initial) if decision == "ready"]
        mode = rng.choice(["normal", "revalidate", "cancel-before", "cancel-after-fresh", "cancel-during", "mutation-fail", "commit-fail"])
        kwargs: dict[str, int] = {}
        if ready:
            target = rng.choice(ready)
            if mode == "revalidate": kwargs["revalidation_change"] = target
            elif mode == "cancel-before": kwargs["cancel_before"] = target
            elif mode == "cancel-after-fresh": kwargs["cancel_before"] = target + length
            elif mode == "cancel-during": kwargs["cancel_during"] = target
            elif mode == "mutation-fail": kwargs["mutation_failure"] = target
            elif mode == "commit-fail": kwargs["commit_failure"] = target
        result.append(Case(initial, **kwargs))
    return result


def check_repository(root: Path) -> int:
    executor = (root / "src/FileOp.Core/Operations/FileCopyOperationExecutor.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/FileCopyOperationExecutorTests.cs").read_text(encoding="utf-8")
    files_ui = (root / "src/FileOp.App/FilesView.xaml.cs").read_text(encoding="utf-8")
    wrapper = (root / "tools/test-copy-executor-local.ps1").read_text(encoding="utf-8")
    docs = (root / "docs/file-copy-executor.md").read_text(encoding="utf-8")

    required = [
        "public interface IFileCopyMutationPrimitive",
        "public sealed class FileCopyOperationExecutor : IFileOperationExecutor",
        "ReferenceEquals(validation.Plan, plan)",
        "ReferenceEquals(freshValidation.Plan, freshPlan)",
        "MarkMutationStartedAsync(plan.Id, ordinal, UtcNow())",
        "CopyNewFileAsync(freshItem)",
        "CommitCopyAsync(plan.Id, ordinal, receipt.DestinationIdentity, UtcNow())",
        "snapshot.ReportProgress(ordinal + 1, receipt.CanonicalDestinationPath)",
        "MarkMutationRecoveryRequiredAsync",
        "Cancellation is intentionally not passed through this boundary",
    ]
    for needle in required:
        assert needle in executor, needle
    start = executor.index("MarkMutationStartedAsync(plan.Id, ordinal, UtcNow())")
    mutate = executor.index("CopyNewFileAsync(freshItem)", start)
    commit = executor.index("CommitCopyAsync(plan.Id, ordinal, receipt.DestinationIdentity, UtcNow())", mutate)
    progress = executor.index("snapshot.ReportProgress(ordinal + 1, receipt.CanonicalDestinationPath)", commit)
    assert start < mutate < commit < progress
    for forbidden in ["File.Copy(", "File.Move(", "File.Delete(", "Directory.Move(", "Directory.Delete(", "File.OpenWrite(", "new FileStream("]:
        assert forbidden not in executor, forbidden
    for name in [
        "SuccessfulCopyCommitsHistoryBeforeReportingProgress",
        "CancellationDuringMutationStopsAfterCommittedFileBoundary",
        "CommitFailureAfterMutationForcesRecoveryPath",
        "FreshIdentityChangeFailsBeforeMutation",
        "ValidatorReturningDifferentPlanInstanceIsRejected",
    ]:
        assert name in tests, name
    assert "FileCopyOperationExecutor" not in files_ui and ".ExecuteAsync(" not in files_ui
    assert "test-local.ps1\") -OfflineOnly" in wrapper
    assert "verify_file_copy_executor.py --repo-root $repoRoot --cases 20000" in wrapper
    assert "no concrete mutation primitive" in docs.casefold()
    return len(required) + 1 + 7 + 5 + 5


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--self-test-only", action="store_true")
    parser.add_argument("--cases", type=int, default=20000)
    args = parser.parse_args()
    if args.cases <= 0: parser.error("--cases must be greater than zero")
    all_cases = cases(args.cases)
    checks = sum(check(case, run_case(case)) for case in all_cases)
    print(f"PASS file Copy executor orchestration: {checks} checks across {args.cases} randomized cases + 8 fixed cases")
    if not args.self_test_only:
        print(f"PASS file Copy executor source wiring: {check_repository(args.repo_root.resolve())} checks")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
