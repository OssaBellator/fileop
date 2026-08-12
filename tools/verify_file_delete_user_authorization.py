#!/usr/bin/env python3
"""Verify session-only file delete user-authorization receipt semantics."""
from __future__ import annotations

import argparse
import random
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class Validation:
    instance_id: int
    plan_id: int
    ready: bool
    root_identity: tuple[int, int] | None
    item_identities: tuple[tuple[int, int] | None, ...]


@dataclass(frozen=True)
class Receipt:
    authorization_id: int
    validation_instance_id: int
    plan_id: int
    root_identity: tuple[int, int]
    item_identities: tuple[tuple[int, int], ...]
    user_authorized_attempt: bool = True
    delete_mutation_authorized: bool = False


def issue_receipt(
    validation: Validation,
    *,
    explicit_user_confirmation: bool,
    generated_authorization_id: int,
) -> Receipt | None:
    if not explicit_user_confirmation:
        return None
    if generated_authorization_id == 0:
        return None
    if not validation.ready:
        return None
    if validation.root_identity is None:
        return None
    if not validation.item_identities or any(identity is None for identity in validation.item_identities):
        return None
    return Receipt(
        authorization_id=generated_authorization_id,
        validation_instance_id=validation.instance_id,
        plan_id=validation.plan_id,
        root_identity=validation.root_identity,
        item_identities=tuple(identity for identity in validation.item_identities if identity is not None),
    )


def bound_to(receipt: Receipt, validation: Validation) -> bool:
    return receipt.validation_instance_id == validation.instance_id


