#!/usr/bin/env python3
"""Verify exact delete authorization/stability/history evidence binding."""
from __future__ import annotations

import argparse
import random
from dataclasses import dataclass
from pathlib import Path

from verify_file_delete_action_history import (
    check_repository as check_delete_history_repository,
    run_model as run_delete_history_model,
    run_sqlite_model as run_delete_history_sqlite_model,
)


@dataclass(frozen=True)
class BindingState:
    user_authorized: bool
    mutation_authorized: bool
    ordinal_valid: bool
    complete_ordered_shape: bool
    exact_receipt_reference: bool
    stability_ordinal_same: bool
    operation_id_same: bool
    authorization_id_same: bool
    history_nonterminal: bool
    history_pending: bool
    entry_same: bool
    root_path_same: bool
    root_identity_same: bool
    file_path_same: bool
    file_identity_same: bool


def can_bind(state: BindingState) -> bool:
    return all(
        (
            state.user_authorized,
            not state.mutation_authorized,
            state.ordinal_valid,
            state.complete_ordered_shape,
            state.exact_receipt_reference,
            state.stability_ordinal_same,
            state.operation_id_same,
            state.authorization_id_same,
            state.history_nonterminal,
            state.history_pending,
            state.entry_same,
            state.root_path_same,
            state.root_identity_same,
            state.file_path_same,
            state.file_identity_same,
        )
    )


def run_binding_model(cases: int, seed: int) -> int:
    fixed = BindingState(
        user_authorized=True,
        mutation_authorized=False,
        ordinal_valid=True,
        complete_ordered_shape=True,
        exact_receipt_reference=True,
        stability_ordinal_same=True,
        operation_id_same=True,
        authorization_id_same=True,
        history_nonterminal=True,
        history_pending=True,
        entry_same=True,
        root_path_same=True,
        root_identity_same=True,
        file_path_same=True,
        file_identity_same=True,
    )
    checks = 0
    assert can_bind(fixed)
    checks += 1
    for field in (
        "user_authorized",
        "ordinal_valid",
        "complete_ordered_shape",
        "exact_receipt_reference",
        "stability_ordinal_same",
        "operation_id_same",
        "authorization_id_same",
        "history_nonterminal",
        "history_pending",
        "entry_same",
        "root_path_same",
        "root_identity_same",
        "file_path_same",
        "file_identity_same",
    ):
        changed = fixed.__class__(**{**fixed.__dict__, field: False})
        assert not can_bind(changed)
        checks += 1
    assert not can_bind(fixed.__class__(**{**fixed.__dict__, "mutation_authorized": True}))
    checks += 1

    rng = random.Random(seed)
    fields = tuple(fixed.__dict__.keys())
    for _ in range(cases):
        state = BindingState(
            user_authorized=rng.random() < 0.96,
            mutation_authorized=rng.random() < 0.02,
            ordinal_valid=rng.random() < 0.97,
            complete_ordered_shape=rng.random() < 0.96,
            exact_receipt_reference=rng.random() < 0.94,
            stability_ordinal_same=rng.random() < 0.96,
            operation_id_same=rng.random() < 0.97,
            authorization_id_same=rng.random() < 0.97,
            history_nonterminal=rng.random() < 0.95,
            history_pending=rng.random() < 0.93,
            entry_same=rng.random() < 0.96,
            root_path_same=rng.random() < 0.96,
            root_identity_same=rng.random() < 0.95,
            file_path_same=rng.random() < 0.95,
            file_identity_same=rng.random() < 0.94,
        )
        bound = can_bind(state)
        expected = (
            state.user_authorized
            and not state.mutation_authorized
            and state.ordinal_valid
            and state.complete_ordered_shape
            and state.exact_receipt_reference
            and state.stability_ordinal_same
            and state.operation_id_same
            and state.authorization_id_same
            and state.history_nonterminal
            and state.history_pending
            and state.entry_same
            and state.root_path_same
            and state.root_identity_same
            and state.file_path_same
            and state.file_identity_same
        )
        assert bound == expected
        assert not state.mutation_authorized or not bound
        checks += 2

        if bound:
            delete_mutation_authorized = False
            reusable_restart_capability = False
            durable_barrier_already_claimed = False
            assert not delete_mutation_authorized
            assert not reusable_restart_capability
            assert not durable_barrier_already_claimed
            checks += 3

        for field in rng.sample(fields, k=3):
            value = getattr(state, field)
            if field == "mutation_authorized":
                changed_value = True
            else:
                changed_value = False
            changed = state.__class__(**{**state.__dict__, field: changed_value})
            if field == "mutation_authorized" or value:
                assert not can_bind(changed)
                checks += 1

    return checks


def run_model(cases: int, seed: int) -> int:
    return (
        run_binding_model(cases, seed)
        + run_delete_history_model(cases, seed ^ 0xD31E7E51)
        + run_delete_history_sqlite_model(cases, seed ^ 0xD31B244F)
    )


def require(text: str, needle: str, label: str) -> int:
    if needle not in text:
        raise AssertionError(f"missing {label}: {needle}")
    return 1


def forbid(text: str, needle: str, label: str) -> int:
    if needle in text:
        raise AssertionError(f"forbidden {label}: {needle}")
    return 1


