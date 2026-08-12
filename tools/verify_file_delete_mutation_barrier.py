#!/usr/bin/env python3
"""Verify durable delete mutation-barrier ownership and recovery semantics."""
from __future__ import annotations

import argparse
import random
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class BarrierState:
    pre_cancelled: bool
    final_scope_live: bool
    capability_held: bool
    mark_outcome: int
    recovery_succeeds: bool
    release_succeeds: bool
    barrier_scope_disposal_succeeds: bool


# mark_outcome:
# 0 = throw, inspection proves Pending
# 1 = return exact MutationStarted
# 2 = return malformed evidence after durable MutationStarted
# 3 = throw after durable MutationStarted
# 4 = throw and inspection cannot prove Pending or MutationStarted

def simulate(state: BarrierState) -> tuple[bool, tuple[str, ...], bool, bool, bool, bool, bool]:
    """Return (scope, events, old_scope_held, lease_held, barrier, authority, mutation)."""
    events: list[str] = []
    old_scope_held = state.final_scope_live
    lease_held = state.final_scope_live and state.capability_held
    barrier = False
    authority = False
    mutation = False

    if state.pre_cancelled:
        return False, tuple(events), old_scope_held, lease_held, barrier, authority, mutation
    if not state.final_scope_live or not state.capability_held:
        return False, tuple(events), old_scope_held, lease_held, barrier, authority, mutation

    events.append("detach")
    old_scope_held = False
    lease_held = True
    events.append("mark-started")

    if state.mark_outcome == 1:
        events.append("validate-started")
        barrier = True
        authority = True
        return True, tuple(events), old_scope_held, lease_held, barrier, authority, mutation

    if state.mark_outcome == 0:
        events.append("inspect-pending")
    elif state.mark_outcome == 2:
        events.append("validate-failed")
        events.append("mark-recovery")
        if state.recovery_succeeds:
            events.append("recovery-durable")
    elif state.mark_outcome == 3:
        events.append("inspect-started")
        events.append("mark-recovery")
        if state.recovery_succeeds:
            events.append("recovery-durable")
    else:
        events.append("inspect-ambiguous")

    events.append("release-final")
    if state.release_succeeds:
        lease_held = False
    return False, tuple(events), old_scope_held, lease_held, barrier, authority, mutation


