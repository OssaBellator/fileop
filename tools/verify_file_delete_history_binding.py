#!/usr/bin/env python3
"""Verify non-authorizing binding of delete authorization, stability evidence and durable history."""
from __future__ import annotations

import argparse
import random
from dataclasses import dataclass, replace
from pathlib import Path

PENDING = 0
MUTATION_STARTED = 1
COMMITTED = 2
FAILED = 3
RECOVERY_REQUIRED = 4


@dataclass(frozen=True)
class BindingState:
    exact_receipt_reference: bool = True
    user_authorized: bool = True
    authorization_mutation_authorized: bool = False
    stability_bound: bool = True
    stability_mutation_authorized: bool = False
    history_mutation_authorized: bool = False
    plan_id: int = 1
    history_operation_id: int = 1
    authorization_id: int = 2
    history_authorization_id: int = 2
    ordinal: int = 0
    authorization_count: int = 1
    history_count: int = 1
    history_entry_ordinal: int = 0
    terminal: bool = False
    entry_state: int = PENDING
    source_pane: str = "left"
    history_source_pane: str = "left"
    source_tab: int = 3
    history_source_tab: int = 3
    root_path: str = r"C:\Users\Alice\Temp"
    history_root_path: str = r"C:\Users\Alice\Temp"
    stability_root_path: str = r"C:\Users\Alice\Temp"
    root_identity: tuple[int, int] = (7, 700)
    history_root_identity: tuple[int, int] = (7, 700)
    stability_root_identity: tuple[int, int] = (7, 700)
    file_path: str = r"C:\Users\Alice\Temp\bind.tmp"
    history_file_path: str = r"C:\Users\Alice\Temp\bind.tmp"
    stability_file_path: str = r"C:\Users\Alice\Temp\bind.tmp"
    file_identity: tuple[int, int] = (7, 701)
    history_file_identity: tuple[int, int] = (7, 701)
    stability_file_identity: tuple[int, int] = (7, 701)
    entry_equal: bool = True
    queued_ticks: int = 10
    history_queued_ticks: int = 10
    validated_ticks: int = 20
    history_validated_ticks: int = 20
    authorized_ticks: int = 30
    history_authorized_ticks: int = 30


def path_equal(left: str, right: str) -> bool:
    return left.casefold() == right.casefold()


def can_bind(state: BindingState) -> bool:
    return (
        state.exact_receipt_reference
        and state.user_authorized
        and not state.authorization_mutation_authorized
        and state.stability_bound
        and not state.stability_mutation_authorized
        and not state.history_mutation_authorized
        and state.plan_id == state.history_operation_id
        and state.authorization_id == state.history_authorization_id
        and 0 <= state.ordinal < state.authorization_count
        and state.authorization_count == state.history_count
        and state.history_entry_ordinal == state.ordinal
        and not state.terminal
        and state.entry_state == PENDING
        and state.source_pane == state.history_source_pane
        and state.source_tab == state.history_source_tab
        and path_equal(state.root_path, state.history_root_path)
        and path_equal(state.root_path, state.stability_root_path)
        and state.root_identity == state.history_root_identity == state.stability_root_identity
        and state.entry_equal
        and path_equal(state.file_path, state.history_file_path)
        and path_equal(state.file_path, state.stability_file_path)
        and state.file_identity == state.history_file_identity == state.stability_file_identity
        and state.queued_ticks == state.history_queued_ticks
        and state.validated_ticks == state.history_validated_ticks
        and state.authorized_ticks == state.history_authorized_ticks
    )


def make_invalid(state: BindingState, choice: int, rng: random.Random) -> BindingState:
    if choice == 0:
        return replace(state, exact_receipt_reference=False)
    if choice == 1:
        return replace(state, user_authorized=False)
    if choice == 2:
        return replace(state, authorization_mutation_authorized=True)
    if choice == 3:
        return replace(state, stability_bound=False)
    if choice == 4:
        return replace(state, stability_mutation_authorized=True)
    if choice == 5:
        return replace(state, history_mutation_authorized=True)
    if choice == 6:
        return replace(state, history_operation_id=state.plan_id + 1)
    if choice == 7:
        return replace(state, history_authorization_id=state.authorization_id + 1)
    if choice == 8:
        return replace(state, ordinal=1)
    if choice == 9:
        return replace(state, history_count=2)
    if choice == 10:
        return replace(state, history_entry_ordinal=1)
    if choice == 11:
        return replace(state, terminal=True)
    if choice == 12:
        return replace(
            state,
            entry_state=rng.choice((MUTATION_STARTED, COMMITTED, FAILED, RECOVERY_REQUIRED)),
        )
    if choice == 13:
        return replace(state, history_source_pane="right")
    if choice == 14:
        return replace(state, history_source_tab=state.source_tab + 1)
    if choice == 15:
        return replace(state, history_root_path=state.root_path + ".substituted")
    if choice == 16:
        return replace(state, stability_root_path=state.root_path + ".substituted")
    if choice == 17:
        return replace(state, history_root_identity=(7, 999))
    if choice == 18:
        return replace(state, stability_root_identity=(7, 999))
    if choice == 19:
        return replace(state, entry_equal=False)
    if choice == 20:
        return replace(state, history_file_path=state.file_path + ".substituted")
    if choice == 21:
        return replace(state, stability_file_path=state.file_path + ".substituted")
    if choice == 22:
        return replace(state, history_file_identity=(7, 999))
    if choice == 23:
        return replace(state, stability_file_identity=(7, 999))
    if choice == 24:
        return replace(state, history_queued_ticks=state.queued_ticks + 1)
    if choice == 25:
        return replace(state, history_validated_ticks=state.validated_ticks + 1)
    return replace(state, history_authorized_ticks=state.authorized_ticks + 1)


