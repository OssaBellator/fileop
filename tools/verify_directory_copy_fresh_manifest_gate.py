#!/usr/bin/env python3
"""Portable model/source checks for directory Copy fresh-manifest gating."""
from __future__ import annotations

import argparse
import random
import sys
from pathlib import Path

from verify_directory_copy_transaction import (
    check_repository as check_directory_copy_transaction_repository,
    run_model as run_directory_copy_transaction_model,
)


def gate(acquisition: str, *, changed: bool) -> str:
    if acquisition == "Unsupported":
        return "AcquisitionUnsupported"
    if acquisition != "Ready":
        return "AcquisitionUnavailable"
    if changed:
        return "ReviewedTreeChanged"
    return "ReadyForDurableHistory"


def run_model(cases: int) -> int:
    assert gate("Ready", changed=False) == "ReadyForDurableHistory"
    assert gate("Ready", changed=True) == "ReviewedTreeChanged"
    assert gate("Unsupported", changed=False) == "AcquisitionUnsupported"
    assert gate("Unavailable", changed=False) == "AcquisitionUnavailable"
    checks = 4

    rng = random.Random(0xD1C0_205)
    statuses = ["Ready", "Unsupported", "Unavailable"]
    for _ in range(cases):
        acquisition = rng.choice(statuses)
        changed = bool(rng.getrandbits(1))
        result = gate(acquisition, changed=changed)
        can_begin_history = result == "ReadyForDurableHistory"
        assert can_begin_history == (acquisition == "Ready" and not changed)
        checks += 1
        if acquisition != "Ready":
            assert result != "ReviewedTreeChanged"
            checks += 1
        if changed and acquisition == "Ready":
            assert not can_begin_history
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
    acquisition = read(root, "src/FileOp.Core/Operations/DirectoryOperationTreeManifestAcquisition.cs")
    gate_source = read(root, "src/FileOp.Core/Operations/DirectoryCopyFreshManifestGate.cs")
    revalidation = read(root, "src/FileOp.Core/Operations/DirectoryOperationTreeManifestRevalidation.cs")
    tests = read(root, "tests/FileOp.Windows.Tests/DirectoryCopyFreshManifestGateTests.cs")
    checks = 0

    checks += require(
        acquisition,
        "public enum DirectoryOperationTreeManifestAcquisitionStatus",
        "Ready,",
        "Unsupported,",
        "Unavailable,",
        "DirectoryOperationFidelityClassifier.Classify(fidelityEvidence)",
        "Ready directory manifest acquisition requires complete plain-tree fidelity evidence.",
        "Unsupported/unavailable directory manifest acquisition must not publish a manifest",
        "public bool CanRevalidateReviewedManifest",
        "public bool GrantsMutationAuthority => false",
        "public bool GrantsCopyAuthority => false",
        "public bool GrantsCreateAuthority => false",
        "public bool GrantsDeleteAuthority => false",
        "public interface IDirectoryOperationTreeManifestAcquirer",
        "AcquireFreshAsync(",
    )
    checks += require(
        gate_source,
        "public enum DirectoryCopyFreshManifestGateStatus",
        "ReadyForDurableHistory",
        "AcquisitionUnsupported",
        "AcquisitionUnavailable",
        "ReviewedTreeChanged",
        "public bool CanBeginDurableHistory",
        "public bool GrantsMutationAuthority => false",
        "DirectoryOperationTreeManifestRevalidator.Compare(",
        "if (!revalidation.EvidenceStillMatches)",
        "Separate durable directory-Copy history may now begin; no filesystem mutation authority was created",
    )
    assert gate_source.index("AcquireFreshAsync(") < gate_source.index("DirectoryOperationTreeManifestRevalidator.Compare(")
    checks += 1
    checks += require(
        revalidation,
        "public bool EvidenceStillMatches => Changes.Count == 0",
        "public bool GrantsMutationAuthority => false",
    )
    checks += require(
        tests,
        "ExactFreshManifestAllowsOnlyDurableHistoryBoundary",
        "ChangedObjectIdentityFailsBeforeDurableHistory",
        "UnsupportedFreshFidelityFailsClosedWithoutRevalidation",
        "UnavailableMetadataInspectionFailsClosedWithoutRevalidation",
        "ReadyAcquisitionRejectsIncompleteFidelityEvidence",
        "UnsupportedAcquisitionCannotPublishManifest",
        "CancellationIsObservedBeforeAcquisition",
    )
    combined = acquisition + gate_source
    checks += forbid(
        combined,
        "Directory.Enumerate",
        "Directory.CreateDirectory(",
        "Directory.Move(",
        "Directory.Delete(",
        "File.Copy(",
        "File.Move(",
        "File.Delete(",
        "FileStream(",
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
    transaction_checks = run_directory_copy_transaction_model(args.cases)
    print(
        f"PASS directory Copy fresh-manifest gate model: {model_checks:,} checks across {args.cases:,} randomized cases; "
        f"recursive transaction: {transaction_checks:,} checks"
    )
    if not args.model_only:
        source_checks = check_repository(args.repo_root.resolve())
        transaction_source_checks = check_directory_copy_transaction_repository(args.repo_root.resolve())
        print(
            f"PASS directory Copy fresh-manifest gate source contract: {source_checks} checks; "
            f"recursive transaction: {transaction_source_checks} checks"
        )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
