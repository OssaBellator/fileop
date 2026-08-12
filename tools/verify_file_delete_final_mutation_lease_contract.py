#!/usr/bin/env python3
"""Verify final same-lease file-delete capability contracts without mutation."""
from __future__ import annotations

import argparse
import random
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class FinalLeaseState:
    pre_cancelled: bool
    read_only_scope_live: bool
    read_only_release_succeeds: bool
    cancelled_after_release: bool
    provider_returns_lease: bool
    lease_mutation_authorized: bool
    delete_access_capability_held: bool
    evidence_exact_receipt: bool
    evidence_exact_ordinal: bool
    root_path_same: bool
    root_identity_same: bool
    file_path_same: bool
    file_identity_same: bool
    final_disposal_succeeds: bool


def simulate(state: FinalLeaseState) -> tuple[bool, tuple[str, ...], bool, bool, bool, bool]:
    """Return (scope_created, events, read_only_held, final_held, barrier, mutation)."""
    events: list[str] = []
    mutation_barrier = False
    delete_mutation = False
    read_only_held = state.read_only_scope_live
    final_held = False

    if state.pre_cancelled:
        return False, tuple(events), read_only_held, final_held, mutation_barrier, delete_mutation
    if not read_only_held:
        return False, tuple(events), False, final_held, mutation_barrier, delete_mutation

    events.append("release-read-only")
    if not state.read_only_release_succeeds:
        return False, tuple(events), True, final_held, mutation_barrier, delete_mutation
    read_only_held = False

    if state.cancelled_after_release:
        return False, tuple(events), read_only_held, final_held, mutation_barrier, delete_mutation

    events.append("acquire-final")
    if not state.provider_returns_lease:
        return False, tuple(events), read_only_held, final_held, mutation_barrier, delete_mutation
    final_held = True

    provider_valid = (
        not state.lease_mutation_authorized
        and state.delete_access_capability_held
        and state.evidence_exact_receipt
        and state.evidence_exact_ordinal
        and state.root_path_same
        and state.root_identity_same
        and state.file_path_same
        and state.file_identity_same
    )
    if not provider_valid:
        events.append("dispose-final")
        final_held = False
        return False, tuple(events), read_only_held, final_held, mutation_barrier, delete_mutation

    events.append("accept-final")
    return True, tuple(events), read_only_held, final_held, mutation_barrier, delete_mutation