def run_model(cases: int, seed: int) -> int:
    checks = 0
    baseline = BarrierState(
        pre_cancelled=False,
        final_scope_live=True,
        capability_held=True,
        mark_outcome=1,
        recovery_succeeds=True,
        release_succeeds=True,
        barrier_scope_disposal_succeeds=True,
    )
    scope, events, old_held, lease_held, barrier, authority, mutation = simulate(baseline)
    assert scope
    assert events == ("detach", "mark-started", "validate-started")
    assert not old_held
    assert lease_held
    assert barrier
    assert authority
    assert not mutation
    checks += 7

    rng = random.Random(seed)
    for _ in range(cases):
        state = BarrierState(
            pre_cancelled=rng.random() < 0.05,
            final_scope_live=rng.random() >= 0.06,
            capability_held=rng.random() >= 0.07,
            mark_outcome=rng.randrange(5),
            recovery_succeeds=rng.random() >= 0.10,
            release_succeeds=rng.random() >= 0.08,
            barrier_scope_disposal_succeeds=rng.random() >= 0.08,
        )
        scope, events, old_held, lease_held, barrier, authority, mutation = simulate(state)

        assert not mutation
        assert events.count("detach") <= 1
        assert events.count("mark-started") <= 1
        assert events.count("mark-recovery") <= 1
        assert events.count("release-final") <= 1
        checks += 5

        if state.pre_cancelled:
            assert not scope
            assert events == ()
            assert old_held == state.final_scope_live
            assert lease_held == (state.final_scope_live and state.capability_held)
            checks += 4
            continue

        if not state.final_scope_live or not state.capability_held:
            assert not scope
            assert events == ()
            assert old_held == state.final_scope_live
            assert lease_held == (state.final_scope_live and state.capability_held)
            checks += 4
            continue

        assert events[0] == "detach"
        assert events[1] == "mark-started"
        assert not old_held
        checks += 3

        if state.mark_outcome == 1:
            assert scope
            assert barrier
            assert authority
            assert lease_held
            assert "release-final" not in events
            assert "mark-recovery" not in events
            checks += 6

            # Successful barrier-scope disposal clears authority only after release succeeds.
            dispose_attempts = 1
            if not state.barrier_scope_disposal_succeeds:
                assert lease_held
                assert authority
                checks += 2
                dispose_attempts += 1
            lease_held = False
            authority = False
            assert not lease_held
            assert not authority
            assert dispose_attempts in (1, 2)
            assert barrier
            checks += 4
            continue

        assert not scope
        assert not barrier
        assert not authority
        assert events[-1] == "release-final"
        checks += 4

        if state.mark_outcome == 0:
            assert "inspect-pending" in events
            assert "mark-recovery" not in events
            checks += 2
        elif state.mark_outcome in (2, 3):
            assert "mark-recovery" in events
            assert ("recovery-durable" in events) == state.recovery_succeeds
            checks += 2
        else:
            assert "inspect-ambiguous" in events
            assert "mark-recovery" not in events
            checks += 2

        assert lease_held == (not state.release_succeeds)
        checks += 1

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
    final_lease = (root / "src/FileOp.Core/Operations/FileDeleteOperationFinalMutationLease.cs").read_text(encoding="utf-8")
    barrier = (root / "src/FileOp.Core/Operations/FileDeleteOperationMutationBarrier.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/FileDeleteOperationMutationBarrierTests.cs").read_text(encoding="utf-8")
    docs = (root / "docs/file-delete-mutation-barrier.md").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    plan = (root / "src/FileOp.Core/Operations/FileOperationPlan.cs").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")

    production_consumers: list[str] = []
    for subtree in ("src/FileOp.App", "src/FileOp.Windows", "src/FileOp.Indexer"):
        directory = root / subtree
        if not directory.exists():
            continue
        for path in directory.rglob("*.cs"):
            text = path.read_text(encoding="utf-8")
            if "FileDeleteOperationMutationBarrier" in text or "FileDeleteOperationMutationBarrierScope" in text:
                production_consumers.append(path.relative_to(root).as_posix())
    if production_consumers:
        raise AssertionError(
            "delete mutation barrier must remain unwired from production: "
            + ", ".join(production_consumers)
        )

    checks = 0
    required = (
        (final_lease, "internal async ValueTask<IFileDeleteOperationFinalMutationLease> DetachLeaseAsync()", "internal lease transfer"),
        (final_lease, "await _disposeGate.WaitAsync().ConfigureAwait(false);", "shared ownership/disposal gate"),
        (final_lease, "Volatile.Write(ref _lease, null);\n            return lease;", "old scope becomes inert before transfer returns"),
        (barrier, "public sealed class FileDeleteOperationMutationBarrierScope : IAsyncDisposable", "barrier scope"),
        (barrier, "private IFileDeleteOperationFinalMutationLease? _lease;", "private live capability ownership"),
        (barrier, "public bool MutationBarrierSatisfied => true;", "durable barrier evidence"),
        (barrier, "lease.DeleteAccessCapabilityHeld &&\n                !lease.DeleteMutationAuthorized &&\n                ReferenceEquals(lease.Evidence, FinalEvidence)", "authority requires exact live provider contract"),
        (barrier, "public bool DeleteMutationPerformed => false;", "no mutation performed"),
        (barrier, "cancellationToken.ThrowIfCancellationRequested();", "pre-transfer cancellation"),
        (barrier, "var lease = await finalLeaseScope.DetachLeaseAsync().ConfigureAwait(false);", "atomic transfer consumption"),
        (barrier, ".MarkMutationStartedAsync(operationId, ordinal, CancellationToken.None)", "non-cancellable durable barrier"),
        (barrier, ".GetAsync(priorBinding.HistorySnapshot.OperationId, CancellationToken.None)", "non-cancellable outcome inspection"),
        (barrier, ".MarkMutationRecoveryRequiredAsync(", "recovery transition"),
        (barrier, "CancellationToken.None)\n                .ConfigureAwait(false);", "non-cancellable recovery/inspection calls"),
        (barrier, "IsExactPendingHistory(priorBinding, observed)", "proven pre-barrier exception outcome"),
        (barrier, "TryValidateBarrierHistory(", "durable MutationStarted validation"),
        (barrier, "ValidateStaticHistoryProvenance", "exact operation provenance validation"),
        (barrier, "ValidateLiveLease(finalEvidence, lease);", "capability recheck after durable transition"),
        (barrier, "await lease.DisposeAsync().ConfigureAwait(false);\n            Volatile.Write(ref _lease, null);", "barrier-scope disposal retry ownership"),
        (tests, "SuccessfulClaimTransfersFinalLeaseAndCreatesLiveBarrierAuthority", "successful transfer regression"),
        (tests, "RetainedFinalScopeAliasCannotDisposeLeaseAfterTransfer", "retained alias regression"),
        (tests, "CancellationBeforeDetachLeavesFinalScopeUntouched", "pre-cancellation regression"),
        (tests, "CancellationAfterDetachCannotInterruptDurableBarrierSection", "post-detach cancellation regression"),
        (tests, "BarrierWriteThrowWithPendingHistoryReleasesWithoutRecovery", "pending inspection regression"),
        (tests, "BarrierWriteThrowAfterPersistIsInspectedRecoveredAndReleased", "post-persist exception recovery regression"),
        (tests, "InvalidReturnedBarrierHistoryMarksRecoveryBeforeLeaseRelease", "invalid return recovery regression"),
        (tests, "RecoveryPersistenceFailureLeavesDurableMutationStartedSignal", "recovery failure regression"),
        (tests, "BarrierScopeDisposalFailureRetainsAuthorityUntilRetrySucceeds", "barrier disposal retry regression"),
        (docs, "Cancellation is honored only before ownership transfer", "cancellation boundary documentation"),
        (docs, "durable `MutationStarted`", "restart recovery signal documentation"),
        (docs, "does not perform deletion", "no-delete documentation"),
        (gate, "verify_file_delete_mutation_barrier.py --repo-root $repoRoot --cases 50000", "offline gate wiring"),
        (plan, "public enum FileOperationKind\n{\n    Copy,\n    Move,", "generic delete remains absent"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 unchanged"),
    )
    for text, needle, label in required:
        checks += require(text, needle, label)

    cancel_pos = barrier.index("cancellationToken.ThrowIfCancellationRequested();")
    detach_pos = barrier.index("var lease = await finalLeaseScope.DetachLeaseAsync().ConfigureAwait(false);")
    mark_pos = barrier.index(".MarkMutationStartedAsync(operationId, ordinal, CancellationToken.None)")
    if not cancel_pos < detach_pos < mark_pos:
        raise AssertionError("delete mutation barrier ordering must remain cancellation -> detach -> non-cancellable durable barrier")
    checks += 1

    if barrier[detach_pos:].count("cancellationToken") != 0:
        raise AssertionError("caller cancellation token must not flow into the post-detach critical section")
    checks += 1

    for text, needle, label in (
        (barrier, "CommitDeletedAsync(", "delete settlement"),
        (barrier, "SetFileInformationByHandle", "same-handle mutation API"),
        (barrier, "NtSetInformationFile", "native mutation API"),
        (barrier, "DeleteFileW", "path delete API"),
        (barrier, "File.Delete(", "managed file deletion"),
        (barrier, "Directory.Delete(", "managed directory deletion"),
        (barrier, "SafeFileHandle", "raw handle exposure"),
        (barrier, "FileOperationKind.Delete", "generic delete integration"),
        (protocol, "FileDeleteOperationMutationBarrier", "protocol transport"),
    ):
        checks += forbid(text, needle, label)

    if gate.count("verify_file_delete_mutation_barrier.py") != 1:
        raise AssertionError("delete mutation barrier verifier must be wired exactly once")
    checks += 1
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0xBA771E5)
    args = parser.parse_args()
    if args.cases < 0:
        parser.error("--cases must be non-negative")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {source_checks:,} source/test checks" if args.repo_root else ""
    print(
        f"PASS: delete mutation barrier verified with {model_checks:,} randomized lifecycle assertions "
        f"across {args.cases:,} states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
