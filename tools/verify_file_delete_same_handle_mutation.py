#!/usr/bin/env python3
"""Verify the same-handle file-delete mutation and durable settlement boundary."""
from __future__ import annotations

import argparse
import random
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class MutationState:
    pre_cancelled: bool
    barrier_exact: bool
    mutation_facet_present: bool
    mutation_succeeds: bool
    release_succeeds: bool
    commit_persists: bool
    commit_throws: bool
    inspection_succeeds: bool
    recovery_succeeds: bool
    recovery_release_succeeds: bool


@dataclass(frozen=True)
class MutationResult:
    outcome: str
    events: tuple[str, ...]
    mutation_count: int
    committed: bool
    recovery_required: bool
    cleanup_owner: bool
    barrier_still_owns_lease: bool


def execute(state: MutationState) -> MutationResult:
    events: list[str] = []
    if state.pre_cancelled:
        return MutationResult("cancelled-pre-transfer", (), 0, False, False, False, True)
    if not state.barrier_exact:
        return MutationResult("invalid-barrier", (), 0, False, False, False, True)
    if not state.mutation_facet_present:
        events.append("reject-before-transfer")
        return MutationResult("missing-mutation-facet", tuple(events), 0, False, False, False, True)

    events.append("detach")
    mutation_count = 1
    events.append("same-handle-disposition")
    if not state.mutation_succeeds:
        events.append("mark-recovery")
        recovery = state.recovery_succeeds
        events.append("release")
        if not state.recovery_release_succeeds:
            events.append("retain-cleanup-owner")
            return MutationResult(
                "mutation-failed-release-pending",
                tuple(events), mutation_count, False, recovery, True, False,
            )
        return MutationResult(
            "mutation-failed", tuple(events), mutation_count, False, recovery, False, False,
        )

    events.append("release")
    if not state.release_succeeds:
        events.append("mark-recovery")
        recovery = state.recovery_succeeds
        events.append("retain-cleanup-owner")
        return MutationResult(
            "release-pending", tuple(events), mutation_count, False, recovery, True, False,
        )

    events.append("commit")
    committed = state.commit_persists
    if not state.commit_throws:
        if committed:
            return MutationResult("committed", tuple(events), mutation_count, True, False, False, False)
        events.append("inspect")
    else:
        events.append("inspect")

    if not state.inspection_succeeds:
        return MutationResult(
            "commit-inspection-ambiguous", tuple(events), mutation_count, committed, False, False, False,
        )
    if committed:
        events.append("observe-committed")
        return MutationResult(
            "committed-after-inspection", tuple(events), mutation_count, True, False, False, False,
        )

    events.extend(("observe-mutation-started", "mark-recovery"))
    return MutationResult(
        "commit-not-proven",
        tuple(events), mutation_count, False, state.recovery_succeeds, False, False,
    )


def run_model(cases: int, seed: int) -> int:
    baseline = MutationState(
        False, True, True, True, True, True, False, True, True, True,
    )
    result = execute(baseline)
    assert result.outcome == "committed"
    assert result.events == ("detach", "same-handle-disposition", "release", "commit")
    assert result.mutation_count == 1 and result.committed
    assert not result.recovery_required and not result.cleanup_owner
    checks = 5

    rng = random.Random(seed)
    for _ in range(cases):
        state = MutationState(
            pre_cancelled=rng.random() < 0.05,
            barrier_exact=rng.random() >= 0.05,
            mutation_facet_present=rng.random() >= 0.04,
            mutation_succeeds=rng.random() >= 0.08,
            release_succeeds=rng.random() >= 0.05,
            commit_persists=rng.random() >= 0.12,
            commit_throws=rng.random() < 0.12,
            inspection_succeeds=rng.random() >= 0.05,
            recovery_succeeds=rng.random() >= 0.05,
            recovery_release_succeeds=rng.random() >= 0.05,
        )
        result = execute(state)
        events = result.events

        assert result.mutation_count in (0, 1)
        assert events.count("same-handle-disposition") <= 1
        assert events.count("commit") <= 1
        assert events.count("detach") <= 1
        assert "commit" not in events or "release" in events
        checks += 5

        if state.pre_cancelled or not state.barrier_exact:
            assert result.mutation_count == 0
            assert result.barrier_still_owns_lease
            assert not result.committed
            checks += 3
            continue
        if not state.mutation_facet_present:
            assert result.mutation_count == 0
            assert result.barrier_still_owns_lease
            assert events == ("reject-before-transfer",)
            checks += 3
            continue

        assert events[0] == "detach"
        assert result.mutation_count == 1
        assert not result.barrier_still_owns_lease
        checks += 3

        if result.outcome.startswith("committed"):
            assert state.mutation_succeeds and state.release_succeeds
            assert result.committed
            assert not result.cleanup_owner
            assert events.index("same-handle-disposition") < events.index("release") < events.index("commit")
            checks += 4
        else:
            assert not result.committed or result.outcome == "commit-inspection-ambiguous"
            checks += 1

        if result.cleanup_owner:
            assert "retain-cleanup-owner" in events
            assert not result.committed
            checks += 2
        if "mark-recovery" in events:
            assert "detach" in events
            assert not result.committed
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


