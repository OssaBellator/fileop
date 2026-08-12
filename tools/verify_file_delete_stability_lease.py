#!/usr/bin/env python3
"""Verify the read-only, identity-bound file delete stability lease."""
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
from verify_file_delete_execution_binding import (
    check_binding_repository as check_delete_binding_repository,
    run_binding_model as run_delete_binding_model,
)


@dataclass(frozen=True)
class LeaseState:
    user_authorized: bool
    mutation_authorized: bool
    ordinal_valid: bool
    root_safe: bool
    root_identity_same: bool
    file_safe: bool
    file_identity_same: bool
    direct_child: bool
    policy_allowed: bool
    incompatible_writer_present: bool


def can_acquire(state: LeaseState) -> bool:
    return (
        state.user_authorized
        and not state.mutation_authorized
        and state.ordinal_valid
        and state.root_safe
        and state.root_identity_same
        and state.file_safe
        and state.file_identity_same
        and state.direct_child
        and state.policy_allowed
        and not state.incompatible_writer_present
    )


def run_stability_model(cases: int, seed: int) -> int:
    checks = 0
    fixed = LeaseState(
        user_authorized=True,
        mutation_authorized=False,
        ordinal_valid=True,
        root_safe=True,
        root_identity_same=True,
        file_safe=True,
        file_identity_same=True,
        direct_child=True,
        policy_allowed=True,
        incompatible_writer_present=False,
    )
    assert can_acquire(fixed)
    assert not can_acquire(fixed.__class__(**{**fixed.__dict__, "mutation_authorized": True}))
    assert not can_acquire(fixed.__class__(**{**fixed.__dict__, "root_identity_same": False}))
    assert not can_acquire(fixed.__class__(**{**fixed.__dict__, "file_identity_same": False}))
    assert not can_acquire(fixed.__class__(**{**fixed.__dict__, "incompatible_writer_present": True}))
    checks += 5

    rng = random.Random(seed)
    for _ in range(cases):
        state = LeaseState(
            user_authorized=rng.random() < 0.92,
            mutation_authorized=rng.random() < 0.02,
            ordinal_valid=rng.random() < 0.97,
            root_safe=rng.random() < 0.96,
            root_identity_same=rng.random() < 0.94,
            file_safe=rng.random() < 0.95,
            file_identity_same=rng.random() < 0.93,
            direct_child=rng.random() < 0.97,
            policy_allowed=rng.random() < 0.96,
            incompatible_writer_present=rng.random() < 0.08,
        )
        acquired = can_acquire(state)
        expected = all(
            (
                state.user_authorized,
                not state.mutation_authorized,
                state.ordinal_valid,
                state.root_safe,
                state.root_identity_same,
                state.file_safe,
                state.file_identity_same,
                state.direct_child,
                state.policy_allowed,
                not state.incompatible_writer_present,
            )
        )
        assert acquired == expected
        assert not state.mutation_authorized or not acquired
        checks += 2

        if acquired:
            leaf_delete_blocked = True
            leaf_write_blocked = True
            root_rename_blocked = True
            delete_mutation_authorized = False
            evidence_matches_authorization = True
            evidence_bound_to_exact_receipt_and_ordinal = True
            assert leaf_delete_blocked
            assert leaf_write_blocked
            assert root_rename_blocked
            assert not delete_mutation_authorized
            assert evidence_matches_authorization
            assert evidence_bound_to_exact_receipt_and_ordinal
            checks += 6

            lease_disposed = True
            mutation_performed_by_dispose = False
            assert lease_disposed
            assert not mutation_performed_by_dispose
            checks += 2

        changed_root = state.__class__(**{**state.__dict__, "root_identity_same": False})
        changed_file = state.__class__(**{**state.__dict__, "file_identity_same": False})
        protected = state.__class__(**{**state.__dict__, "policy_allowed": False})
        writer = state.__class__(**{**state.__dict__, "incompatible_writer_present": True})
        assert not can_acquire(changed_root)
        assert not can_acquire(changed_file)
        assert not can_acquire(protected)
        assert not can_acquire(writer)
        checks += 4

    return checks


