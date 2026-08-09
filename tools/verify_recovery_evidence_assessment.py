#!/usr/bin/env python3
"""Zero-Actions model/source checks for aggregate Copy recovery evidence assessment."""
from __future__ import annotations

import argparse
import itertools
import random
from enum import Enum, auto
from pathlib import Path
from typing import Dict, Iterable, Tuple


class Dimension(Enum):
    DESTINATION_ROOT = auto()
    DESTINATION_IDENTITY = auto()
    MAIN_STREAM = auto()
    HARD_LINK_COUNT = auto()
    BASIC_METADATA = auto()
    OWNER_GROUP_DACL = auto()


class State(Enum):
    MATCHES = auto()
    CHANGED = auto()
    INCOMPLETE = auto()
    UNAVAILABLE = auto()


class Aggregate(Enum):
    OBSERVED_SUBSET_MATCHES = auto()
    OBSERVED_EVIDENCE_CHANGED = auto()
    EVIDENCE_INCOMPLETE = auto()
    EVIDENCE_UNAVAILABLE = auto()


def assess(states: Dict[Dimension, State]) -> Aggregate:
    if any(state is State.CHANGED for state in states.values()):
        return Aggregate.OBSERVED_EVIDENCE_CHANGED
    if any(state is State.UNAVAILABLE for state in states.values()):
        return Aggregate.EVIDENCE_UNAVAILABLE
    if any(state is State.INCOMPLETE for state in states.values()):
        return Aggregate.EVIDENCE_INCOMPLETE
    return Aggregate.OBSERVED_SUBSET_MATCHES


def verify_case(states: Dict[Dimension, State]) -> int:
    assert set(states) == set(Dimension)
    aggregate = assess(states)
    changed = {dimension for dimension, state in states.items() if state is State.CHANGED}
    unavailable = {dimension for dimension, state in states.items() if state is State.UNAVAILABLE}
    incomplete = {dimension for dimension, state in states.items() if state is State.INCOMPLETE}
    matching = {dimension for dimension, state in states.items() if state is State.MATCHES}

    assert changed | unavailable | incomplete | matching == set(Dimension)
    assert not (changed & unavailable)
    assert not (changed & incomplete)
    assert not (changed & matching)
    assert not (unavailable & incomplete)
    assert not (unavailable & matching)
    assert not (incomplete & matching)

    if changed:
        assert aggregate is Aggregate.OBSERVED_EVIDENCE_CHANGED
    elif unavailable:
        assert aggregate is Aggregate.EVIDENCE_UNAVAILABLE
    elif incomplete:
        assert aggregate is Aggregate.EVIDENCE_INCOMPLETE
    else:
        assert aggregate is Aggregate.OBSERVED_SUBSET_MATCHES
        assert matching == set(Dimension)

    return 8


def run_model(cases: int) -> int:
    checks = 0
    dimensions = tuple(Dimension)
    states = tuple(State)

    # Exhaust all 4^6 dimension-state combinations first so every precedence edge
    # is always covered, regardless of the randomized case count.
    for values in itertools.product(states, repeat=len(dimensions)):
        checks += verify_case(dict(zip(dimensions, values)))

    rng = random.Random(20260809)
    for _ in range(cases):
        case = {dimension: rng.choice(states) for dimension in dimensions}
        checks += verify_case(case)

    return checks


def require(source: str, needles: Iterable[str], folded: bool = False) -> int:
    haystack = source.casefold() if folded else source
    checks = 0
    for needle in needles:
        candidate = needle.casefold() if folded else needle
        assert candidate in haystack, needle
        checks += 1
    return checks


def check_repository(root: Path) -> int:
    paths = {
        "core": root / "src/FileOp.Core/Operations/FileOperationRecoveryEvidenceAssessment.cs",
        "tests": root / "tests/FileOp.Windows.Tests/FileOperationRecoveryEvidenceAssessmentTests.cs",
        "docs": root / "docs/file-operation-recovery-evidence-assessment.md",
        "py_wrapper": root / "tools/test-copy-executor-local.py",
        "ps_wrapper": root / "tools/test-copy-executor-local.ps1",
        "windows_gate": root / "tools/test-windows-copy-local.ps1",
    }
    missing = [str(path) for path in paths.values() if not path.is_file()]
    if missing:
        raise FileNotFoundError(", ".join(missing))

    source = {name: path.read_text(encoding="utf-8") for name, path in paths.items()}
    checks = 0

    checks += require(source["core"], (
        "DestinationRoot = 1 << 0",
        "DestinationIdentity = 1 << 1",
        "MainStream = 1 << 2",
        "HardLinkCount = 1 << 3",
        "BasicMetadata = 1 << 4",
        "OwnerGroupDacl = 1 << 5",
        "ObservedSubsetMatches",
        "ObservedEvidenceChanged",
        "EvidenceIncomplete",
        "EvidenceUnavailable",
        "MatchingDimensions",
        "ChangedDimensions",
        "IncompleteDimensions",
        "UnavailableDimensions",
        "ValidateOperationId(",
        "RequireSameOrdinals(",
        "RequireSameInspection(",
        "Array.AsReadOnly(snapshot)",
        "items.OrderBy(static item => item.Ordinal)",
        "changed != FileOperationRecoveryEvidenceDimension.None",
        "unavailable != FileOperationRecoveryEvidenceDimension.None",
        "incomplete != FileOperationRecoveryEvidenceDimension.None",
        "grants no mutation authority",
    ))

    for forbidden in (
        "File.Delete(",
        "File.Move(",
        "File.Copy(",
        "Directory.Delete(",
        "Directory.Move(",
        "FileStream",
        "CreateFileW",
        "NtCreateFile",
        "CanDelete",
        "CanUndo",
    ):
        assert forbidden not in source["core"], forbidden
        checks += 1

    for test_name in (
        "AllImplementedEvidenceMatchesWithoutGrantingUndoAuthority",
        "ChangedEvidenceWinsOverUnavailableAndIncompleteEvidence",
        "UnavailableEvidenceWinsOverIncompleteEvidenceWhenNothingChanged",
        "MissingRecordedDimensionProducesIncompleteAssessment",
        "AssessorRejectsOperationOrdinalAndInspectionSnapshotMismatch",
        "AssessmentDefensivelySnapshotsAndSortsItems",
    ):
        assert test_name in source["tests"], test_name
        checks += 1

    checks += require(source["docs"], (
        "ObservedSubsetMatches",
        "not an “unchanged file” result",
        "alternate data streams",
        "extended attributes",
        "complete set of hard-link names",
        "SACL/audit state",
        "point-in-time evidence",
        "grants **no mutation authority**",
        "explicit user authorization",
    ), folded=True)

    verifier_name = "verify_recovery_evidence_assessment.py"
    assert verifier_name in source["py_wrapper"]
    assert verifier_name in source["ps_wrapper"]
    assert "FullyQualifiedName~FileOperationRecoveryEvidenceAssessmentTests" in source["windows_gate"]
    checks += 3

    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--cases", type=int, default=50000)
    args = parser.parse_args()
    if args.cases < 0:
        raise ValueError("--cases must be non-negative")

    model_checks = run_model(args.cases)
    repository_checks = check_repository(args.repo_root.resolve())
    print(
        "PASS: aggregate Copy recovery evidence assessment verified "
        f"with {model_checks:,} model assertions across all 4^6 combinations plus "
        f"{args.cases:,} randomized cases and {repository_checks:,} source/gate checks."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
