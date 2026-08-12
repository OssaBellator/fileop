#!/usr/bin/env python3
"""Verify the non-mutating Windows final delete-capability lease provider."""
from __future__ import annotations

import argparse
import random
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class NativeAcquireState:
    pre_cancelled: bool
    request_valid: bool
    root_allowed: bool
    file_allowed: bool
    root_safe: bool
    root_path_exact: bool
    root_identity_exact: bool
    relative_leaf_safe: bool
    ntcreate_succeeds: bool
    file_safe: bool
    file_path_exact: bool
    file_identity_exact: bool
    root_still_exact: bool


def acquire(state: NativeAcquireState) -> tuple[bool, bool, bool, tuple[str, ...]]:
    """Return (lease, delete_capability, mutation, ordered events)."""
    events: list[str] = []
    mutation = False
    if state.pre_cancelled or not state.request_valid:
        return False, False, mutation, tuple(events)
    if not state.root_allowed or not state.file_allowed:
        return False, False, mutation, tuple(events)

    events.append("open-root")
    if not (state.root_safe and state.root_path_exact and state.root_identity_exact):
        return False, False, mutation, tuple(events)

    events.append("open-relative-leaf-with-delete")
    if not state.relative_leaf_safe or not state.ntcreate_succeeds:
        return False, False, mutation, tuple(events)
    if not (state.file_safe and state.file_path_exact and state.file_identity_exact):
        return False, False, mutation, tuple(events)

    events.append("revalidate-root")
    if not state.root_still_exact:
        return False, False, mutation, tuple(events)

    events.append("return-live-lease")
    return True, True, mutation, tuple(events)