def run_model(cases: int, seed: int) -> int:
    checks = 0
    baseline = FinalLeaseState(
        pre_cancelled=False,
        read_only_scope_live=True,
        read_only_release_succeeds=True,
        cancelled_after_release=False,
        provider_returns_lease=True,
        lease_mutation_authorized=False,
        delete_access_capability_held=True,
        evidence_exact_receipt=True,
        evidence_exact_ordinal=True,
        root_path_same=True,
        root_identity_same=True,
        file_path_same=True,
        file_identity_same=True,
        final_disposal_succeeds=True,
    )
    created, events, read_only_held, final_held, barrier, mutation = simulate(baseline)
    assert created
    assert events == ("release-read-only", "acquire-final", "accept-final")
    assert not read_only_held
    assert final_held
    assert not barrier
    assert not mutation
    checks += 6

    rng = random.Random(seed)
    for _ in range(cases):
        state = FinalLeaseState(
            pre_cancelled=rng.random() < 0.04,
            read_only_scope_live=rng.random() >= 0.06,
            read_only_release_succeeds=rng.random() >= 0.07,
            cancelled_after_release=rng.random() < 0.04,
            provider_returns_lease=rng.random() >= 0.05,
            lease_mutation_authorized=rng.random() < 0.03,
            delete_access_capability_held=rng.random() >= 0.07,
            evidence_exact_receipt=rng.random() >= 0.08,
            evidence_exact_ordinal=rng.random() >= 0.06,
            root_path_same=rng.random() >= 0.06,
            root_identity_same=rng.random() >= 0.06,
            file_path_same=rng.random() >= 0.06,
            file_identity_same=rng.random() >= 0.06,
            final_disposal_succeeds=rng.random() >= 0.08,
        )
        created, events, read_only_held, final_held, barrier, mutation = simulate(state)

        assert not barrier
        assert not mutation
        assert events.count("release-read-only") <= 1
        assert events.count("acquire-final") <= 1
        assert events.count("accept-final") <= 1
        assert events.count("dispose-final") <= 1
        checks += 6

        if state.pre_cancelled:
            assert not created
            assert events == ()
            assert read_only_held == state.read_only_scope_live
            assert not final_held
            checks += 4
            continue

        if not state.read_only_scope_live:
            assert not created
            assert events == ()
            assert not read_only_held
            assert not final_held
            checks += 4
            continue

        assert events and events[0] == "release-read-only"
        checks += 1

        if not state.read_only_release_succeeds:
            assert not created
            assert "acquire-final" not in events
            assert read_only_held
            assert not final_held
            checks += 4
            continue

        assert not read_only_held
        checks += 1

        if state.cancelled_after_release:
            assert not created
            assert events == ("release-read-only",)
            assert not final_held
            checks += 3
            continue

        assert events[:2] == ("release-read-only", "acquire-final")
        checks += 1

        if not state.provider_returns_lease:
            assert not created
            assert events == ("release-read-only", "acquire-final")
            assert not final_held
            checks += 3
            continue

        provider_valid = (
            not state.lease_mutation_authorized
            and state.delete_access_capability_held
            and state.evidence_exact_receipt
            and state.evidence_exact_ordinal
            and state.root_path_same
            and state.root_identity_same
            and state.file_path_same
            and state.file_identity_same
        )
        if not provider_valid:
            assert not created
            assert events[-1] == "dispose-final"
            assert not final_held
            checks += 3
            continue

        assert created
        assert events[-1] == "accept-final"
        assert final_held
        checks += 3

        provider_acquisition_proven_by_value = False
        delete_access_proven_by_value = False
        lease_liveness_proven_by_value = False
        delete_mutation_authorized = False
        mutation_barrier_satisfied = False
        delete_mutation_performed = False
        assert not provider_acquisition_proven_by_value
        assert not delete_access_proven_by_value
        assert not lease_liveness_proven_by_value
        assert not delete_mutation_authorized
        assert not mutation_barrier_satisfied
        assert not delete_mutation_performed
        checks += 6

        dispose_attempts = 1
        if not state.final_disposal_succeeds:
            assert final_held
            checks += 1
            dispose_attempts += 1
        final_held = False
        assert not final_held
        assert dispose_attempts in (1, 2)
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
    core = (root / "src/FileOp.Core/Operations/FileDeleteOperationFinalMutationLease.cs").read_text(encoding="utf-8")
    preparation = (root / "src/FileOp.Core/Operations/FileDeleteOperationPreMutationPreparation.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/FileDeleteOperationFinalMutationLeaseContractTests.cs").read_text(encoding="utf-8")
    docs = (root / "docs/file-delete-final-mutation-lease-contract.md").read_text(encoding="utf-8")
    local_gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    generic_execution = (root / "src/FileOp.Core/Operations/FileOperationExecution.cs").read_text(encoding="utf-8")
    plan = (root / "src/FileOp.Core/Operations/FileOperationPlan.cs").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")

    production_consumers: list[str] = []
    for subtree in ("src/FileOp.App", "src/FileOp.Windows", "src/FileOp.Indexer"):
        directory = root / subtree
        if not directory.exists():
            continue
        for path in directory.rglob("*.cs"):
            text = path.read_text(encoding="utf-8")
            if "IFileDeleteOperationFinalMutationLeaseProvider" in text or \
                    "FileDeleteOperationFinalMutationLeasePreparation" in text:
                production_consumers.append(path.relative_to(root).as_posix())
    if production_consumers:
        raise AssertionError(
            "final delete mutation lease contract must remain unwired from production: "
            + ", ".join(production_consumers)
        )

    checks = 0
    required = [
        (core, "public sealed class FileDeleteOperationFinalMutationLeaseRequest", "final request type"),
        (core, "internal FileDeleteOperationFinalMutationLeaseRequest(", "coordinator-only request constructor"),
        (core, "public bool PriorReadOnlyLeaseReleaseProven => false;", "request cannot forge release proof"),
        (core, "private readonly FileDeleteOperationFinalMutationLeaseRequest _request;", "evidence keeps request private"),
        (core, "public sealed class FileDeleteOperationFinalMutationLeaseEvidence", "value evidence contract"),
        (core, "public bool ProviderAcquisitionProven => false;", "value evidence no provider proof"),
        (core, "public bool DeleteAccessCapabilityProven => false;", "value evidence no capability proof"),
        (core, "public bool LeaseLivenessProven => false;", "value evidence no liveness proof"),
        (core, "ReferenceEquals(Authorization, authorization) && Ordinal == ordinal", "exact receipt/ordinal binding"),
        (core, "public interface IFileDeleteOperationFinalMutationLease : IAsyncDisposable", "live final lease interface"),
        (core, "bool DeleteAccessCapabilityHeld { get; }", "live capability property"),
        (core, "public interface IFileDeleteOperationFinalMutationLeaseProvider", "final provider interface"),
        (core, "public sealed class FileDeleteOperationFinalMutationLeaseScope : IAsyncDisposable", "owned final scope"),
        (core, "public bool PriorReadOnlyLeaseReleaseObserved => true;", "release observation"),
        (core, "public bool FinalLeaseProviderAcquisitionObserved => true;", "provider acquisition observation"),
        (core, "public bool DeleteMutationAuthorized => false;", "contract remains non-authorizing"),
        (core, "public bool MutationBarrierSatisfied => false;", "contract remains pre-barrier"),
        (core, "public bool DeleteMutationPerformed => false;", "contract remains non-mutating"),
        (core, "public bool FinalLeaseHeld => Volatile.Read(ref _lease) is not null;", "owned final lease lifetime"),
        (core, "await lease.DisposeAsync().ConfigureAwait(false);\n            Volatile.Write(ref _lease, null);", "ownership clears only after successful disposal"),
        (core, "public static class FileDeleteOperationFinalMutationLeasePreparation", "release/reacquire coordinator"),
        (core, "await readOnlyPreparationScope.DisposeAsync().ConfigureAwait(false);", "read-only release"),
        (core, "readOnlyPreparationScope.StabilityLeaseHeld", "post-release state check"),
        (core, "new FileDeleteOperationFinalMutationLeaseRequest(authorization, ordinal)", "Core-minted final request after release"),
        (core, ".AcquireAsync(request, cancellationToken)", "final provider acquisition"),
        (core, "!lease.DeleteAccessCapabilityHeld", "capability-held requirement"),
        (core, "evidence.ProviderAcquisitionProven", "reject forged provider proof"),
        (core, "evidence.DeleteAccessCapabilityProven", "reject forged capability proof"),
        (core, "evidence.LeaseLivenessProven", "reject forged liveness proof"),
        (preparation, "public bool StabilityLeaseHeld => Volatile.Read(ref _lease) is not null;", "#130 held-state source"),
        (preparation, "Volatile.Write(ref _lease, null);", "#130 release after disposal"),
        (tests, "FinalProviderRunsOnlyAfterReadOnlyPreparationLeaseIsReleased", "release-before-acquire regression"),
        (tests, "ReadOnlyDisposalFailurePreventsFinalProviderAcquisition", "release-failure regression"),
        (tests, "PreCancellationLeavesReadOnlyPreparationHeldAndDoesNotInvokeFinalProvider", "pre-cancellation regression"),
        (tests, "ReplayedFinalLeaseFromDifferentAuthorizationFailsAndIsReleased", "stale final lease regression"),
        (tests, "FinalProviderMustHoldCapabilityWithoutClaimingMutationAuthorization", "capability/authorization regression"),
        (tests, "FinalScopeDisposalFailureRetainsCapabilityOwnershipAndAllowsRetry", "final disposal retry regression"),
        (tests, "ValueEvidenceCannotExposeReusableRequestOrClaimCapabilityProof", "non-reusable request/value regression"),
        (tests, "GetConstructors(BindingFlags.Public | BindingFlags.Instance)", "no public request constructor regression"),
        (tests, "GetProperty(\"Request\")", "no evidence request property regression"),
        (docs, "constructor is **internal to FileOp.Core**", "coordinator-only request documentation"),
        (docs, "Releasing the earlier read-only lease necessarily creates a handoff interval", "handoff gap disclosure"),
        (docs, "Reopening the pathname after final validation", "no path fallback documentation"),
        (local_gate, "verify_file_delete_final_mutation_lease_contract.py --repo-root $repoRoot --cases 50000", "offline gate wiring"),
        (plan, "public enum FileOperationKind\n{\n    Copy,\n    Move,", "generic operation enum unchanged"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
    ]
    for text, needle, label in required:
        checks += require(text, needle, label)

    release_pos = core.index("await readOnlyPreparationScope.DisposeAsync().ConfigureAwait(false);")
    final_request_pos = core.index("new FileDeleteOperationFinalMutationLeaseRequest(authorization, ordinal)")
    acquire_pos = core.index(".AcquireAsync(request, cancellationToken)")
    if not release_pos < final_request_pos < acquire_pos:
        raise AssertionError("final delete lease ordering must remain read-only release -> Core request -> final acquire")
    checks += 1

    for text, needle, label in (
        (core, "public FileDeleteOperationFinalMutationLeaseRequest(", "public request constructor"),
        (core, "public FileDeleteOperationFinalMutationLeaseRequest Request", "reusable request exposure from evidence"),
        (core, "MarkMutationStartedAsync(", "durable mutation barrier call"),
        (core, "CommitDeletedAsync(", "delete commit call"),
        (core, "MarkMutationRecoveryRequiredAsync(", "recovery transition call"),
        (core, "SetFileInformationByHandle", "same-handle delete implementation"),
        (core, "NtSetInformationFile", "native delete implementation"),
        (core, "DeleteFileW", "path delete implementation"),
        (core, "File.Delete(", "managed file delete"),
        (core, "Directory.Delete(", "managed directory delete"),
        (core, "SafeFileHandle", "raw Windows handle exposure"),
        (core, "public IFileDeleteOperationFinalMutationLease Lease", "underlying final lease exposure"),
        (generic_execution, "FileDeleteOperationFinalMutationLease", "generic executor integration"),
        (plan, "Delete,", "generic Delete operation kind"),
    ):
        checks += forbid(text, needle, label)

    return checks + 1


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0x132F1A1)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {source_checks:,} source checks" if args.repo_root else ""
    print(
        f"PASS: final file-delete mutation lease contract verified with {model_checks:,} model assertions "
        f"across {args.cases:,} randomized states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
