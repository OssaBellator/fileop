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
            if case.cancel_before == ordinal:
                terminal = "succeeded" if completed == len(case.initial) else "cancelled"
                events.extend([f"history:complete:{terminal}", f"state:{terminal}"])
                return terminal, events, completed
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


def check_invariants(case: Case, result: tuple[str, list[str], int]) -> int:
    terminal, events, completed = result
    checks = 0
    mutations = [int(event.split(":")[1]) for event in events if event.startswith("mutation:")]
    for ordinal in mutations:
        start = events.index(f"history:start:{ordinal}")
        mutate = events.index(f"mutation:{ordinal}")
        assert start < mutate
        checks += 1
        if f"history:commit:{ordinal}" in events:
            commit = events.index(f"history:commit:{ordinal}")
            progress = events.index(f"progress:{ordinal + 1}")
            assert mutate < commit < progress
            checks += 1

    if case.revalidation_change is not None and f"validate:fresh:{case.revalidation_change}" in events:
        ordinal = case.revalidation_change
        assert f"history:start:{ordinal}" not in events
        assert f"mutation:{ordinal}" not in events
        checks += 2

    for ordinal in range(len(case.initial)):
        if case.mutation_failure == ordinal and f"mutation:{ordinal}" in events:
            assert f"history:recovery:{ordinal}" in events
            assert f"history:commit:{ordinal}" not in events
            assert f"progress:{ordinal + 1}" not in events
            checks += 3
        if case.commit_failure == ordinal and f"history:commit-attempt:{ordinal}" in events:
            assert f"history:recovery:{ordinal}" in events
            assert f"history:commit:{ordinal}" not in events
            assert f"progress:{ordinal + 1}" not in events
            checks += 3

    if case.cancel_during is not None and f"mutation:{case.cancel_during}" in events:
        ordinal = case.cancel_during
        assert f"history:commit:{ordinal}" in events
        assert not [value for value in mutations if value > ordinal]
        checks += 2

    assert 0 <= completed <= len(case.initial)
    if terminal == "succeeded":
        assert completed == len(case.initial)
    elif terminal == "cancelled":
        assert completed < len(case.initial)
    checks += 2
    return checks


def fixed_cases() -> list[Case]:
    return [
        Case(("ready",)),
        Case(("skip", "skip")),
        Case(("ready",), revalidation_change=0),
        Case(("ready",), mutation_failure=0),
        Case(("ready",), commit_failure=0),
        Case(("ready", "ready"), cancel_before=0),
        Case(("ready", "ready"), cancel_before=2),
        Case(("ready", "ready"), cancel_during=0),
        Case(("skip", "ready"), cancel_during=1),
    ]


def randomized_cases(count: int) -> list[Case]:
    rng = random.Random(20260808)
    cases: list[Case] = []
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
        cases.append(Case(initial, **kwargs))
    return cases


def check_repository(root: Path) -> int:
    files = {
        "executor": root / "src/FileOp.Core/Operations/FileCopyOperationExecutor.cs",
        "execution": root / "src/FileOp.Core/Operations/FileOperationExecution.cs",
        "history": root / "src/FileOp.Core/Operations/FileOperationActionHistory.cs",
        "store": root / "src/FileOp.Core/Operations/SqliteFileOperationActionHistoryStore.cs",
        "tests": root / "tests/FileOp.Windows.Tests/FileCopyOperationExecutorTests.cs",
        "files_ui": root / "src/FileOp.App/FilesView.xaml.cs",
        "docs": root / "docs/file-copy-executor.md",
        "wrapper": root / "tools/test-copy-executor-local.ps1",
    }
    missing = [str(path) for path in files.values() if not path.is_file()]
    if missing:
        raise FileNotFoundError(", ".join(missing))
    source = {name: path.read_text(encoding="utf-8") for name, path in files.items()}

    required = [
        "public interface IFileCopyMutationPrimitive",
        "public sealed class FileCopyOperationExecutor : IFileOperationExecutor",
        "ReferenceEquals(validation.Plan, plan)",
        "ReferenceEquals(freshValidation.Plan, freshPlan)",
        ".MarkMutationStartedAsync(plan.Id, ordinal, UtcNow())",
        ".CopyNewFileAsync(freshItem)",
        ".CommitCopyAsync(",
        "snapshot.ReportProgress(ordinal + 1, receipt.CanonicalDestinationPath)",
        "MarkMutationRecoveryRequiredAsync(",
        "Cancellation is intentionally not passed through this boundary",
        "plan.Kind != FileOperationKind.Copy",
        "plan.Intent.Entries.Any(static entry => entry.IsDirectory)",
    ]
    for needle in required:
        assert needle in source["executor"], needle

    start = source["executor"].index(".MarkMutationStartedAsync(plan.Id, ordinal, UtcNow())")
    mutate = source["executor"].index(".CopyNewFileAsync(freshItem)", start)
    commit = source["executor"].index(".CommitCopyAsync(", mutate)
    progress = source["executor"].index("snapshot.ReportProgress(ordinal + 1, receipt.CanonicalDestinationPath)", commit)
    assert start < mutate < commit < progress

    for forbidden in ["File.Copy(", "File.Move(", "File.Delete(", "Directory.Move(", "Directory.Delete(", "File.OpenWrite(", "new FileStream("]:
        assert forbidden not in source["executor"], forbidden

    assert "public interface IFileOperationExecutor" in source["execution"]
    assert "CommitCopyAsync" in source["history"]
    # Guard the already-merged custom-constructor call site that source-only review found.
    assert "CompletedAtUtc: null" not in source["store"], "Action-history store uses stale primary-record named argument casing"
    assert "TerminalState: null" not in source["store"], "Action-history store uses stale primary-record named argument casing"
    assert "FileCopyOperationExecutor" not in source["files_ui"]
    assert ".ExecuteAsync(" not in source["files_ui"]
    assert "completedAtUtc: null" in source["tests"]
    assert "terminalState: null" in source["tests"]
    assert "# File Copy executor orchestration" in source["docs"]
    assert "test-local.ps1\") -OfflineOnly" in source["wrapper"]
    assert "verify_file_copy_executor.py --repo-root $repoRoot --cases 20000" in source["wrapper"]
    return len(required) + 1 + 7 + 10


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--self-test-only", action="store_true")
    parser.add_argument("--cases", type=int, default=20000)
    args = parser.parse_args()
    if args.cases <= 0:
        parser.error("--cases must be greater than zero")

    checks = 0
    fixed = fixed_cases()
    for case in fixed + randomized_cases(args.cases):
        checks += check_invariants(case, run_case(case))
    print(f"PASS file Copy executor orchestration: {checks} checks across {args.cases} randomized cases + {len(fixed)} fixed cases")
    if not args.self_test_only:
        print(f"PASS file Copy executor source wiring: {check_repository(args.repo_root.resolve())} checks")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
