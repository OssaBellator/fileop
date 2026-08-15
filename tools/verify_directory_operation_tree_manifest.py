#!/usr/bin/env python3
"""Portable model/source checks for the non-authorizing directory tree manifest."""
from __future__ import annotations

import argparse
import random
import re
import sys
from dataclasses import dataclass
from pathlib import Path, PureWindowsPath


@dataclass(frozen=True)
class Entry:
    relative: str
    canonical: str
    volume: int
    file_ref: int
    is_dir: bool
    is_reparse: bool = False


def split_relative(relative: str) -> tuple[str, ...] | None:
    if not relative or relative.isspace():
        return None
    path = PureWindowsPath(relative)
    if path.is_absolute() or path.drive or path.root:
        return None
    parts = tuple(re.split(r"[\\/]", relative))
    if not parts or any(
        not part
        or part.isspace()
        or part in {".", ".."}
        or part.endswith(" ")
        or part.endswith(".")
        for part in parts
    ):
        return None
    return parts


def expected_canonical(root: str, parts: tuple[str, ...]) -> str:
    return str(PureWindowsPath(root, *parts))


def manifest_valid(
    *,
    fidelity_complete: bool,
    unsupported_features: bool,
    fidelity_root_matches: bool,
    root_evidence_valid: bool,
    root: str,
    root_volume: int,
    root_ref: int,
    entries: list[Entry],
) -> bool:
    if (
        not fidelity_complete
        or unsupported_features
        or not fidelity_root_matches
        or not root_evidence_valid
        or not root
    ):
        return False

    by_path: dict[str, Entry] = {}
    identities: set[tuple[int, int]] = {(root_volume, root_ref)}
    normalized: list[tuple[Entry, tuple[str, ...], str]] = []

    for entry in entries:
        parts = split_relative(entry.relative)
        if parts is None or entry.is_reparse:
            return False
        relative = "\\".join(parts)
        key = relative.casefold()
        if key in by_path:
            return False
        if entry.volume != root_volume:
            return False
        identity = (entry.volume, entry.file_ref)
        if identity in identities:
            return False
        if entry.canonical.casefold() != expected_canonical(root, parts).casefold():
            return False
        identities.add(identity)
        by_path[key] = entry
        normalized.append((entry, parts, relative))

    for entry, parts, _ in normalized:
        if len(parts) == 1:
            continue
        parent_key = "\\".join(parts[:-1]).casefold()
        parent = by_path.get(parent_key)
        if parent is None or not parent.is_dir:
            return False

    return True


def generate_valid_tree(rng: random.Random) -> tuple[str, int, int, list[Entry]]:
    root = r"C:\ManifestRoot"
    root_volume = rng.randrange(1, 2**16)
    root_ref = 1
    next_ref = 2
    entries: list[Entry] = []
    directories: list[tuple[str, ...]] = []

    for index in range(rng.randrange(0, 10)):
        use_parent = bool(directories) and rng.random() < 0.6
        parent = rng.choice(directories) if use_parent else ()
        name = f"node-{index}-{rng.randrange(1_000_000)}"
        parts = (*parent, name)
        is_dir = rng.random() < 0.45
        relative = "\\".join(parts)
        entries.append(
            Entry(
                relative=relative,
                canonical=expected_canonical(root, parts),
                volume=root_volume,
                file_ref=next_ref,
                is_dir=is_dir,
            )
        )
        next_ref += 1
        if is_dir:
            directories.append(parts)

    return root, root_volume, root_ref, entries


