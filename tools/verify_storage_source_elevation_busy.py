#!/usr/bin/env python3
"""Verify native cache epochs distinguish elevation busy from index maintenance."""

from __future__ import annotations

import argparse
from pathlib import Path
import random


def track_native(
    generation: int,
    active: bool,
    elevation_busy: bool,
    tracked_session: int | None,
    tracked_identity: int | None,
    tracked_can_elevate: bool,
    *,
    mode: str,
    busy: bool,
    can_elevate: bool,
    session: int | None,
    identity: int | None,
) -> tuple[int, bool, bool, int | None, int | None, bool]:
    if mode == "Native" and session is not None and identity is not None:
        previous_can_elevate = tracked_can_elevate
        session_changed = tracked_session != session
        identity_changed = tracked_identity != identity
        if session_changed or identity_changed:
            tracked_session = session
            tracked_identity = identity

        if busy:
            if elevation_busy or (
                not session_changed
                and not identity_changed
                and previous_can_elevate
            ):
                elevation_busy = True
            else:
                active = False
        else:
            was_elevation_busy = elevation_busy
            elevation_busy = False
            was_active = active
            active = True
            if session_changed or identity_changed or not was_active:
                if session_changed or identity_changed or not was_elevation_busy:
                    generation += 1

        tracked_can_elevate = can_elevate
    elif mode != "Native":
        active = False
        elevation_busy = False
        tracked_session = None
        tracked_identity = None
        tracked_can_elevate = False

    return (
        generation,
        active,
        elevation_busy,
        tracked_session,
        tracked_identity,
        tracked_can_elevate,
    )


def run_model(cases: int) -> int:
    rng = random.Random(0x155E1E7)
    checks = 0

    for _ in range(cases):
        generation = rng.randint(1, 1_000_000)
        session = rng.randint(1, 1_000_000)
        identity = rng.getrandbits(64)
        replacement_session = session + 1_000_001
        replacement_identity = identity
        while replacement_identity == identity:
            replacement_identity = rng.getrandbits(64)

        elevatable = (generation, True, False, session, identity, True)
        elevation_busy = track_native(
            *elevatable,
            mode="Native",
            busy=True,
            can_elevate=False,
            session=session,
            identity=identity,
        )
        assert elevation_busy == (
            generation,
            True,
            True,
            session,
            identity,
            False,
        )
        checks += 1

        repeated_elevation_busy = track_native(
            *elevation_busy,
            mode="Native",
            busy=True,
            can_elevate=False,
            session=session,
            identity=identity,
        )
        assert repeated_elevation_busy == elevation_busy
        checks += 1

        failed_elevation = track_native(
            *repeated_elevation_busy,
            mode="Native",
            busy=False,
            can_elevate=True,
            session=session,
            identity=identity,
        )
        assert failed_elevation == elevatable
        checks += 1

        successful_elevation = track_native(
            *elevation_busy,
            mode="Native",
            busy=False,
            can_elevate=False,
            session=replacement_session,
            identity=identity,
        )
        assert successful_elevation == (
            generation + 1,
            True,
            False,
            replacement_session,
            identity,
            False,
        )
        checks += 1

        replacement_volume = track_native(
            *elevation_busy,
            mode="Native",
            busy=False,
            can_elevate=False,
            session=replacement_session,
            identity=replacement_identity,
        )
        assert replacement_volume == (
            generation + 1,
            True,
            False,
            replacement_session,
            replacement_identity,
            False,
        )
        checks += 1

        maintenance_ready = (generation, True, False, session, identity, False)
        maintenance_busy = track_native(
            *maintenance_ready,
            mode="Native",
            busy=True,
            can_elevate=False,
            session=session,
            identity=identity,
        )
        assert maintenance_busy == (
            generation,
            False,
            False,
            session,
            identity,
            False,
        )
        checks += 1

        repeated_maintenance_busy = track_native(
            *maintenance_busy,
            mode="Native",
            busy=True,
            can_elevate=False,
            session=session,
            identity=identity,
        )
        assert repeated_maintenance_busy == maintenance_busy
        checks += 1

        recovered_maintenance = track_native(
            *repeated_maintenance_busy,
            mode="Native",
            busy=False,
            can_elevate=False,
            session=session,
            identity=identity,
        )
        assert recovered_maintenance == (
            generation + 1,
            True,
            False,
            session,
            identity,
            False,
        )
        checks += 1

        healthy_repeat = track_native(
            *maintenance_ready,
            mode="Native",
            busy=False,
            can_elevate=False,
            session=session,
            identity=identity,
        )
        assert healthy_repeat == maintenance_ready
        checks += 1

        left_native = track_native(
            *maintenance_ready,
            mode="Fallback",
            busy=False,
            can_elevate=False,
            session=None,
            identity=None,
        )
        assert left_native == (generation, False, False, None, None, False)
        checks += 1

    return checks


