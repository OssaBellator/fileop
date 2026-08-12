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
    read_only_held = state.read_only_scope_live
    final_held = False
    barrier = False
    mutation = False

    if state.pre_cancelled:
        return False, tuple(events), read_only_held, final_held, barrier, mutation
    if not read_only_held:
        return False, tuple(events), False, final_held, barrier, mutation

    events.append("release-read-only")
    if not state.read_only_release_succeeds:
        return False, tuple(events), True, final_held, barrier, mutation
    read_only_held = False

    if state.cancelled_after_release:
        return False, tuple(events), read_only_held, final_held, barrier, mutation

    events.append("acquire-final")
    if not state.provider_returns_lease:
        return False, tuple(events), read_only_held, final_held, barrier, mutation
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
        return False, tuple(events), read_only_held, False, barrier, mutation

    events.append("accept-final")
    return True, tuple(events), read_only_held, final_held, barrier, mutation


def run_model(cases: int, seed: int) -> int:
    checks = 0
    baseline = FinalLeaseState(
        False, True, True, False, True, False, True,
        True, True, True, True, True, True, True,
    )
    created, events, read_only_held, final_held, barrier, mutation = simulate(baseline)
    assert created
    assert events == ("release-read-only", "acquire-final", "accept-final")
    assert not read_only_held and final_held
    assert not barrier and not mutation
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

        assert not barrier and not mutation
        assert events.count("release-read-only") <= 1
        assert events.count("acquire-final") <= 1
        assert events.count("accept-final") <= 1
        assert events.count("dispose-final") <= 1
        checks += 5

        if state.pre_cancelled:
            assert not created and events == ()
            assert read_only_held == state.read_only_scope_live
            assert not final_held
            checks += 3
            continue
        if not state.read_only_scope_live:
            assert not created and events == () and not read_only_held and not final_held
            checks += 4
            continue

        assert events[0] == "release-read-only"
        checks += 1
        if not state.read_only_release_succeeds:
            assert not created and read_only_held and not final_held
            assert "acquire-final" not in events
            checks += 4
            continue

        assert not read_only_held
        checks += 1
        if state.cancelled_after_release:
            assert not created and events == ("release-read-only",) and not final_held
            checks += 3
            continue

        assert events[:2] == ("release-read-only", "acquire-final")
        checks += 1
        if not state.provider_returns_lease:
            assert not created and not final_held
            checks += 2
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
            assert not created and events[-1] == "dispose-final" and not final_held
            checks += 3
            continue

        assert created and events[-1] == "accept-final" and final_held
        checks += 3
        # Value evidence never becomes provider/capability/liveness proof or mutation authority.
        assert not barrier and not mutation
        checks += 2

        dispose_attempts = 1
        if not state.final_disposal_succeeds:
            assert final_held
            dispose_attempts += 1
            checks += 1
        final_held = False
        assert not final_held and dispose_attempts in (1, 2)
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

    reviewed_provider = "src/FileOp.Windows/Operations/WindowsFileDeleteOperationFinalMutationLeaseProvider.cs"
    forbidden_consumers: list[str] = []
    provider_implementations: list[str] = []
    for subtree in ("src/FileOp.App", "src/FileOp.Windows", "src/FileOp.Indexer"):
        directory = root / subtree
        if not directory.exists():
            continue
        for path in directory.rglob("*.cs"):
            text = path.read_text(encoding="utf-8")
            relative = path.relative_to(root).as_posix()
            if "FileDeleteOperationFinalMutationLeasePreparation" in text:
                forbidden_consumers.append(relative)
            if "IFileDeleteOperationFinalMutationLeaseProvider" in text:
                provider_implementations.append(relative)
                if relative != reviewed_provider:
                    forbidden_consumers.append(relative)
    if forbidden_consumers:
        raise AssertionError(
            "final delete lease may have only the reviewed native provider and no production coordinator consumer: "
            + ", ".join(sorted(set(forbidden_consumers)))
        )
    if provider_implementations not in ([], [reviewed_provider]):
        raise AssertionError(
            "unexpected production final-lease provider set: " + repr(provider_implementations)
        )

    checks = 0
    for text, needle, label in (
        (core, "public sealed class FileDeleteOperationFinalMutationLeaseRequest", "final request type"),
        (core, "internal FileDeleteOperationFinalMutationLeaseRequest(", "coordinator-only request constructor"),
        (core, "public bool PriorReadOnlyLeaseReleaseProven => false;", "request cannot forge release proof"),
        (core, "private readonly FileDeleteOperationFinalMutationLeaseRequest _request;", "evidence keeps request private"),
        (core, "public bool ProviderAcquisitionProven => false;", "value evidence no provider proof"),
        (core, "public bool DeleteAccessCapabilityProven => false;", "value evidence no capability proof"),
        (core, "public bool LeaseLivenessProven => false;", "value evidence no liveness proof"),
        (core, "ReferenceEquals(Authorization, authorization) && Ordinal == ordinal", "exact receipt/ordinal binding"),
        (core, "public interface IFileDeleteOperationFinalMutationLease : IAsyncDisposable", "live final lease interface"),
        (core, "bool DeleteAccessCapabilityHeld { get; }", "live capability property"),
        (core, "public interface IFileDeleteOperationFinalMutationLeaseProvider", "provider interface"),
        (core, "public sealed class FileDeleteOperationFinalMutationLeaseScope : IAsyncDisposable", "owned final scope"),
        (core, "public bool PriorReadOnlyLeaseReleaseObserved => true;", "release observation"),
        (core, "public bool FinalLeaseProviderAcquisitionObserved => true;", "provider acquisition observation"),
        (core, "public bool DeleteMutationAuthorized => false;", "pre-barrier contract remains non-authorizing"),
        (core, "public bool MutationBarrierSatisfied => false;", "contract remains pre-barrier"),
        (core, "public bool DeleteMutationPerformed => false;", "contract remains non-mutating"),
        (core, "public bool FinalLeaseHeld => Volatile.Read(ref _lease) is not null;", "owned final lease lifetime"),
        (core, "await lease.DisposeAsync().ConfigureAwait(false);\n            Volatile.Write(ref _lease, null);", "ownership clears after disposal"),
        (core, "await readOnlyPreparationScope.DisposeAsync().ConfigureAwait(false);", "read-only release"),
        (core, "new FileDeleteOperationFinalMutationLeaseRequest(authorization, ordinal)", "Core-minted request"),
        (core, ".AcquireAsync(request, cancellationToken)", "provider acquisition"),
        (preparation, "public bool StabilityLeaseHeld => Volatile.Read(ref _lease) is not null;", "read-only held state"),
        (tests, "FinalProviderRunsOnlyAfterReadOnlyPreparationLeaseIsReleased", "release-before-acquire regression"),
        (tests, "ReadOnlyDisposalFailurePreventsFinalProviderAcquisition", "release-failure regression"),
        (tests, "PreCancellationLeavesReadOnlyPreparationHeldAndDoesNotInvokeFinalProvider", "pre-cancellation regression"),
        (tests, "ReplayedFinalLeaseFromDifferentAuthorizationFailsAndIsReleased", "stale lease regression"),
        (tests, "FinalProviderMustHoldCapabilityWithoutClaimingMutationAuthorization", "capability/authority regression"),
        (tests, "FinalScopeDisposalFailureRetainsCapabilityOwnershipAndAllowsRetry", "disposal retry regression"),
        (tests, "ValueEvidenceCannotExposeReusableRequestOrClaimCapabilityProof", "value-only evidence regression"),
        (docs, "constructor is **internal to FileOp.Core**", "coordinator-only request documentation"),
        (docs, "Releasing the earlier read-only lease necessarily creates a handoff interval", "handoff gap disclosure"),
        (docs, "Reopening the pathname after final validation", "no path fallback documentation"),
        (local_gate, "verify_file_delete_final_mutation_lease_contract.py --repo-root $repoRoot --cases 50000", "offline gate wiring"),
        (plan, "public enum FileOperationKind\n{\n    Copy,\n    Move,", "generic operation enum unchanged"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
    ):
        checks += require(text, needle, label)

    release_pos = core.index("await readOnlyPreparationScope.DisposeAsync().ConfigureAwait(false);")
    final_request_pos = core.index("new FileDeleteOperationFinalMutationLeaseRequest(authorization, ordinal)")
    acquire_pos = core.index(".AcquireAsync(request, cancellationToken)")
    if not release_pos < final_request_pos < acquire_pos:
        raise AssertionError("final delete lease ordering must remain read-only release -> Core request -> final acquire")
    checks += 1

    for text, needle, label in (
        (core, "public FileDeleteOperationFinalMutationLeaseRequest(", "public request constructor"),
        (core, "public FileDeleteOperationFinalMutationLeaseRequest Request", "request exposure from evidence"),
        (core, "MarkMutationStartedAsync(", "barrier call in pre-barrier contract"),
        (core, "CommitDeletedAsync(", "delete settlement in pre-barrier contract"),
        (core, "SetFileInformationByHandle", "delete implementation in Core"),
        (core, "NtSetInformationFile", "native mutation in Core"),
        (core, "DeleteFileW", "path delete in Core"),
        (core, "File.Delete(", "managed delete in Core"),
        (core, "Directory.Delete(", "managed directory delete in Core"),
        (core, "SafeFileHandle", "raw handle exposure from Core"),
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
