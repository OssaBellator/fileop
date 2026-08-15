#!/usr/bin/env python3
"""Portable model/source checks for the held Windows SQLite publication-quiescence lease."""
from __future__ import annotations

import argparse
import random
import sys
from pathlib import Path


def can_transfer_lease(
    *,
    local_gate: bool,
    maintenance_gate: bool,
    live_checkpoint: bool,
    shadow_checkpoint: bool,
    pools_cleared: bool,
    live_sidecars_absent: bool,
    shadow_sidecars_absent: bool,
) -> bool:
    return all(
        (
            local_gate,
            maintenance_gate,
            live_checkpoint,
            shadow_checkpoint,
            pools_cleared,
            live_sidecars_absent,
            shadow_sidecars_absent,
        )
    )


def can_cross_swap_barrier(*, lease_transferred: bool, disposed: bool) -> bool:
    return lease_transferred and not disposed


def releases_local_gate(*, lease_transferred: bool, provider_failed: bool, disposed: bool) -> bool:
    return (not lease_transferred and provider_failed) or (lease_transferred and disposed)


def releases_maintenance_gate(*, lease_transferred: bool, provider_failed: bool, disposed: bool) -> bool:
    return releases_local_gate(
        lease_transferred=lease_transferred,
        provider_failed=provider_failed,
        disposed=disposed,
    )