def run_model(cases: int, seed: int) -> int:
    rng = random.Random(seed)
    checks = 0
    baseline = BindingState()
    assert can_bind(baseline)
    checks += 1

    for _ in range(cases):
        assert can_bind(baseline)
        checks += 1

        invalid = make_invalid(baseline, rng.randrange(27), rng)
        assert not can_bind(invalid)
        checks += 1

        case_alias = replace(
            baseline,
            history_root_path=baseline.root_path.lower(),
            stability_root_path=baseline.root_path.upper(),
            history_file_path=baseline.file_path.lower(),
            stability_file_path=baseline.file_path.upper(),
        )
        assert can_bind(case_alias)
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
    core = (root / "src/FileOp.Core/Operations/FileDeleteOperationHistoryBinding.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/FileDeleteOperationHistoryBindingTests.cs").read_text(encoding="utf-8")
    plan = (root / "src/FileOp.Core/Operations/FileOperationPlan.cs").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")

    consumers = []
    for subtree in ("src/FileOp.App", "src/FileOp.Windows", "src/FileOp.Indexer"):
        directory = root / subtree
        if directory.exists():
            for path in directory.rglob("*.cs"):
                if "FileDeleteOperationHistoryBinding" in path.read_text(encoding="utf-8"):
                    consumers.append(path.relative_to(root).as_posix())
    if consumers:
        raise AssertionError("delete history binding must remain unwired from production: " + ", ".join(consumers))

    checks = 0
    for text, needle, label in (
        (core, "public sealed class FileDeleteOperationHistoryBindingEvidence", "binding evidence type"),
        (core, "public static class FileDeleteOperationHistoryBinding", "binding validator"),
        (core, "stabilityEvidence.IsBoundTo(authorization, ordinal)", "exact receipt/ordinal stability binding"),
        (core, "historySnapshot.OperationId != authorization.PlanId", "operation binding"),
        (core, "historySnapshot.AuthorizationId != authorization.AuthorizationId", "authorization binding"),
        (core, "historySnapshot.IsTerminal", "terminal-history refusal"),
        (core, "historyEntry.State != FileDeleteOperationActionEntryState.Pending", "pending-only binding"),
        (core, "historySnapshot.SourceDirectoryIdentity != authorization.SourceDirectoryIdentity", "root identity binding"),
        (core, "historyEntry.SourceIdentity != authorizedItem.Identity", "file identity binding"),
        (core, "public bool DeleteMutationAuthorized => false;", "non-authorizing evidence"),
        (core, "public bool MutationBarrierSatisfied => false;", "barrier not implied"),
        (core, "public bool StabilityLeaseLivenessProven => false;", "lease liveness not implied"),
        (tests, "ExactPendingHistoryAndStabilityEvidenceBindWithoutGrantingMutationAuthority", "valid binding regression"),
        (tests, "StabilityEvidenceFromDifferentReceiptFailsClosed", "receipt substitution regression"),
        (tests, "HistoryOperationOrAuthorizationSubstitutionFailsClosed", "durable provenance substitution regression"),
        (tests, "NonPendingOrTerminalHistoryCannotBindForANewBarrier", "stale-state regression"),
        (tests, "RootOrFilePathIdentitySubstitutionFailsClosed", "path/identity substitution regression"),
        (plan, "public enum FileOperationKind\n{\n    Copy,\n    Move,", "Copy/Move enum unchanged"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 unchanged"),
    ):
        checks += require(text, needle, label)

    for text, needle, label in (
        (core, "File.Delete(", "managed delete mutation"),
        (core, "Directory.Delete(", "managed directory mutation"),
        (core, "DeleteFileW", "native delete mutation"),
        (core, "SetFileInformationByHandle", "handle delete mutation"),
        (core, "SafeFileHandle", "Windows handle exposure"),
        (core, "IFileOperationExecutor", "generic executor integration"),
        (plan, "Delete,", "generic Delete operation kind"),
    ):
        checks += forbid(text, needle, label)

    return checks + 1


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0xB1A0125)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {source_checks:,} source checks" if args.repo_root else ""
    print(
        f"PASS: file delete history binding verified with {model_checks:,} model assertions "
        f"across {args.cases:,} randomized states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