def require(text: str, needle: str, label: str) -> int:
    if needle not in text:
        raise AssertionError(f"missing {label}: {needle}")
    return 1


def check_repository(root: Path) -> int:
    engine = (root / "src/FileOp.App/DesktopSearchEngine.StorageSourceIdentity.cs").read_text(
        encoding="utf-8"
    )
    lifecycle = (root / "src/FileOp.App/DesktopSearchEngine.cs").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    checks = 0

    required_engine = (
        ("_nativeStorageSourceElevationBusy", "elevation busy marker"),
        ("Volatile.Read(ref _trackedNativeStorageCanElevate) == 1", "prior elevation capability"),
        ("!sessionChanged && !identityChanged && previousCanElevate", "elevation busy classification"),
        ("Interlocked.Exchange(ref _nativeStorageSourceElevationBusy, 0) == 1", "elevation busy recovery"),
        ("sessionChanged || identityChanged || wasActive == 0", "native recovery candidate"),
        ("sessionChanged || identityChanged || !elevationBusy", "failed-elevation generation suppression"),
        ("Volatile.Write(ref _trackedNativeStorageCanElevate, state.CanElevate ? 1 : 0)", "capability tracking"),
    )
    for needle, label in required_engine:
        checks += require(engine, needle, label)

    initialize_at = lifecycle.index("ApplyNativePreparation(preparation);")
    start_if_at = lifecycle.index("if (!preparation.CanElevate)", initialize_at)
    start_sync_at = lifecycle.index("StartBackgroundSync();", start_if_at)
    if not initialize_at < start_if_at < start_sync_at:
        raise AssertionError("initial native background sync must remain gated by !CanElevate")
    checks += 1

    elevation_start = lifecycle.index("public async Task<bool> TryElevateAsync")
    elevation_busy_at = lifecycle.index("IsBusy = true", elevation_start)
    elevation_gate_at = lifecycle.index("_searchOperationGate.WaitAsync", elevation_busy_at)
    if elevation_busy_at >= elevation_gate_at:
        raise AssertionError("native elevation busy publication must precede helper replacement")
    checks += 1

    background_start = lifecycle.index("private async Task BackgroundSyncAsync")
    elevation_required_at = lifecycle.index("IndexingServiceErrorCode.ElevationRequired", background_start)
    can_elevate_at = lifecycle.index("CanElevate = true", elevation_required_at)
    return_at = lifecycle.index("return;", can_elevate_at)
    busy_at = lifecycle.index("IndexingServiceErrorCode.Busy", return_at)
    if not elevation_required_at < can_elevate_at < return_at < busy_at:
        raise AssertionError("background sync must stop after publishing native CanElevate=true")
    checks += 1

    checks += require(
        lifecycle,
        "IndexingServiceErrorCode.SnapshotRequired",
        "same-session native snapshot maintenance path",
    )
    checks += require(
        gate,
        "verify_storage_source_elevation_busy.py --repo-root $repoRoot --cases 50000",
        "offline elevation-busy verifier wiring",
    )
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    args = parser.parse_args()
    if args.cases < 0:
        raise ValueError("--cases must be non-negative")

    model_checks = run_model(args.cases)
    repo_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {repo_checks:,} source checks" if args.repo_root else ""
    print(
        "PASS: storage source elevation-busy behavior verified with "
        f"{model_checks:,} model checks across {args.cases:,} randomized states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
