#!/usr/bin/env python3
"""Portable model/source checks for the dormant recursive directory Copy transaction."""
from __future__ import annotations

import argparse
import random
import re
import sys
from pathlib import Path


def trace(action_count: int, cancel_after: int | None, fail_at: int | None) -> list[str]:
    events = ["fresh-acquire", "revalidate", "begin-history"]
    for ordinal in range(action_count):
        if cancel_after is not None and ordinal >= cancel_after:
            events.append("cancelled")
            return events
        events += [f"barrier:{ordinal}", f"mutate:{ordinal}"]
        if fail_at == ordinal:
            events += [f"recovery:{ordinal}", "recovery-terminal"]
            return events
        events.append(f"commit:{ordinal}")
    events.append("succeeded")
    return events


def run_model(cases: int) -> int:
    assert trace(1, None, None) == [
        "fresh-acquire", "revalidate", "begin-history", "barrier:0", "mutate:0", "commit:0", "succeeded"
    ]
    assert trace(2, 1, None)[-1] == "cancelled"
    assert trace(2, None, 0)[-1] == "recovery-terminal"
    checks = 3
    rng = random.Random(0xD1C0_7A05)
    for _ in range(cases):
        count = rng.randrange(1, 12)
        cancel_after = rng.choice([None, *range(count + 1)])
        fail_at = rng.choice([None, *range(count)])
        events = trace(count, cancel_after, fail_at)
        assert events.index("fresh-acquire") < events.index("revalidate") < events.index("begin-history")
        checks += 1
        for ordinal in range(count):
            mutate = f"mutate:{ordinal}"
            if mutate in events:
                assert f"barrier:{ordinal}" in events
                assert events.index(f"barrier:{ordinal}") < events.index(mutate)
                checks += 1
                if f"commit:{ordinal}" in events:
                    assert events.index(mutate) < events.index(f"commit:{ordinal}")
                    checks += 1
        if "recovery-terminal" in events:
            assert any(event.startswith("recovery:") for event in events)
            assert "succeeded" not in events
            checks += 2
        if "cancelled" in events:
            for index, event in enumerate(events):
                if event.startswith("mutate:"):
                    ordinal = event.split(":", 1)[1]
                    if f"recovery:{ordinal}" not in events:
                        assert f"commit:{ordinal}" in events[index + 1 :]
                        checks += 1
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
    history = read(root, "src/FileOp.Core/Operations/DirectoryCopyActionHistory.cs")
    store = read(root, "src/FileOp.Core/Operations/SqliteDirectoryCopyActionHistoryStore.cs")
    executor = read(root, "src/FileOp.Core/Operations/DirectoryCopyTransactionExecutor.cs")
    tests = read(root, "tests/FileOp.Windows.Tests/DirectoryCopyTransactionTests.cs")
    checks = 0
    checks += require(
        history,
        "public enum DirectoryCopyActionKind",
        "CreateDirectory",
        "CopyFile",
        "public enum DirectoryCopyActionEntryState",
        "MutationStarted",
        "Committed",
        "RecoveryRequired",
        "public sealed record DirectoryCopyTransactionPlan",
        "DestinationParentOrdinal",
        "DestinationContentFingerprint",
        "Directory Copy destination cannot be the reviewed source root or lie inside its subtree.",
        "Committed directory Copy file history requires destination SHA-256 evidence.",
        "public bool RequiresRecovery",
        "public bool GrantsAutomaticReplayAuthority => false",
        "public bool GrantsRollbackAuthority => false",
        "public bool GrantsDeleteAuthority => false",
        "public interface IDirectoryCopyActionHistoryStore",
    )
    checks += require(
        store,
        "directory_copy_operations",
        "directory_copy_entries",
        "freshGate.CanBeginDurableHistory",
        "ReferenceEquals(freshGate.ReviewedManifest, plan.ReviewedManifest)",
        "BuildActions(plan)",
        "DestinationParentOrdinal",
        "destination_fingerprint_algorithm",
        "destination_fingerprint_hex",
        "ValidateFingerprintTransition(",
        "Committed directory Copy file history requires destination SHA-256 evidence.",
        "DirectoryCopyActionEntryState.Pending",
        "DirectoryCopyActionEntryState.MutationStarted",
        "DirectoryCopyActionEntryState.Committed",
        "DirectoryCopyActionEntryState.RecoveryRequired",
        "A mutation-sensitive directory Copy cannot settle as Failed/Cancelled",
        "Directory Copy cannot succeed until every recursive action is committed.",
    )
    checks += require(
        executor,
        "public sealed class DirectoryCopyTransactionExecutor",
        "if (!fresh.CanBeginDurableHistory)",
        ".BeginAsync(plan, fresh, UtcNow()",
        ".MarkMutationStartedAsync(plan.OperationId, ordinal, UtcNow())",
        "No cancellation token crosses the durable MutationStarted boundary.",
        ".ExecuteNoReplaceAsync(new DirectoryCopyMutationRequest(",
        "TryResolveCommittedParent(",
        "parent.State != DirectoryCopyActionEntryState.Committed",
        "receipt.DestinationIdentity.VolumeSerialNumber != parentIdentity.VolumeSerialNumber",
        "Directory Copy file commit requires destination SHA-256 content evidence",
        "lease.Receipt.DestinationContentFingerprint",
        ".MarkRecoveryRequiredAsync(",
        "observedFingerprint",
        "DirectoryCopyActionTerminalState.RecoveryRequired",
        "DirectoryCopyActionTerminalState.Succeeded",
    )
    fresh_gate_call = re.search(
        r"_freshGate\s*\.PrepareAsync\(plan\.ReviewedManifest",
        executor,
    )
    assert fresh_gate_call, "Directory Copy executor must invoke the fresh-manifest gate before durable history"
    assert fresh_gate_call.start() < executor.index(".BeginAsync(plan, fresh, UtcNow()")
    assert executor.index(".MarkMutationStartedAsync(plan.OperationId, ordinal, UtcNow())") < executor.index(".ExecuteNoReplaceAsync(new DirectoryCopyMutationRequest(")
    checks += 3
    checks += require(
        tests,
        "SqliteHistoryPersistsParentLinkedRecursiveActions",
        "ExecutorPersistsMutationBarrierAndChainsCommittedParentIdentity",
        "ChangedFreshManifestBlocksBeforeDurableHistoryAndMutation",
        "ProviderFailureAfterMutationStartedSettlesRecoveryRequired",
        "CancellationDuringMutationCommitsCurrentActionThenStopsAtBoundary",
        "FileReceiptWithoutFingerprintCannotCommitAndRequiresRecovery",
        "TransactionPlanRejectsDestinationInsideReviewedSourceTree",
        "DestinationContentFingerprint is not null",
        "Assert.ThrowsAsync<InvalidOperationException>",
        "Assert.Throws<ArgumentException>",
    )
    checks += forbid(
        tests,
        "Assert.ThrowsException",
        "Assert.ThrowsExceptionAsync",
    )
    combined = store + executor
    checks += forbid(
        combined,
        "Directory.Enumerate",
        "Directory.Move(",
        "File.Copy(",
        "File.Move(",
        "File.Delete(",
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
    print(f"PASS directory Copy transaction model: {model_checks:,} checks across {args.cases:,} randomized cases")
    if not args.model_only:
        source_checks = check_repository(args.repo_root.resolve())
        print(f"PASS directory Copy transaction source contract: {source_checks} checks")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