def run_model(cases: int, seed: int) -> int:
    checks = 0
    fixed = Validation(
        instance_id=1,
        plan_id=10,
        ready=True,
        root_identity=(7, 100),
        item_identities=((7, 101), (7, 102)),
    )
    fixed_receipt = issue_receipt(
        fixed,
        explicit_user_confirmation=True,
        generated_authorization_id=99,
    )
    second_receipt = issue_receipt(
        fixed,
        explicit_user_confirmation=True,
        generated_authorization_id=100,
    )
    assert fixed_receipt is not None
    assert second_receipt is not None
    assert fixed_receipt.authorization_id != second_receipt.authorization_id
    assert fixed_receipt.user_authorized_attempt
    assert not fixed_receipt.delete_mutation_authorized
    assert bound_to(fixed_receipt, fixed)
    assert issue_receipt(fixed, explicit_user_confirmation=False, generated_authorization_id=101) is None
    assert issue_receipt(fixed, explicit_user_confirmation=True, generated_authorization_id=0) is None
    assert not bound_to(
        fixed_receipt,
        Validation(
            instance_id=2,
            plan_id=fixed.plan_id,
            ready=fixed.ready,
            root_identity=fixed.root_identity,
            item_identities=fixed.item_identities,
        ),
    )
    checks += 8

    rng = random.Random(seed)
    for case in range(cases):
        item_count = rng.randint(1, 6)
        ready = rng.random() < 0.7
        confirmation = rng.random() < 0.6
        root_identity = None if rng.random() < 0.06 else (case + 1, case + 11)
        items = tuple(
            None if rng.random() < 0.04 else (case + 1, 1000 + case * 8 + ordinal)
            for ordinal in range(item_count)
        )
        validation = Validation(
            instance_id=case + 1,
            plan_id=100_000 + case,
            ready=ready,
            root_identity=root_identity,
            item_identities=items,
        )
        generated_authorization_id = 1_000_000 + case
        receipt = issue_receipt(
            validation,
            explicit_user_confirmation=confirmation,
            generated_authorization_id=generated_authorization_id,
        )
        should_issue = (
            confirmation
            and ready
            and root_identity is not None
            and bool(items)
            and all(identity is not None for identity in items)
        )
        assert (receipt is not None) == should_issue
        checks += 1

        assert issue_receipt(
            validation,
            explicit_user_confirmation=False,
            generated_authorization_id=generated_authorization_id,
        ) is None
        assert issue_receipt(
            validation,
            explicit_user_confirmation=True,
            generated_authorization_id=0,
        ) is None
        checks += 2

        blocked = Validation(
            instance_id=validation.instance_id,
            plan_id=validation.plan_id,
            ready=False,
            root_identity=validation.root_identity,
            item_identities=validation.item_identities,
        )
        assert issue_receipt(
            blocked,
            explicit_user_confirmation=True,
            generated_authorization_id=generated_authorization_id,
        ) is None
        checks += 1

        if receipt is not None:
            assert receipt.authorization_id == generated_authorization_id
            assert receipt.plan_id == validation.plan_id
            assert receipt.root_identity == validation.root_identity
            assert receipt.item_identities == validation.item_identities
            assert receipt.user_authorized_attempt
            assert not receipt.delete_mutation_authorized
            assert bound_to(receipt, validation)
            checks += 7

            separately_generated = issue_receipt(
                validation,
                explicit_user_confirmation=True,
                generated_authorization_id=generated_authorization_id + cases + 1,
            )
            assert separately_generated is not None
            assert separately_generated.authorization_id != receipt.authorization_id
            assert bound_to(separately_generated, validation)
            assert not separately_generated.delete_mutation_authorized
            checks += 4

            equivalent_revalidation = Validation(
                instance_id=validation.instance_id + cases + 1,
                plan_id=validation.plan_id,
                ready=validation.ready,
                root_identity=validation.root_identity,
                item_identities=validation.item_identities,
            )
            assert not bound_to(receipt, equivalent_revalidation)

            changed_plan = Validation(
                instance_id=equivalent_revalidation.instance_id + cases + 1,
                plan_id=validation.plan_id + 1,
                ready=validation.ready,
                root_identity=validation.root_identity,
                item_identities=validation.item_identities,
            )
            assert not bound_to(receipt, changed_plan)

            changed_root = Validation(
                instance_id=changed_plan.instance_id + cases + 1,
                plan_id=validation.plan_id,
                ready=validation.ready,
                root_identity=(validation.root_identity[0], validation.root_identity[1] + 1),
                item_identities=validation.item_identities,
            )
            assert not bound_to(receipt, changed_root)

            changed_items = list(validation.item_identities)
            changed_items[0] = (
                changed_items[0][0],
                changed_items[0][1] + 1,
            )
            changed_item_validation = Validation(
                instance_id=changed_root.instance_id + cases + 1,
                plan_id=validation.plan_id,
                ready=validation.ready,
                root_identity=validation.root_identity,
                item_identities=tuple(changed_items),
            )
            assert not bound_to(receipt, changed_item_validation)
            checks += 4

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
    core = (root / "src/FileOp.Core/Operations/FileDeleteOperationUserAuthorization.cs").read_text(encoding="utf-8")
    validation = (root / "src/FileOp.Core/Operations/FileDeleteOperationExecutionValidation.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/FileDeleteOperationUserAuthorizationTests.cs").read_text(encoding="utf-8")
    docs = (root / "docs/file-delete-user-authorization.md").read_text(encoding="utf-8")
    plan = (root / "src/FileOp.Core/Operations/FileOperationPlan.cs").read_text(encoding="utf-8")
    generic_execution = (root / "src/FileOp.Core/Operations/FileOperationExecution.cs").read_text(encoding="utf-8")
    cleanup = (root / "src/FileOp.Core/Storage/StorageCleanupReadiness.cs").read_text(encoding="utf-8")
    parent = (root / "tools/verify_file_delete_execution_validation.py").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")

    production_consumers = []
    for subtree in ("src/FileOp.App", "src/FileOp.Windows", "src/FileOp.Indexer"):
        directory = root / subtree
        if directory.exists():
            for path in directory.rglob("*.cs"):
                text = path.read_text(encoding="utf-8")
                if "FileDeleteOperationUserAuthorization" in text:
                    production_consumers.append(path.relative_to(root).as_posix())
    if production_consumers:
        raise AssertionError(
            "user authorization must remain unwired from production in this slice: "
            + ", ".join(production_consumers)
        )

    checks = 0
    required = [
        (core, "public sealed record FileDeleteOperationUserAuthorizationReceipt", "authorization receipt contract"),
        (core, "internal FileDeleteOperationUserAuthorizationReceipt(", "non-public receipt construction"),
        (core, "authorizationId == Guid.Empty", "non-empty authorization id"),
        (core, "!validation.CanRequestAuthorizationReview", "ready validation prerequisite"),
        (core, "validation.Status != FileDeleteOperationExecutionValidationStatus.ReadyForAuthorizationReview", "ready status prerequisite"),
        (core, "validation.DeleteMutationAuthorized", "validation remains non-authorizing prerequisite"),
        (core, "validation.SourceDirectory.Identity is not FileIdentity sourceDirectoryIdentity", "root identity snapshot"),
        (core, "validation.Items.Count != validation.Plan.Intent.Entries.Count", "complete evidence requirement"),
        (core, "item.Entry != plannedEntry", "exact ordered entry provenance"),
        (core, "item.Source.Identity is not FileIdentity identity", "file identity snapshot"),
        (core, "AuthorizationId = authorizationId;", "authorization id capture"),
        (core, "Validation = validation;", "exact validation instance capture"),
        (core, "Plan = validation.Plan;", "exact plan capture"),
        (core, "AuthorizedAtUtc = authorizedAtUtc.ToUniversalTime();", "authorization timestamp normalization"),
        (core, "CanonicalSourceDirectoryPath = validation.SourceDirectory.CanonicalPath;", "canonical root snapshot"),
        (core, "public bool UserAuthorizedAttempt => true;", "explicit user-consent semantic"),
        (core, "public bool DeleteMutationAuthorized => false;", "hard non-mutation authorization"),
        (core, "ReferenceEquals(Validation, validation)", "exact validation instance binding"),
        (core, "public interface IFileDeleteOperationUserAuthorizationIssuer", "issuer contract"),
        (core, "IssueAfterExplicitUserConfirmation", "explicit-confirmation issuer method"),
        (core, "TimeProvider.System, Guid.NewGuid", "production time/id sources"),
        (core, "_authorizationIdFactory();", "issuer-owned authorization id"),
        (core, "_timeProvider.GetUtcNow()", "issuer-owned UTC observation"),
        (core, "authorization ID source returned an empty ID", "invalid id-source rejection"),
        (validation, "public bool DeleteMutationAuthorized => false;", "validation non-authorization retained"),
        (tests, "ReadyValidationCanRecordUserConsentWithoutGrantingMutationAuthority", "positive consent regression"),
        (tests, "BlockedValidationCannotRecordUserAuthorization", "blocked validation regression"),
        (tests, "ReceiptIsBoundToExactValidationInstanceNotEquivalentRevalidation", "revalidation invalidation regression"),
        (tests, "IssuerOwnsFreshAuthorizationIdAndUtcObservation", "source-owned id/time regression"),
        (tests, "ReceiptSnapshotsExactOrderedIdentityEvidence", "identity snapshot regression"),
        (docs, "explicit user confirmation", "explicit confirmation documentation"),
        (docs, "absence of a receipt", "decline/cancel documentation"),
        (docs, "issuer owns", "source-owned provenance documentation"),
        (docs, "not a mutation lease", "lease boundary documentation"),
        (docs, "no time window", "no fake time-safety documentation"),
        (docs, "exact validation instance", "exact validation binding documentation"),
        (cleanup, "CleanupMutationAuthorized => false", "cleanup non-authorization retained"),
        (plan, "public enum FileOperationKind\n{\n    Copy,\n    Move,", "Copy/Move operation enum unchanged"),
        (parent, "from verify_file_delete_user_authorization import (", "delete validator imports authorization child"),
        (parent, "run_delete_authorization_model(args.cases", "delete validator runs authorization model"),
        (parent, "check_delete_authorization_repository(repo_root)", "delete validator runs authorization source checks"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
    ]
    for text, needle, label in required:
        checks += require(text, needle, label)

    for text, needle, label in (
        (plan, "Delete,", "Delete operation enum"),
        (generic_execution, "FileDeleteOperationPlan", "generic delete execution integration"),
        (core, "IFileOperationExecutor", "delete executor integration"),
        (core, "File.Delete(", "managed delete primitive"),
        (core, "Directory.Delete(", "managed directory delete primitive"),
        (core, "DeleteFileW", "native delete primitive"),
        (core, "SetFileInformationByHandle", "handle delete primitive"),
        (core, "MoveFileEx", "move/delete primitive"),
        (core, "IFileOperationActionHistory", "delete action-history integration"),
    ):
        checks += forbid(text, needle, label)

    checks += 1
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0xA0710)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {source_checks:,} source checks" if args.repo_root else ""
    print(
        f"PASS: file delete user authorization verified with {model_checks:,} model assertions "
        f"across {args.cases:,} randomized states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