def run_model(cases: int, seed: int = 0x2092026) -> int:
    rng = random.Random(seed)
    checks = 0

    assert can_transfer_lease(
        local_gate=True,
        maintenance_gate=True,
        live_checkpoint=True,
        shadow_checkpoint=True,
        pools_cleared=True,
        live_sidecars_absent=True,
        shadow_sidecars_absent=True,
    )
    checks += 1

    for missing in range(7):
        requirements = [True] * 7
        requirements[missing] = False
        assert not can_transfer_lease(
            local_gate=requirements[0],
            maintenance_gate=requirements[1],
            live_checkpoint=requirements[2],
            shadow_checkpoint=requirements[3],
            pools_cleared=requirements[4],
            live_sidecars_absent=requirements[5],
            shadow_sidecars_absent=requirements[6],
        )
        checks += 1

    for _ in range(cases):
        requirements = [bool(rng.getrandbits(1)) for _ in range(7)]
        transferred = can_transfer_lease(
            local_gate=requirements[0],
            maintenance_gate=requirements[1],
            live_checkpoint=requirements[2],
            shadow_checkpoint=requirements[3],
            pools_cleared=requirements[4],
            live_sidecars_absent=requirements[5],
            shadow_sidecars_absent=requirements[6],
        )
        assert transferred == all(requirements)
        checks += 1

        disposed = bool(rng.getrandbits(1))
        assert can_cross_swap_barrier(
            lease_transferred=transferred,
            disposed=disposed,
        ) == (transferred and not disposed)
        checks += 1

        provider_failed = not transferred
        local_released = releases_local_gate(
            lease_transferred=transferred,
            provider_failed=provider_failed,
            disposed=disposed,
        )
        maintenance_released = releases_maintenance_gate(
            lease_transferred=transferred,
            provider_failed=provider_failed,
            disposed=disposed,
        )
        assert local_released == maintenance_released
        assert local_released == (provider_failed or (transferred and disposed))
        checks += 2

        if transferred and not disposed:
            assert not local_released
            assert not maintenance_released
            checks += 2

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
    gate = read(root, "src/FileOp.Windows/IndexingService/IndexingVolumeFileGate.cs")
    lease = read(
        root,
        "src/FileOp.Windows/IndexingService/WindowsIndexRebuildPublicationQuiescenceLease.cs",
    )
    tests = read(
        root,
        "tests/FileOp.Windows.Tests/IndexRebuildPublicationQuiescenceLeaseTests.cs",
    )
    publication_verifier = read(root, "tools/verify_index_rebuild_publication_transaction.py")
    authoritative_step = read(root, "tools/verify_storage_busy_publication_barrier.py")
    local_gate = read(root, "tools/test-local.ps1")

    checks = 0
    checks += require(
        gate,
        "public string DatabasePath { get; }",
        "DatabasePath = Path.GetFullPath(databasePath);",
        "public SemaphoreSlim OperationGate { get; }",
        "OperationGate = new SemaphoreSlim(1, 1);",
        "substituting an unrelated semaphore",
        "TryAcquireMaintenance() => TryOpen(FileAccess.ReadWrite, FileShare.None)",
    )
    checks += require(
        lease,
        "WindowsIndexRebuildPublicationQuiescenceLease",
        "internal WindowsIndexRebuildPublicationQuiescenceLease(",
        "public bool GrantsFilesystemMutationAuthority => false",
        "ObjectDisposedException.ThrowIf(IsDisposed, this)",
        "IndexRebuildPublicationTransactionPolicy.MarkSwapStarted(",
        "Interlocked.Exchange(ref _disposed, 1)",
        "maintenanceLease?.Dispose()",
        "_operationGate.Release()",
        "WindowsIndexRebuildPublicationQuiescenceProvider",
        "var operationGate = publicationGate.OperationGate;",
        "operationGate.WaitAsync(0, cancellationToken)",
        "publicationGate.TryAcquireMaintenance()",
        "string.Equals(publicationGate.DatabasePath, livePath, StringComparison.OrdinalIgnoreCase)",
        "callers cannot substitute unrelated local-gate evidence",
        "Path.GetFullPath(liveDatabasePath)",
        "Path.GetFullPath(shadowDatabasePath)",
        "FileAttributes.ReparsePoint",
        "SqliteOpenMode.ReadWrite",
        "Cache = SqliteCacheMode.Private",
        "Pooling = false",
        'command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);"',
        "busy != 0 || remainingWalFrames != 0 || checkpointedFrames != 0",
        "SqliteConnection.ClearAllPools()",
        'databasePath + "-wal"',
        'databasePath + "-shm"',
        "LocalVolumeOperationGateHeld: true",
        "CrossProcessMaintenanceLeaseHeld: true",
        "LiveWalCheckpointComplete: true",
        "ShadowWalCheckpointComplete: true",
        "SqliteConnectionPoolsCleared: true",
        "LiveWalAndShmSidecarsQuiesced: true",
        "ShadowWalAndShmSidecarsQuiesced: true",
    )
    checks += forbid(
        lease,
        "File.Move(",
        "File.Replace(",
        "File.Delete(",
        "Directory.Move(",
        "Directory.Delete(",
        "SemaphoreSlim operationGate,\n        IndexingVolumeFileGate",
    )
    checks += require(
        tests,
        "ReadyWalDatabasesHoldBothGatesAndCrossExistingSwapBarrier",
        "DisposeReleasesBothGatesExactlyOnceAndLeaseCannotBeReused",
        "BusyLocalGateReturnsNullWithoutTakingProcessLease",
        "BusyProcessGateReturnsNullAndReleasesLocalGate",
        "PublicationGateForDifferentLiveDatabaseIsRejectedBeforeAcquisition",
        "NonSiblingShadowDatabaseIsRejectedBeforeAcquisition",
        "NonWalDatabaseFailsClosedAndReleasesBothGates",
        "Assert.IsFalse(lease.GrantsFilesystemMutationAuthority)",
        "Assert.IsNull(publicationGate.TryAcquireRead())",
        "publicationGate.OperationGate",
        "Assert.Throws<ObjectDisposedException>",
    )
    checks += forbid(
        tests,
        "Assert.ThrowsException",
    )
    checks += require(
        publication_verifier,
        "verify_index_publication_quiescence_lease",
        "run_quiescence_model",
        "check_quiescence_repository",
    )
    checks += require(
        authoritative_step,
        "verify_index_rebuild_publication_transaction",
        "run_publication_model",
        "check_publication_repository",
        "run_quiescence_model",
        "check_quiescence_repository",
    )
    checks += require(
        local_gate,
        "verify_storage_busy_publication_barrier.py --repo-root $repoRoot --cases 50000",
    )
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path, default=Path.cwd())
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--model-only", action="store_true")
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases)
    print(
        f"PASS index publication quiescence lease model: {model_checks} checks across {args.cases} randomized cases"
    )
    if not args.model_only:
        source_checks = check_repository(args.repo_root.resolve())
        print(f"PASS index publication quiescence lease source contract: {source_checks} checks")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ValueError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