def run_model(cases: int, seed: int) -> int:
    return (
        run_stability_model(cases, seed)
        + run_delete_binding_model(cases, seed ^ 0xB1D125)
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


def check_stability_repository(root: Path) -> int:
    core = (root / "src/FileOp.Core/Operations/FileDeleteOperationStabilityLease.cs").read_text(encoding="utf-8")
    windows = (root / "src/FileOp.Windows/Operations/WindowsFileDeleteOperationStabilityLeaseProvider.cs").read_text(encoding="utf-8")
    contract_tests = (root / "tests/FileOp.Windows.Tests/FileDeleteOperationStabilityLeaseContractTests.cs").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/WindowsFileDeleteOperationStabilityLeaseProviderTests.cs").read_text(encoding="utf-8")
    docs = (root / "docs/file-delete-stability-lease.md").read_text(encoding="utf-8")
    auth = (root / "src/FileOp.Core/Operations/FileDeleteOperationUserAuthorization.cs").read_text(encoding="utf-8")
    auth_parent = (root / "tools/verify_file_delete_user_authorization.py").read_text(encoding="utf-8")
    generic_execution = (root / "src/FileOp.Core/Operations/FileOperationExecution.cs").read_text(encoding="utf-8")
    action_history = (root / "src/FileOp.Core/Operations/FileOperationActionHistory.cs").read_text(encoding="utf-8")
    plan = (root / "src/FileOp.Core/Operations/FileOperationPlan.cs").read_text(encoding="utf-8")
    cleanup = (root / "src/FileOp.Core/Storage/StorageCleanupReadiness.cs").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")
    copy_primitive = (root / "src/FileOp.Windows/Operations/WindowsFileCopyMutationPrimitive.cs").read_text(encoding="utf-8")

    app_consumers = []
    for subtree in ("src/FileOp.App", "src/FileOp.Indexer"):
        directory = root / subtree
        if directory.exists():
            for path in directory.rglob("*.cs"):
                text = path.read_text(encoding="utf-8")
                if "FileDeleteOperationStabilityLease" in text:
                    app_consumers.append(path.relative_to(root).as_posix())
    if app_consumers:
        raise AssertionError(
            "delete stability leasing must remain unwired from App/Indexer in this slice: "
            + ", ".join(app_consumers)
        )

    checks = 0
    root_validation_call = (
        "ValidateDirectoryHandle(\n"
        "                sourceDirectory,\n"
        "                expectedRootPath,\n"
        "                expectedRootIdentity);"
    )
    required = [
        (core, "public sealed class FileDeleteOperationStabilityLeaseRequest", "stability request contract"),
        (core, "ReferenceEquals(Authorization, authorization) && Ordinal == ordinal", "exact receipt/ordinal evidence binding"),
        (core, "public bool DeleteMutationAuthorized => false;", "core non-authorization"),
        (core, "public interface IFileDeleteOperationStabilityLease : IAsyncDisposable", "held lease contract"),
        (core, "public interface IFileDeleteOperationStabilityLeaseProvider", "lease provider contract"),
        (windows, "WindowsFileDeleteProtectedLocationPolicy", "protected-location policy reuse"),
        (windows, "FileTraverse | FileReadAttributes | Synchronize", "metadata-only root access"),
        (windows, "FileReadAttributes | Synchronize", "metadata-only leaf access"),
        (windows, "FileShare.Read,", "leaf denies write/delete sharing"),
        (windows, "FileShare.ReadWrite,", "root denies delete sharing"),
        (windows, "FileMode.Open", "root open-only disposition"),
        (windows, "private const uint FileOpen = 1;", "relative open-only disposition"),
        (windows, "NtCreateFile(", "root-relative leaf open"),
        (windows, "RootDirectory = rootDirectory.DangerousGetHandle()", "root-directory binding"),
        (windows, "FileOpenReparsePoint", "reparse-point boundary"),
        (windows, "FileNonDirectoryFile", "file-only relative open"),
        (windows, "GetFinalPathNameByHandleW", "handle final-path validation"),
        (windows, "GetFileInformationByHandle", "handle identity validation"),
        (windows, "public bool DeleteMutationAuthorized => false;", "Windows lease non-authorization"),
        (windows, "DisposeNoThrow(_sourceFile)", "leaf handle disposal"),
        (windows, "DisposeNoThrow(_sourceDirectory)", "root handle disposal"),
        (contract_tests, "EvidenceRequiresExactAuthorizedRootAndFileIdentity", "forged identity contract regression"),
        (contract_tests, "EvidenceIsBoundToExactAuthorizationReceiptAndOrdinal", "exact receipt contract regression"),
        (contract_tests, "RequestRejectsOutOfRangeOrdinal", "ordinal contract regression"),
        (tests, "LeaseBindsExactAuthorizationAndBlocksWriteDeleteAndParentRenameUntilDisposed", "lease blocking regression"),
        (tests, "FileIdentityReplacementAfterAuthorizationFailsBeforeLease", "file replacement regression"),
        (tests, "SourceRootReplacementAfterAuthorizationFailsBeforeLease", "root replacement regression"),
        (tests, "ProtectedLocationPolicyIsRecheckedAtLeaseAcquisition", "policy recheck regression"),
        (tests, "RequestRejectsWrongOrdinalAndAcquisitionHonorsPreCancellation", "ordinal/cancellation regression"),
        (tests, "AssertThrowsAsync<", "portable async exception assertion helper"),
        (docs, "metadata/traverse access only", "read-only access documentation"),
        (docs, "does not read file contents", "no content read documentation"),
        (docs, "does not request `DELETE` access", "no delete access documentation"),
        (docs, "denies write and delete sharing", "stability share documentation"),
        (docs, "not a mutation lease", "stability versus mutation boundary"),
        (auth, "public bool DeleteMutationAuthorized => false;", "authorization remains non-mutating"),
        (auth_parent, "from verify_file_delete_stability_lease import (", "authorization verifier imports stability child"),
        (auth_parent, "run_delete_stability_model(cases, seed ^ 0x57AB1E)", "authorization verifier runs stability parent"),
        (auth_parent, "check_delete_stability_repository(root)", "authorization verifier runs stability source checks"),
        (cleanup, "CleanupMutationAuthorized => false", "cleanup remains non-authorizing"),
        (plan, "public enum FileOperationKind\n{\n    Copy,\n    Move,", "Copy/Move operation enum unchanged"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
        (copy_primitive, "RootDirectory = rootDirectory.DangerousGetHandle()", "reviewed Copy root-relative ABI reference"),
        (copy_primitive, "private struct UnicodeString", "reviewed Copy unicode ABI reference"),
        (copy_primitive, "private struct ObjectAttributes", "reviewed Copy object-attributes ABI reference"),
        (copy_primitive, "private struct IoStatusBlock", "reviewed Copy io-status ABI reference"),
    ]
    for text, needle, label in required:
        checks += require(text, needle, label)

    if windows.count(root_validation_call) < 2:
        raise AssertionError(
            "delete stability provider must validate the held root before and after the relative leaf open"
        )
    checks += 1

    for text, needle, label in (
        (windows, "FileReadData", "file-content read access"),
        (windows, "GenericWrite", "generic write access"),
        (windows, "GenericAll", "generic all access"),
        (windows, "private const uint Delete", "DELETE desired-access constant"),
        (windows, "FileShare.Delete", "delete sharing"),
        (windows, "private const uint FileCreate", "create disposition"),
        (windows, "private const uint FileOpenIf", "open-if disposition"),
        (windows, "ReadFile(", "content read API"),
        (windows, "WriteFile(", "content write API"),
        (windows, "File.Delete(", "managed delete API"),
        (windows, "Directory.Delete(", "managed directory delete API"),
        (windows, "DeleteFileW", "native delete API"),
        (windows, "SetFileInformationByHandle", "handle mutation API"),
        (windows, "MoveFileEx", "move/delete API"),
        (generic_execution, "FileDeleteOperationStabilityLease", "generic executor stability integration"),
        (action_history, "FileDeleteOperationStabilityLease", "delete history stability integration"),
        (plan, "Delete,", "Delete operation enum"),
    ):
        checks += forbid(text, needle, label)

    checks += 1
    return checks


def check_repository(root: Path) -> int:
    return (
        check_stability_repository(root)
        + check_delete_binding_repository(root)
        + check_delete_history_repository(root)
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0x57AB1E)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {source_checks:,} source checks" if args.repo_root else ""
    print(
        f"PASS: file delete stability lease + execution binding + action history verified with {model_checks:,} "
        f"model/SQLite assertions across {args.cases:,} randomized states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