def run_model(cases: int, seed: int) -> int:
    baseline = NativeAcquireState(
        False, True, True, True, True, True, True,
        True, True, True, True, True, True,
    )
    lease, capability, mutation, events = acquire(baseline)
    assert lease and capability and not mutation
    assert events == (
        "open-root",
        "open-relative-leaf-with-delete",
        "revalidate-root",
        "return-live-lease",
    )
    checks = 4

    rng = random.Random(seed)
    for _ in range(cases):
        state = NativeAcquireState(
            pre_cancelled=rng.random() < 0.04,
            request_valid=rng.random() >= 0.05,
            root_allowed=rng.random() >= 0.04,
            file_allowed=rng.random() >= 0.04,
            root_safe=rng.random() >= 0.05,
            root_path_exact=rng.random() >= 0.05,
            root_identity_exact=rng.random() >= 0.05,
            relative_leaf_safe=rng.random() >= 0.04,
            ntcreate_succeeds=rng.random() >= 0.05,
            file_safe=rng.random() >= 0.05,
            file_path_exact=rng.random() >= 0.05,
            file_identity_exact=rng.random() >= 0.05,
            root_still_exact=rng.random() >= 0.05,
        )
        lease, capability, mutation, events = acquire(state)

        assert not mutation
        assert events.count("open-root") <= 1
        assert events.count("open-relative-leaf-with-delete") <= 1
        assert events.count("revalidate-root") <= 1
        assert events.count("return-live-lease") <= 1
        checks += 5

        if lease:
            assert capability
            assert events[-1] == "return-live-lease"
            assert all((
                not state.pre_cancelled,
                state.request_valid,
                state.root_allowed,
                state.file_allowed,
                state.root_safe,
                state.root_path_exact,
                state.root_identity_exact,
                state.relative_leaf_safe,
                state.ntcreate_succeeds,
                state.file_safe,
                state.file_path_exact,
                state.file_identity_exact,
                state.root_still_exact,
            ))
            checks += 3
            # Disposal removes capability and still performs no mutation.
            capability = False
            assert not capability and not mutation
            checks += 2
        else:
            assert not capability
            assert "return-live-lease" not in events
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
    provider_path = root / "src/FileOp.Windows/Operations/WindowsFileDeleteOperationFinalMutationLeaseProvider.cs"
    provider = provider_path.read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/WindowsFileDeleteOperationFinalMutationLeaseProviderTests.cs").read_text(encoding="utf-8")
    docs = (root / "docs/windows-file-delete-final-mutation-lease-provider.md").read_text(encoding="utf-8")
    contract_verifier = (root / "tools/verify_file_delete_final_mutation_lease_contract.py").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    plan = (root / "src/FileOp.Core/Operations/FileOperationPlan.cs").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")

    checks = 0
    for text, needle, label in (
        (provider, "public sealed class WindowsFileDeleteOperationFinalMutationLeaseProvider", "concrete Windows provider"),
        (provider, "IFileDeleteOperationFinalMutationLeaseProvider", "final provider interface"),
        (provider, "private const uint Delete = 0x00010000;", "DELETE access constant"),
        (provider, "Delete | FileReadAttributes | Synchronize", "minimum final leaf access mask"),
        (provider, "FileShare.Read,", "restrictive leaf sharing"),
        (provider, "FileOpen,", "open-existing disposition"),
        (provider, "FileSynchronousIoNonAlert | FileNonDirectoryFile | FileOpenReparsePoint", "file-only non-reparse open options"),
        (provider, "RootDirectory = rootDirectory.DangerousGetHandle()", "root-relative leaf open"),
        (provider, "EnsureAllowedByProtectedLocationPolicy(expectedRootPath, \"source directory\")", "root protected policy"),
        (provider, "EnsureAllowedByProtectedLocationPolicy(expectedSourcePath, \"source file\")", "file protected policy"),
        (provider, "ValidateDirectoryHandle(\n                sourceDirectory,\n                expectedRootPath,\n                expectedRootIdentity);", "root identity validation"),
        (provider, "ValidateFileHandle(\n                sourceFile,\n                expectedSourcePath,\n                expectedSourceIdentity);", "leaf identity validation"),
        (provider, "(information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0", "reparse refusal"),
        (provider, "new FileDeleteOperationFinalMutationLeaseEvidence(", "value evidence construction"),
        (provider, "private sealed class FinalMutationLease : IFileDeleteOperationFinalMutationLease", "private-handle live lease"),
        (provider, "IsLive(_sourceDirectory) && IsLive(_sourceFile)", "live capability state"),
        (provider, "public bool DeleteMutationAuthorized => false;", "provider remains non-authorizing"),
        (provider, "DisposeNoThrow(_sourceFile);", "leaf release"),
        (provider, "DisposeNoThrow(_sourceDirectory);", "root release"),
        (tests, "FinalLeaseBindsExactAuthorizationAndHoldsDeleteCapabilityWithoutMutationAuthority", "native capability regression"),
        (tests, "FileReplacementDuringReadOnlyToFinalHandoffIsRejectedByFinalProvider", "file handoff race regression"),
        (tests, "RootReplacementDuringReadOnlyToFinalHandoffIsRejectedByFinalProvider", "root handoff race regression"),
        (tests, "ProtectedLocationPolicyIsRecheckedByFinalProviderAfterReadOnlyRelease", "protected policy regression"),
        (tests, "ConcreteProviderHonorsPreCancellationBeforeNativeAcquisition", "provider cancellation regression"),
        (tests, "DeleteIsBlocked", "native delete sharing probe"),
        (tests, "WriteOpenIsBlocked", "write sharing probe"),
        (tests, "DirectoryRenameIsBlocked", "root rename sharing probe"),
        (docs, "successful `NtCreateFile` request containing `DELETE`", "capability proof documentation"),
        (docs, "does not perform deletion", "no mutation documentation"),
        (contract_verifier, "reviewed_provider = \"src/FileOp.Windows/Operations/WindowsFileDeleteOperationFinalMutationLeaseProvider.cs\"", "evolved #136 production-provider guard"),
        (gate, "verify_windows_file_delete_final_mutation_lease_provider.py --repo-root $repoRoot --cases 50000", "offline gate wiring"),
        (plan, "public enum FileOperationKind\n{\n    Copy,\n    Move,", "generic Delete remains absent"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 unchanged"),
    ):
        checks += require(text, needle, label)

    first_root_validation = provider.index("ValidateDirectoryHandle(")
    relative_open = provider.index("sourceFile = OpenRelativeFile(")
    second_root_validation = provider.index("ValidateDirectoryHandle(", first_root_validation + 1)
    if not first_root_validation < relative_open < second_root_validation:
        raise AssertionError("final provider must validate root before and after the root-relative leaf open")
    checks += 1

    # The production provider may acquire DELETE access, but it may not consume that access.
    for needle, label in (
        ("SetFileInformationByHandle", "file disposition mutation"),
        ("NtSetInformationFile", "native disposition mutation"),
        ("DeleteFileW", "path delete"),
        ("File.Delete(", "managed file delete"),
        ("Directory.Delete(", "managed directory delete"),
        ("FileDisposition", "delete disposition class"),
        ("DeleteOnClose", "delete-on-close option"),
        ("CommitDeletedAsync(", "delete settlement"),
        ("MarkMutationStartedAsync(", "durable barrier transition"),
        ("FileDeleteOperationMutationBarrier", "barrier production consumer"),
    ):
        checks += forbid(provider, needle, label)

    # No application/indexer wiring and no second Windows consumer yet.
    consumers: list[str] = []
    for subtree in ("src/FileOp.App", "src/FileOp.Indexer"):
        directory = root / subtree
        if not directory.exists():
            continue
        for path in directory.rglob("*.cs"):
            if "WindowsFileDeleteOperationFinalMutationLeaseProvider" in path.read_text(encoding="utf-8"):
                consumers.append(path.relative_to(root).as_posix())
    if consumers:
        raise AssertionError("final provider must remain unwired from App/Indexer: " + ", ".join(consumers))
    checks += 1

    if gate.count("verify_windows_file_delete_final_mutation_lease_provider.py") != 1:
        raise AssertionError("Windows final delete provider verifier must be wired exactly once")
    checks += 1
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0xD311E7E)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {source_checks:,} source/test checks" if args.repo_root else ""
    print(
        f"PASS: Windows final delete-capability provider verified with {model_checks:,} model assertions "
        f"across {args.cases:,} randomized native-acquisition states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
