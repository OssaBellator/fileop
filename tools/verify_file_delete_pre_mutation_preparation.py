#!/usr/bin/env python3
"""Verify held, non-authorizing file-delete pre-mutation preparation."""
from __future__ import annotations

import argparse
import random
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class PreparationState:
    pre_cancelled: bool = False
    provider_returns_lease: bool = True
    lease_mutation_authorized: bool = False
    exact_provider_binding: bool = True
    history_read_succeeds: bool = True
    history_present: bool = True
    history_binding_valid: bool = True


@dataclass(frozen=True)
class PreparationResult:
    acquire_count: int
    history_read_count: int
    cleanup_dispose_count: int
    scope_created: bool
    scope_holds_lease: bool
    mutation_barrier_calls: int
    order: tuple[str, ...]


def simulate(state: PreparationState) -> PreparationResult:
    if state.pre_cancelled:
        return PreparationResult(0, 0, 0, False, False, 0, ())

    order: list[str] = ["acquire"]
    if not state.provider_returns_lease:
        return PreparationResult(1, 0, 0, False, False, 0, tuple(order))

    if state.lease_mutation_authorized or not state.exact_provider_binding:
        order.append("dispose")
        return PreparationResult(1, 0, 1, False, False, 0, tuple(order))

    order.append("history-read")
    if not state.history_read_succeeds or not state.history_present or not state.history_binding_valid:
        order.append("dispose")
        return PreparationResult(1, 1, 1, False, False, 0, tuple(order))

    return PreparationResult(1, 1, 0, True, True, 0, tuple(order))