def run_model(cases: int) -> int:
    rng = random.Random(0xD1_196_2026)
    checks = 0

    assert manifest_valid(
        fidelity_complete=True,
        unsupported_features=False,
        fidelity_root_matches=True,
        root_evidence_valid=True,
        root=r"C:\ManifestRoot",
        root_volume=10,
        root_ref=1,
        entries=[],
    )
    checks += 1

    defect_kinds = (
        "none",
        "incomplete",
        "unsupported",
        "fidelity_root_mismatch",
        "invalid_root_evidence",
        "rooted",
        "parent_escape",
        "ambiguous_name",
        "canonical_mismatch",
        "descendant_reparse",
        "different_volume",
        "duplicate_path",
        "duplicate_identity",
        "missing_parent",
        "file_parent",
        "root_identity_reuse",
    )

    for _ in range(cases):
        root, root_volume, root_ref, entries = generate_valid_tree(rng)
        defect = rng.choice(defect_kinds)
        fidelity_complete = True
        unsupported_features = False
        fidelity_root_matches = True
        root_evidence_valid = True
        expected = True

        if defect == "incomplete":
            fidelity_complete = False
            expected = False
        elif defect == "unsupported":
            unsupported_features = True
            expected = False
        elif defect == "fidelity_root_mismatch":
            fidelity_root_matches = False
            expected = False
        elif defect == "invalid_root_evidence":
            root_evidence_valid = False
            expected = False
        elif defect == "rooted":
            entries.append(Entry(r"C:\outside.txt", r"C:\outside.txt", root_volume, 100_001, False))
            expected = False
        elif defect == "parent_escape":
            entries.append(Entry(r"..\escape.txt", r"C:\escape.txt", root_volume, 100_002, False))
            expected = False
        elif defect == "ambiguous_name":
            entries.append(Entry("ambiguous.", root + r"\ambiguous.", root_volume, 100_011, False))
            expected = False
        elif defect == "canonical_mismatch":
            entries.append(Entry("mismatch.txt", root + r"\different.txt", root_volume, 100_003, False))
            expected = False
        elif defect == "descendant_reparse":
            entries.append(Entry("link", root + r"\link", root_volume, 100_012, False, True))
            expected = False
        elif defect == "different_volume":
            entries.append(Entry("other-volume.txt", root + r"\other-volume.txt", root_volume + 1, 100_004, False))
            expected = False
        elif defect == "duplicate_path":
            entries.extend(
                [
                    Entry("Duplicate.txt", root + r"\Duplicate.txt", root_volume, 100_005, False),
                    Entry("duplicate.TXT", root + r"\duplicate.TXT", root_volume, 100_006, False),
                ]
            )
            expected = False
        elif defect == "duplicate_identity":
            entries.extend(
                [
                    Entry("one.txt", root + r"\one.txt", root_volume, 100_007, False),
                    Entry("two.txt", root + r"\two.txt", root_volume, 100_007, False),
                ]
            )
            expected = False
        elif defect == "missing_parent":
            entries.append(Entry(r"missing\child.txt", root + r"\missing\child.txt", root_volume, 100_008, False))
            expected = False
        elif defect == "file_parent":
            entries.extend(
                [
                    Entry("parent", root + r"\parent", root_volume, 100_009, False),
                    Entry(r"parent\child.txt", root + r"\parent\child.txt", root_volume, 100_010, False),
                ]
            )
            expected = False
        elif defect == "root_identity_reuse":
            entries.append(Entry("cycle", root + r"\cycle", root_volume, root_ref, True))
            expected = False

        actual = manifest_valid(
            fidelity_complete=fidelity_complete,
            unsupported_features=unsupported_features,
            fidelity_root_matches=fidelity_root_matches,
            root_evidence_valid=root_evidence_valid,
            root=root,
            root_volume=root_volume,
            root_ref=root_ref,
            entries=entries,
        )
        assert actual is expected, (defect, entries)
        checks += 1

        if expected:
            assert len({(entry.volume, entry.file_ref) for entry in entries}) == len(entries)
            assert all(entry.volume == root_volume for entry in entries)
            assert all(not entry.is_reparse for entry in entries)
            checks += 3

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
    fidelity = read(root, "src/FileOp.Core/Operations/DirectoryOperationFidelity.cs")
    manifest = read(root, "src/FileOp.Core/Operations/DirectoryOperationTreeManifest.cs")
    tests = read(root, "tests/FileOp.Windows.Tests/DirectoryOperationTreeManifestTests.cs")

    checks = 0
    checks += require(
        fidelity,
        "EligibleForFuturePlainTreeExecutor",
        "classification still grants no mutation authority",
        "a recursive durable-history executor remains required",
    )
    checks += require(
        manifest,
        "DirectoryOperationFidelityClassifier.Classify(fidelityEvidence)",
        "support.CanEnterFutureMutationBoundary",
        "DirectoryOperationTreeEntryEvidence",
        "FileOperationCanonicalPath Source",
        "DirectoryOperationTreeManifestEntry",
        "CanonicalRootPath",
        "RootIdentity",
        "IReadOnlyList<DirectoryOperationTreeManifestEntry> Entries",
        "GrantsMutationAuthority => false",
        "root.State != FileOperationCanonicalPathState.Directory",
        "root.IsLeafReparsePoint",
        "root.Identity is not FileIdentity rootIdentity",
        "not bound to the execution-validated canonical root",
        "Path.IsPathRooted(entry.RelativePath)",
        'segment == ".."',
        "segment.EndsWith(' ')",
        "segment.EndsWith('.')",
        "source.State switch",
        "source.IsLeafReparsePoint",
        "source.Identity is not FileIdentity identity",
        "expectedCanonicalPath",
        "StringComparison.OrdinalIgnoreCase",
        "identity.VolumeSerialNumber != rootIdentity.VolumeSerialNumber",
        "EnsureUniqueRelativePaths(normalized)",
        "EnsureUniqueObjectIdentities(rootIdentity, normalized)",
        "EnsureCompleteParentTopology(normalized)",
        "StringComparer.OrdinalIgnoreCase",
        "Hard-link/cycle evidence is unsupported",
        "missing parent directory",
        "represented as a file",
        "OrderBy(static entry => entry.Depth)",
        "Array.AsReadOnly(ordered)",
    )
    checks += forbid(
        manifest,
        "Directory.CreateDirectory(",
        "Directory.Delete(",
        "Directory.Move(",
        "File.Copy(",
        "File.Move(",
        "File.Delete(",
        "FileStream(",
    )
    checks += require(
        tests,
        "EmptyPlainDirectoryCreatesNonAuthorizingManifest",
        "NestedPlainTreeIsDeterministicallyOrderedParentBeforeChild",
        "UnsupportedFidelityCannotCreateManifest",
        "IncompleteFidelityCannotCreateManifest",
        "FidelityRootMustMatchExecutionValidatedCanonicalRoot",
        "RootMustBeCanonicalNonReparseDirectoryWithIdentity",
        "RootedRelativePathIsRejected",
        "ParentDirectoryEscapeIsRejected",
        "TrailingDotOrSpaceSegmentIsRejectedAsAmbiguous",
        "CanonicalPathMismatchIsRejected",
        "DescendantMustBeExistingNonReparseCanonicalEvidence",
        "DuplicateCaseInsensitiveRelativePathIsRejected",
        "DifferentVolumeDescendantIdentityIsRejected",
        "MissingParentDirectoryIsRejected",
        "ParentRepresentedAsFileIsRejected",
        "ReusedObjectIdentityIsRejectedAsContradictoryPlainTreeEvidence",
        "RootIdentityCannotReappearAsDescendant",
        "ManifestCopiesInputEvidenceAndPreservesExactIdentityBinding",
        "Assert.IsFalse(manifest.GrantsMutationAuthority)",
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
    print(f"PASS directory tree manifest model: {model_checks} checks across {args.cases} randomized cases")
    if not args.model_only:
        source_checks = check_repository(args.repo_root.resolve())
        print(f"PASS directory tree manifest source contract: {source_checks} checks")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
