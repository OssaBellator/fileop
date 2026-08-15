#!/usr/bin/env python3
"""Portable model/source checks for non-authorizing directory tree manifest revalidation."""
from __future__ import annotations

import argparse
import random
import sys
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class Entry:
    relative: str
    canonical: str
    identity: tuple[int, int]
    kind: str


@dataclass(frozen=True)
class Manifest:
    root: str
    root_identity: tuple[int, int]
    entries: tuple[Entry, ...]


def compare(initial: Manifest, fresh: Manifest) -> tuple[str, ...]:
    changes: list[str] = []
    if initial.root.casefold() != fresh.root.casefold():
        changes.append("root_path")
    if initial.root_identity != fresh.root_identity:
        changes.append("root_identity")

    initial_by_path = {entry.relative.casefold(): entry for entry in initial.entries}
    fresh_by_path = {entry.relative.casefold(): entry for entry in fresh.entries}
    for key, entry in initial_by_path.items():
        observed = fresh_by_path.get(key)
        if observed is None:
            changes.append("removed:" + key)
            continue
        if entry.identity != observed.identity:
            changes.append("identity:" + key)
        if entry.kind != observed.kind:
            changes.append("kind:" + key)
        if entry.canonical.casefold() != observed.canonical.casefold():
            changes.append("canonical:" + key)
    for key in fresh_by_path.keys() - initial_by_path.keys():
        changes.append("added:" + key)
    return tuple(sorted(changes))


def base_manifest() -> Manifest:
    return Manifest(
        root=r"C:\Tree",
        root_identity=(77, 1),
        entries=(
            Entry("folder", r"C:\Tree\folder", (77, 2), "dir"),
            Entry(r"folder\file.txt", r"C:\Tree\folder\file.txt", (77, 3), "file"),
            Entry("root.bin", r"C:\Tree\root.bin", (77, 4), "file"),
        ),
    )


def mutate(rng: random.Random, manifest: Manifest, defect: str) -> Manifest:
    entries = list(manifest.entries)
    root = manifest.root
    root_identity = manifest.root_identity

    if defect == "root_path_case_only":
        root = r"c:\TREE"
        entries = [
            Entry(entry.relative, entry.canonical.swapcase(), entry.identity, entry.kind)
            for entry in entries
        ]
    elif defect == "root_path_change":
        root = r"C:\OtherTree"
    elif defect == "root_identity":
        root_identity = (77, 999)
    elif defect == "add":
        entries.append(Entry("new.txt", r"C:\Tree\new.txt", (77, 50), "file"))
    elif defect == "remove":
        entries.pop(rng.randrange(len(entries)))
    elif defect == "replace_identity":
        index = rng.randrange(len(entries))
        entry = entries[index]
        entries[index] = Entry(entry.relative, entry.canonical, (77, 500 + index), entry.kind)
    elif defect == "kind":
        index = rng.randrange(len(entries))
        entry = entries[index]
        entries[index] = Entry(
            entry.relative,
            entry.canonical,
            entry.identity,
            "file" if entry.kind == "dir" else "dir",
        )
    elif defect == "canonical":
        index = rng.randrange(len(entries))
        entry = entries[index]
        entries[index] = Entry(entry.relative, r"C:\Tree\wrong-name", entry.identity, entry.kind)
    elif defect == "case_only_relative":
        entries = [
            Entry(entry.relative.swapcase(), entry.canonical.swapcase(), entry.identity, entry.kind)
            for entry in entries
        ]
    elif defect == "reorder":
        rng.shuffle(entries)
    elif defect != "none":
        raise AssertionError(defect)

    return Manifest(root, root_identity, tuple(entries))