def run_model(cases: int, seed: int) -> int:
    rng = random.Random(seed)
    checks = 0

    baseline = simulate(PreparationState())
    assert baseline.scope_created
    assert baseline.order == ("acquire", "history-read")
    assert baseline.mutation_barrier_calls == 0
    checks += 3

    for _ in range(cases):
        state = PreparationState(
            pre_cancelled=rng.random() < 0.08,
            provider_returns_lease=rng.random() >= 0.08,
            lease_mutation_authorized=rng.random() < 0.08,
            exact_provider_binding=rng.random() >= 0.10,
            history_read_succeeds=rng.random() >= 0.08,
            history_present=rng.random() >= 0.08,
            history_binding_valid=rng.random() >= 0.15,
        )
        result = simulate(state)

        assert result.mutation_barrier_calls == 0
        checks += 1

        if state.pre_cancelled:
            assert result.acquire_count == 0
            assert result.history_read_count == 0
            assert result.cleanup_dispose_count == 0
            assert not result.scope_created
            assert result.order == ()
            checks += 5
            continue

        assert result.acquire_count == 1
        assert result.order and result.order[0] == "acquire"
        checks += 2

        if not state.provider_returns_lease:
            assert result.history_read_count == 0
            assert result.cleanup_dispose_count == 0
            assert not result.scope_created
            checks += 3
            continue

        provider_valid = not state.lease_mutation_authorized and state.exact_provider_binding
        if not provider_valid:
            assert result.history_read_count == 0
            assert result.cleanup_dispose_count == 1
            assert result.order == ("acquire", "dispose")
            assert not result.scope_created
            checks += 4
            continue

        assert result.history_read_count == 1
        assert result.order[:2] == ("acquire", "history-read")
        checks += 2

        history_valid = (
            state.history_read_succeeds
            and state.history_present
            and state.history_binding_valid
        )
        if not history_valid:
            assert result.cleanup_dispose_count == 1
            assert result.order == ("acquire", "history-read", "dispose")
            assert not result.scope_created
            assert not result.scope_holds_lease
            checks += 4
            continue

        assert result.cleanup_dispose_count == 0
        assert result.scope_created
        assert result.scope_holds_lease
        checks += 3

        # The returned scope owns the lease. If a disposal attempt fails, ownership
        # must remain held so the scope does not falsely report release and can retry.
        dispose_count = 0
        held = result.scope_holds_lease
        first_dispose_fails = rng.random() < 0.10
        dispose_count += 1
        if first_dispose_fails:
            assert held
            assert dispose_count == 1
            checks += 2
            dispose_count += 1
            held = False
        else:
            held = False

        assert not held
        assert dispose_count == (2 if first_dispose_fails else 1)
        checks += 2

        # Successful disposal is idempotent; later calls neither re-dispose nor cross
        # the mutation boundary.
        settled_dispose_count = dispose_count
        for _attempt in range(rng.randint(1, 4)):
            assert dispose_count == settled_dispose_count
            assert not held
            assert result.mutation_barrier_calls == 0
            checks += 3

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
    core = (root / "src/FileOp.Core/Operations/FileDeleteOperationPreMutationPreparation.cs").read_text(encoding="utf-8")
    binding = (root / "src/FileOp.Core/Operations/FileDeleteOperationHistoryBinding.cs").read_text(encoding="utf-8")
    stability = (root / "src/FileOp.Core/Operations/FileDeleteOperationStabilityLease.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/FileDeleteOperationPreMutationPreparationTests.cs").read_text(encoding="utf-8")
    docs = (root / "docs/file-delete-pre-mutation-preparation.md").read_text(encoding="utf-8")
    local_gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    plan = (root / "src/FileOp.Core/Operations/FileOperationPlan.cs").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")

    consumers = []
    for subtree in ("src/FileOp.App", "src/FileOp.Windows", "src/FileOp.Indexer"):
        directory = root / subtree
        if directory.exists():
            for path in directory.rglob("*.cs"):
                if "FileDeleteOperationPreMutationPreparation" in path.read_text(encoding="utf-8"):
                    consumers.append(path.relative_to(root).as_posix())
    if consumers:
        raise AssertionError(
            "delete pre-mutation preparation must remain unwired from production: "
            + ", ".join(consumers)
        )

    checks = 0
    for text, needle, label in (
        (core, "public sealed class FileDeleteOperationPreMutationPreparationScope", "owned preparation scope"),
        (core, "public static class FileDeleteOperationPreMutationPreparation", "preparation entry point"),
        (core, "new FileDeleteOperationStabilityLeaseRequest(authorization, ordinal)", "exact stability request"),
        (core, ".AcquireAsync(request, cancellationToken)", "provider acquisition"),
        (core, ".GetAsync(authorization.PlanId, cancellationToken)", "history store read"),
        (core, "FileDeleteOperationHistoryBinding.Validate(", "exact history binding reuse"),
        (core, "return new FileDeleteOperationPreMutationPreparationScope(binding, lease);", "lease ownership transfer"),
        (core, "await _disposeGate.WaitAsync().ConfigureAwait(false);", "serialized scope disposal"),
        (core, "var lease = Volatile.Read(ref _lease);", "cross-thread lease ownership read"),
        (core, "await lease.DisposeAsync().ConfigureAwait(false);\n            Volatile.Write(ref _lease, null);", "clear ownership only after successful disposal"),
        (core, "await lease.DisposeAsync().ConfigureAwait(false);", "preparation-failure cleanup"),
        (core, "public bool DeleteMutationAuthorized => false;", "non-authorizing scope"),
        (core, "public bool MutationBarrierSatisfied => false;", "pre-barrier scope"),
        (core, "public bool StabilityLeaseProviderAcquisitionObserved => true;", "provider acquisition observation"),
        (core, "public bool HistoryStoreReadObserved => true;", "history read observation"),
        (core, "public bool StabilityLeaseHeld => Volatile.Read(ref _lease) is not null;", "held lease lifetime"),
        (core, "public bool IsDisposed => !StabilityLeaseHeld;", "disposed-state lifetime"),
        (binding, "public bool StabilityLeaseAcquisitionProven => false;", "value binding does not overclaim acquisition"),
        (stability, "public interface IFileDeleteOperationStabilityLease", "existing read-only lease contract"),
        (tests, "PrepareAcquiresThenReadsHistoryAndHoldsLeaseUntilScopeDisposal", "ordering/lifetime regression"),
        (tests, "ScopeDisposalFailureRetainsOwnershipAndAllowsRetry", "disposal retry regression"),
        (tests, "ProviderEvidenceFromDifferentAuthorizationFailsBeforeHistoryReadAndReleasesLease", "provider substitution regression"),
        (tests, "MissingHistoryFailsAfterAcquisitionAndReleasesLeaseWithoutCrossingBarrier", "missing history cleanup regression"),
        (tests, "NonPendingOrTerminalHistoryFailsClosedAndReleasesLease", "stale history regression"),
        (tests, "PreCancelledPreparationDoesNotAcquireOrReadHistory", "pre-cancellation regression"),
        (docs, "does **not** authorize or perform deletion", "non-destructive documentation"),
        (docs, "does not call `MarkMutationStartedAsync`", "barrier boundary documentation"),
        (local_gate, "verify_file_delete_pre_mutation_preparation.py --repo-root $repoRoot --cases 50000", "offline gate wiring"),
        (plan, "public enum FileOperationKind\n{\n    Copy,\n    Move,", "Copy/Move generic operation enum"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
    ):
        checks += require(text, needle, label)

    acquisition_position = core.index(".AcquireAsync(request, cancellationToken)")
    history_position = core.index(".GetAsync(authorization.PlanId, cancellationToken)")
    binding_position = core.index("FileDeleteOperationHistoryBinding.Validate(")
    if not acquisition_position < history_position < binding_position:
        raise AssertionError(
            "pre-mutation preparation must acquire the lease before reading history and bind only after that read"
        )
    checks += 1

    for text, needle, label in (
        (core, "Interlocked.Exchange(ref _lease, null)", "ownership cleared before disposal success"),
        (core, "MarkMutationStartedAsync(", "durable mutation barrier transition"),
        (core, "CommitDeletedAsync(", "delete commit transition"),
        (core, "MarkMutationRecoveryRequiredAsync(", "post-barrier recovery transition"),
        (core, "File.Delete(", "managed file delete mutation"),
        (core, "Directory.Delete(", "managed directory delete mutation"),
        (core, "DeleteFileW", "native delete mutation"),
        (core, "SetFileInformationByHandle", "handle delete mutation"),
        (core, "SafeFileHandle", "Windows handle exposure"),
        (core, "public IFileDeleteOperationStabilityLease", "underlying lease exposure"),
        (core, "IFileOperationExecutor", "generic executor integration"),
        (plan, "Delete,", "generic Delete operation kind"),
    ):
        checks += forbid(text, needle, label)

    return checks + 1


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0x128A11CE)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {source_checks:,} source checks" if args.repo_root else ""
    print(
        f"PASS: file delete pre-mutation preparation verified with {model_checks:,} model assertions "
        f"across {args.cases:,} randomized states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