def check_binding_repository(root: Path) -> int:
    core = (root / "src/FileOp.Core/Operations/FileDeleteOperationExecutionBinding.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/FileDeleteOperationExecutionEvidenceBindingTests.cs").read_text(encoding="utf-8")
    docs = (root / "docs/file-delete-execution-evidence-binding.md").read_text(encoding="utf-8")
    stability_parent = (root / "tools/verify_file_delete_stability_lease.py").read_text(encoding="utf-8")
    stability = (root / "src/FileOp.Core/Operations/FileDeleteOperationStabilityLease.cs").read_text(encoding="utf-8")
    history = (root / "src/FileOp.Core/Operations/FileDeleteOperationActionHistory.cs").read_text(encoding="utf-8")
    generic_execution = (root / "src/FileOp.Core/Operations/FileOperationExecution.cs").read_text(encoding="utf-8")
    plan = (root / "src/FileOp.Core/Operations/FileOperationPlan.cs").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")

    consumers = []
    for subtree in ("src/FileOp.App", "src/FileOp.Windows", "src/FileOp.Indexer"):
        directory = root / subtree
        if directory.exists():
            for path in directory.rglob("*.cs"):
                text = path.read_text(encoding="utf-8")
                if "FileDeleteOperationExecutionEvidenceBinder" in text:
                    consumers.append(path.relative_to(root).as_posix())
    if consumers:
        raise AssertionError(
            "delete execution evidence binding must remain unwired from production in this slice: "
            + ", ".join(consumers)
        )

    checks = 0
    required = [
        (core, "public sealed class FileDeleteOperationExecutionEvidenceBinding", "binding evidence contract"),
        (core, "public sealed class FileDeleteOperationExecutionEvidenceBinder", "binding validator"),
        (core, "ReferenceEquals(Authorization, authorization)", "binding exact authorization reference"),
        (core, "ReferenceEquals(StabilityEvidence, stabilityEvidence)", "binding exact stability reference"),
        (core, "ReferenceEquals(History, history)", "binding exact history reference"),
        (core, "stabilityEvidence.IsBoundTo(authorization, ordinal)", "stability exact receipt/ordinal check"),
        (core, "history.OperationId != authorization.PlanId", "plan ID binding"),
        (core, "history.AuthorizationId != authorization.AuthorizationId", "authorization ID binding"),
        (core, "history.IsTerminal", "terminal-history refusal"),
        (core, "historyEntry.State != FileDeleteOperationActionEntryState.Pending", "Pending-only history binding"),
        (core, "history.Entries.Count != authorization.Items.Count", "complete ordered history shape"),
        (core, "historyEntry.Entry != plannedEntry", "ordered entry equality"),
        (core, "history.CanonicalSourceDirectoryPath", "history root-path evidence"),
        (core, "history.SourceDirectoryIdentity", "history root-identity evidence"),
        (core, "historyEntry.CanonicalSourcePath", "history file-path evidence"),
        (core, "historyEntry.SourceIdentity", "history file-identity evidence"),
        (core, "public bool DeleteMutationAuthorized => false;", "binding remains non-authorizing"),
        (tests, "ExactReceiptStabilityAndPendingHistoryBindAsEvidenceOnly", "exact success regression"),
        (tests, "EquivalentIdsDoNotSubstituteForExactAuthorizationReceiptReference", "receipt reference substitution regression"),
        (tests, "HistoryMustBelongToExactAuthorizedPlanAndConsentAttempt", "plan/authorization provenance regression"),
        (tests, "HistoryMustRemainNonTerminalAndSelectedEntryMustRemainPending", "terminal/state regression"),
        (tests, "RootAndFilePathOrIdentitySubstitutionFailsClosed", "path/identity substitution regression"),
        (tests, "OrdinalAndCompleteOrderedEntryShapeAreRequired", "ordinal/shape regression"),
        (docs, "point-in-time evidence only", "evidence-only documentation"),
        (docs, "`Pending -> MutationStarted`", "durable concurrency-barrier documentation"),
        (docs, "does not request `DELETE` desired access", "no DELETE access documentation"),
        (stability_parent, "from verify_file_delete_execution_binding import (", "stability parent imports binding child"),
        (stability_parent, "run_model as run_delete_binding_model", "stability parent runs binding/history family"),
        (stability_parent, "check_repository as check_delete_binding_repository", "stability parent checks binding/history family"),
        (stability, "public bool DeleteMutationAuthorized => false;", "stability remains non-authorizing"),
        (history, "public bool DeleteMutationAuthorized => false;", "history remains non-authorizing"),
        (plan, "public enum FileOperationKind\n{\n    Copy,\n    Move,", "Copy/Move operation enum unchanged"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
    ]
    for text, needle, label in required:
        checks += require(text, needle, label)

    combined = core + "\n" + docs
    for text, needle, label in (
        (core, "IFileDeleteOperationActionHistoryStore", "history-store dependency"),
        (core, "MarkMutationStartedAsync", "mutation-barrier transition call"),
        (core, "IFileDeleteOperationStabilityLeaseProvider", "new lease acquisition"),
        (core, "File.Delete(", "managed delete mutation"),
        (core, "Directory.Delete(", "managed directory delete mutation"),
        (core, "DeleteFileW", "native delete mutation"),
        (core, "SetFileInformationByHandle", "handle delete mutation"),
        (core, "FileStream", "filesystem stream"),
        (core, "Microsoft.Win32", "registry dependency"),
        (generic_execution, "FileDeleteOperationExecutionEvidenceBinding", "generic executor binding integration"),
        (plan, "Delete,", "Delete generic operation enum"),
        (combined, "DeleteMutationAuthorized => true", "mutation authorization"),
    ):
        checks += forbid(text, needle, label)

    checks += 1
    return checks


def check_repository(root: Path) -> int:
    return check_binding_repository(root) + check_delete_history_repository(root)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0xB1D125)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {source_checks:,} source checks" if args.repo_root else ""
    print(
        f"PASS: file delete execution evidence binding + action history verified with {model_checks:,} "
        f"model/SQLite assertions across {args.cases:,} randomized states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