def run_model(cases: int) -> int:
    rng = random.Random(0xD1_202_2026)
    initial = base_manifest()
    checks = 0

    assert compare(initial, initial) == ()
    assert compare(initial, mutate(rng, initial, "root_identity"))
    assert compare(initial, mutate(rng, initial, "add"))
    assert compare(initial, mutate(rng, initial, "remove"))
    assert compare(initial, mutate(rng, initial, "replace_identity"))
    assert compare(initial, mutate(rng, initial, "kind"))
    assert compare(initial, mutate(rng, initial, "canonical"))
    assert compare(initial, mutate(rng, initial, "case_only_relative")) == ()
    assert compare(initial, mutate(rng, initial, "root_path_case_only")) == ()
    assert compare(initial, mutate(rng, initial, "reorder")) == ()
    checks += 10

    defects = (
        "none",
        "root_path_case_only",
        "root_path_change",
        "root_identity",
        "add",
        "remove",
        "replace_identity",
        "kind",
        "canonical",
        "case_only_relative",
        "reorder",
    )
    equivalent = {"none", "root_path_case_only", "case_only_relative", "reorder"}

    for _ in range(cases):
        defect = rng.choice(defects)
        fresh = mutate(rng, initial, defect)
        changes = compare(initial, fresh)
        assert (not changes) == (defect in equivalent), (defect, changes)
        checks += 1
        if changes:
            assert defect not in equivalent
            checks += 1
        else:
            assert defect in equivalent
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
    revalidation = read(
        root,
        "src/FileOp.Core/Operations/DirectoryOperationTreeManifestRevalidation.cs",
    )
    tests = read(
        root,
        "tests/FileOp.Windows.Tests/DirectoryOperationTreeManifestRevalidationTests.cs",
    )
    gate = read(root, "tools/test-local.ps1")

    checks = 0
    checks += require(
        revalidation,
        "public enum DirectoryOperationTreeManifestChangeKind",
        "RootCanonicalPathChanged",
        "RootIdentityChanged",
        "EntryAdded",
        "EntryRemoved",
        "EntryIdentityChanged",
        "EntryKindChanged",
        "EntryCanonicalPathChanged",
        "public bool EvidenceStillMatches => Changes.Count == 0",
        "public bool GrantsMutationAuthority => false",
        "public bool GrantsCopyAuthority => false",
        "public bool GrantsCreateAuthority => false",
        "public bool GrantsDeleteAuthority => false",
        "StringComparer.OrdinalIgnoreCase",
        "initial.RootIdentity != fresh.RootIdentity",
        "initial.Entries.ToDictionary(",
        "fresh.Entries.ToDictionary(",
        "initialEntry.Identity != freshEntry.Identity",
        "initialEntry.Kind != freshEntry.Kind",
        "initialEntry.CanonicalPath, freshEntry.CanonicalPath",
        "!initialByPath.ContainsKey(freshEntry.RelativePath)",
        "Array.AsReadOnly(changes.ToArray())",
    )
    checks += forbid(
        revalidation,
        "Directory.Enumerate",
        "Directory.GetFiles",
        "Directory.GetDirectories",
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
        "EquivalentFreshManifestHasNoChangesAndNoAuthority",
        "RootIdentityChangeFailsClosed",
        "RootCanonicalPathChangeFailsClosed",
        "AddedEntryFailsClosed",
        "RemovedEntryFailsClosed",
        "ReplacedObjectIdentityFailsClosed",
        "FileDirectoryKindChangeFailsClosed",
        "CurrentCaseInsensitiveNamespaceModelAcceptsCaseOnlyPathSpellingChange",
        "MultipleChangesAreReportedDeterministicallyWithoutAuthority",
        "Assert.IsFalse(result.GrantsMutationAuthority)",
    )
    checks += require(
        gate,
        "verify_directory_operation_tree_manifest.py --repo-root $repoRoot --cases 50000",
        "verify_directory_operation_tree_manifest_revalidation.py --repo-root $repoRoot --cases 50000",
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
    print(
        f"PASS directory tree manifest revalidation model: {model_checks:,} checks across {args.cases:,} randomized cases"
    )
    if not args.model_only:
        source_checks = check_repository(args.repo_root.resolve())
        print(f"PASS directory tree manifest revalidation source contract: {source_checks} checks")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