def normalize_markdown_emphasis(text: str) -> str:
    """Remove presentation-only emphasis markers before prose assertions."""
    return text.replace("**", "").replace("__", "")


def check_repository(root: Path) -> int:
    commit = (root / "src/FileOp.Core/Operations/FileDeleteOperationMutationCommit.cs").read_text(encoding="utf-8")
    barrier = (root / "src/FileOp.Core/Operations/FileDeleteOperationMutationBarrier.cs").read_text(encoding="utf-8")
    provider = (root / "src/FileOp.Windows/Operations/WindowsFileDeleteOperationFinalMutationLeaseProvider.cs").read_text(encoding="utf-8")
    lifecycle_tests = (root / "tests/FileOp.Windows.Tests/FileDeleteOperationMutationCommitTests.cs").read_text(encoding="utf-8")
    native_tests = (root / "tests/FileOp.Windows.Tests/WindowsFileDeleteOperationSameHandleMutationTests.cs").read_text(encoding="utf-8")
    docs = (root / "docs/file-delete-same-handle-mutation.md").read_text(encoding="utf-8")
    docs_prose = normalize_markdown_emphasis(docs)
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    plan = (root / "src/FileOp.Core/Operations/FileOperationPlan.cs").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")

    checks = 0
    required = (
        (commit, "internal FileDeleteOperationMutationAuthorization(", "Core-only mutation authorization constructor"),
        (commit, "public interface IFileDeleteOperationSameLeaseMutation", "same-lease mutation facet"),
        (commit, "DetachLeaseForMutationAsync(cancellationToken)", "destructive ownership transfer"),
        (commit, ".MarkDeletePendingAsync(authorization, CancellationToken.None)", "post-transfer non-cancellable mutation"),
        (commit, ".CommitDeletedAsync(", "durable delete settlement"),
        (commit, "barrierScope.FinalEvidence.SourceIdentity", "identity-bound durable commit"),
        (commit, ".GetAsync(barrierScope.BarrierHistory.OperationId, CancellationToken.None)", "commit-outcome inspection"),
        (commit, "DeleteMutationCommitOutcomeAmbiguous", "ambiguous commit recovery code"),
        (commit, "FileDeleteOperationFinalLeaseReleaseException", "cleanup ownership retention"),
        (barrier, "await _disposeGate.WaitAsync(cancellationToken)", "serialized cancellable pre-transfer gate"),
        (barrier, "var authorization = new FileDeleteOperationMutationAuthorization(this);", "authorization minted while barrier is live"),
        (barrier, "Volatile.Write(ref _lease, null);", "retained barrier alias made inert"),
        (provider, "IFileDeleteOperationSameLeaseMutation", "Windows destructive lease facet"),
        (provider, "private const uint FileDispositionDelete = 0x00000001;", "DELETE disposition flag"),
        (provider, "private const uint FileDispositionPosixSemantics = 0x00000002;", "POSIX disposition flag"),
        (provider, "private const uint FileDispositionForceImageSectionCheck = 0x00000004;", "image-section safety flag"),
        (provider, "private enum FileInformationClass", "typed information-class enum"),
        (provider, "FileDispositionInformationEx = 64,", "FileDispositionInformationEx class"),
        (provider, "FileDispositionDelete |\n                    FileDispositionPosixSemantics |\n                    FileDispositionForceImageSectionCheck", "exact conservative disposition flags"),
        (provider, "FileInformationClass.FileDispositionInformationEx", "typed disposition information class use"),
        (provider, "Interlocked.CompareExchange(ref _mutationAttempted, 1, 0)", "one-shot mutation attempt"),
        (provider, "authorization.IsBoundTo(Evidence)", "exact Core authorization binding"),
        (lifecycle_tests, "MutationFailureMarksRecoveryReleasesAndNeverCommits", "mutation failure lifecycle test"),
        (lifecycle_tests, "ReleaseFailureAfterMutationRetainsCleanupOwnershipAndNeverCommits", "release failure lifecycle test"),
        (lifecycle_tests, "CommitThrowAfterPersistenceIsInspectedAndAcceptedWithoutReplay", "post-persist commit inspection test"),
        (lifecycle_tests, "CommitThrowBeforePersistenceMarksRecoveryWithoutMutationReplay", "pre-persist commit recovery test"),
        (native_tests, "SameHandleMutationRemovesExactNamespaceAndCommitsIdentity", "native same-handle deletion test"),
        (native_tests, "PosixDispositionRemovesNamespaceWhileDeleteSharedReaderRemainsUsable", "POSIX held-reader test"),
        (native_tests, "ProtectedLocationPolicyIsRecheckedImmediatelyBeforeMutation", "policy recheck test"),
        (docs, "FILE_DISPOSITION_DELETE | FILE_DISPOSITION_POSIX_SEMANTICS | FILE_DISPOSITION_FORCE_IMAGE_SECTION_CHECK", "documented exact native flags"),
        (docs_prose, "does not reopen the pathname", "documented same-handle boundary"),
        (gate, "verify_file_delete_same_handle_mutation.py --repo-root $repoRoot --cases 50000", "offline gate wiring"),
        (plan, "public enum FileOperationKind\n{\n    Copy,\n    Move,", "generic Delete remains absent"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 unchanged"),
    )
    for text, needle, label in required:
        checks += require(text, needle, label)

    mutation_start = provider.index("public ValueTask MarkDeletePendingAsync(")
    mutation_end = provider.index("public ValueTask DisposeAsync()", mutation_start)
    mutation_body = provider[mutation_start:mutation_end]
    for needle, label in (
        ("OpenRelativeFile(", "path reopen helper"),
        ("CreateFileW(", "path-based root reopen"),
        ("NtCreateFile(", "native path reopen"),
        ("DeleteFileW", "path delete API"),
        ("File.Delete(", "managed path delete API"),
        ("Directory.Delete(", "managed directory delete API"),
    ):
        checks += forbid(mutation_body, needle, label)

    for needle, label in (
        ("private const int FileDispositionInformationEx", "colliding disposition class constant"),
        ("FileDispositionIgnoreReadonly", "read-only attribute bypass"),
        ("FileDispositionOnClose", "disposition-on-close flag"),
        ("FILE_DISPOSITION_IGNORE_READONLY_ATTRIBUTE", "read-only bypass constant"),
        ("FILE_DISPOSITION_ON_CLOSE", "disposition-on-close constant"),
        ("DeleteFileW", "path delete API in production provider"),
        ("File.Delete(", "managed file delete in production provider"),
        ("Directory.Delete(", "managed directory delete in production provider"),
    ):
        checks += forbid(provider, needle, label)

    if provider.count("NtSetInformationFile(") != 2:
        raise AssertionError("reviewed provider must contain one NtSetInformationFile call plus one declaration")
    checks += 1
    if provider.count("FileDispositionForceImageSectionCheck") != 2:
        raise AssertionError("FORCE_IMAGE_SECTION_CHECK must appear exactly once as a constant and once in flags")
    checks += 1
    if gate.count("verify_file_delete_same_handle_mutation.py") != 1:
        raise AssertionError("same-handle mutation verifier must be wired exactly once")
    checks += 1

    consumers: list[str] = []
    for subtree in ("src/FileOp.App", "src/FileOp.Indexer"):
        directory = root / subtree
        if not directory.exists():
            continue
        for path in directory.rglob("*.cs"):
            text = path.read_text(encoding="utf-8")
            if "FileDeleteOperationMutationCommit" in text or "IFileDeleteOperationSameLeaseMutation" in text:
                consumers.append(path.relative_to(root).as_posix())
    if consumers:
        raise AssertionError("same-handle mutation must remain unwired from App/Indexer: " + ", ".join(consumers))
    checks += 1
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0xD31E7E5)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {source_checks:,} source/test checks" if args.repo_root else ""
    print(
        f"PASS: same-handle delete mutation verified with {model_checks:,} model assertions "
        f"across {args.cases:,} randomized mutation/settlement states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
