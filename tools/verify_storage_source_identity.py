#!/usr/bin/env python3
"""Portable model/source verifier for MainWindow storage backing-source identity."""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import ntpath
from pathlib import Path
import random


@dataclass(frozen=True)
class Source:
    mode: str
    root: str | None
    native_identity: int | None = None
    native_generation: int = 0
    fallback_generation: int = 0


def normalize_windows_path(path: str) -> str:
    normalized = ntpath.normpath(path.replace("/", "\\"))
    return normalized.casefold()


def identity_key(source: Source) -> str | None:
    if (
        source.mode == "Native"
        and source.native_identity is not None
        and source.native_generation > 0
    ):
        return f"native:{source.native_identity:016X}:{source.native_generation}"
    if source.mode == "Fallback" and source.fallback_generation > 0:
        return f"fallback:{source.fallback_generation}"
    return None


def root_cache_key(source: Source) -> str | None:
    if source.root is None:
        return None
    return f"{source.mode}:{normalize_windows_path(source.root)}"


def cache_is_current(previous: Source, current: Source) -> bool:
    return (
        identity_key(previous) is not None
        and identity_key(previous) == identity_key(current)
        and root_cache_key(previous) == root_cache_key(current)
    )


def track_native_generation(
    generation: int,
    active: bool,
    tracked_session: int | None,
    tracked_identity: int | None,
    mode: str,
    busy: bool,
    session: int | None,
    identity: int | None,
) -> tuple[int, bool, int | None, int | None]:
    if mode == "Native" and session is not None and identity is not None:
        session_changed = tracked_session != session
        identity_changed = tracked_identity != identity
        if session_changed or identity_changed:
            return generation + 1, not busy, session, identity
        if busy:
            if active:
                generation += 1
            return generation, False, tracked_session, tracked_identity
        return generation, True, tracked_session, tracked_identity
    if mode != "Native":
        return generation, False, None, None
    return generation, active, tracked_session, tracked_identity


def track_fallback_generation(
    generation: int,
    active: bool,
    mode: str,
    fallback_ready: bool,
) -> tuple[int, bool]:
    if mode == "Fallback" and fallback_ready:
        if not active:
            generation += 1
            active = True
        return generation, active
    if mode != "Fallback":
        active = False
    return generation, active


def run_model(cases: int) -> int:
    rng = random.Random(0x154CACE)
    checks = 0

    for index in range(cases):
        drive = chr(ord("C") + rng.randrange(4))
        if rng.random() < 0.5:
            root = f"{drive}:\\"
        else:
            root = f"{drive}:\\Users\\Example{index % 37}"
        root_case_variant = root.swapcase()
        other_root = f"{chr(ord('G') + rng.randrange(4))}:\\"

        native_identity = rng.getrandbits(64)
        replacement_identity = native_identity
        while replacement_identity == native_identity:
            replacement_identity = rng.getrandbits(64)
        native_generation = rng.randint(1, 1_000_000)
        fallback_generation = rng.randint(1, 1_000_000)

        native = Source(
            "Native",
            root,
            native_identity=native_identity,
            native_generation=native_generation,
        )
        native_same = Source(
            "Native",
            root_case_variant,
            native_identity=native_identity,
            native_generation=native_generation,
        )
        native_replaced = Source(
            "Native",
            root,
            native_identity=replacement_identity,
            native_generation=native_generation,
        )
        native_rebuilt = Source(
            "Native",
            root,
            native_identity=native_identity,
            native_generation=native_generation + 1,
        )
        native_other_root = Source(
            "Native",
            other_root,
            native_identity=native_identity,
            native_generation=native_generation,
        )
        fallback = Source("Fallback", root, fallback_generation=fallback_generation)
        fallback_same = Source(
            "Fallback",
            root_case_variant,
            fallback_generation=fallback_generation,
        )
        fallback_rebuilt = Source(
            "Fallback",
            root,
            fallback_generation=fallback_generation + 1,
        )
        unavailable = Source("Initializing", None)

        assert cache_is_current(native, native_same)
        assert not cache_is_current(native, native_replaced)
        assert not cache_is_current(native, native_rebuilt)
        assert not cache_is_current(native, native_other_root)
        assert cache_is_current(fallback, fallback_same)
        assert not cache_is_current(fallback, fallback_rebuilt)
        assert not cache_is_current(native, fallback)
        assert not cache_is_current(native, unavailable)
        checks += 8

        session = rng.randint(1, 1_000_000)
        native_state = (
            native_generation,
            True,
            session,
            native_identity,
        )
        repeated_native = track_native_generation(
            *native_state,
            "Native",
            False,
            session,
            native_identity,
        )
        assert repeated_native == native_state
        checks += 1

        busy_native = track_native_generation(
            *repeated_native,
            "Native",
            True,
            session,
            native_identity,
        )
        assert busy_native == (
            native_generation + 1,
            False,
            session,
            native_identity,
        )
        checks += 1

        repeated_busy = track_native_generation(
            *busy_native,
            "Native",
            True,
            session,
            native_identity,
        )
        assert repeated_busy == busy_native
        checks += 1

        recovered_native = track_native_generation(
            *repeated_busy,
            "Native",
            False,
            session,
            native_identity,
        )
        assert recovered_native == (
            native_generation + 1,
            True,
            session,
            native_identity,
        )
        checks += 1

        replacement_session = session + 1_000_001
        replaced_session = track_native_generation(
            *recovered_native,
            "Native",
            False,
            replacement_session,
            native_identity,
        )
        assert replaced_session == (
            native_generation + 2,
            True,
            replacement_session,
            native_identity,
        )
        checks += 1

        replaced_identity = track_native_generation(
            *replaced_session,
            "Native",
            False,
            replacement_session,
            replacement_identity,
        )
        assert replaced_identity == (
            native_generation + 3,
            True,
            replacement_session,
            replacement_identity,
        )
        checks += 1

        left_native = track_native_generation(
            *replaced_identity,
            "Fallback",
            False,
            None,
            None,
        )
        assert left_native == (native_generation + 3, False, None, None)
        checks += 1

        reentered_native = track_native_generation(
            *left_native,
            "Native",
            False,
            replacement_session,
            replacement_identity,
        )
        assert reentered_native == (
            native_generation + 4,
            True,
            replacement_session,
            replacement_identity,
        )
        checks += 1

        generation = fallback_generation
        active = True
        repeated_fallback = track_fallback_generation(
            generation,
            active,
            "Fallback",
            True,
        )
        assert repeated_fallback == (generation, True)
        checks += 1

        initializing_fallback = track_fallback_generation(
            *repeated_fallback,
            "Initializing",
            False,
        )
        assert initializing_fallback == (generation, False)
        checks += 1

        rebuilt_fallback = track_fallback_generation(
            *initializing_fallback,
            "Fallback",
            True,
        )
        assert rebuilt_fallback == (generation + 1, True)
        checks += 1

        native_after_fallback = track_fallback_generation(
            *rebuilt_fallback,
            "Native",
            False,
        )
        assert native_after_fallback == (generation + 1, False)
        checks += 1

        next_fallback = track_fallback_generation(
            *native_after_fallback,
            "Fallback",
            True,
        )
        assert next_fallback == (generation + 2, True)
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
    paths = {
        "engine": "src/FileOp.App/DesktopSearchEngine.StorageSourceIdentity.cs",
        "lifecycle": "src/FileOp.App/DesktopSearchEngine.cs",
        "window": "src/FileOp.App/MainWindow.StorageSourceIdentity.cs",
        "app": "src/FileOp.App/App.xaml.cs",
        "main": "src/FileOp.App/MainWindow.xaml.cs",
        "files": "src/FileOp.App/MainWindow.Files.cs",
        "types": "src/FileOp.App/MainWindow.StorageTypes.cs",
        "history": "src/FileOp.App/MainWindow.StorageHistory.cs",
        "optimize": "src/FileOp.App/MainWindow.StorageOptimization.cs",
        "gate": "tools/test-local.ps1",
        "protocol": "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs",
    }
    text = {name: (root / path).read_text(encoding="utf-8") for name, path in paths.items()}
    checks = 0

    required = (
        ("engine", "public DesktopSearchEngine()", "engine identity tracker constructor"),
        ("engine", "StateChanged += TrackStorageSourceIdentity;", "engine-first state tracking"),
        ("engine", 'native:{volume.VolumeIdentity:X16}:{Volatile.Read(ref _nativeStorageSourceGeneration)}', "native physical/generation token"),
        ("engine", 'fallback:{Volatile.Read(ref _fallbackStorageSourceGeneration)}', "fallback generation token"),
        ("engine", "ReferenceEquals(_trackedNativeStorageSession, nativeSession)", "native helper-session tracking"),
        ("engine", "_trackedNativeStorageVolumeIdentity != nativeVolume.VolumeIdentity", "native physical identity tracking"),
        ("engine", "Interlocked.Exchange(ref _nativeStorageSourceActive, 0) == 1", "single native maintenance edge"),
        ("engine", "Interlocked.Increment(ref _nativeStorageSourceGeneration)", "native generation advance"),
        ("engine", "Interlocked.Exchange(ref _fallbackStorageSourceActive, 1)", "single fallback publication edge"),
        ("engine", "Interlocked.Increment(ref _fallbackStorageSourceGeneration)", "fallback generation advance"),
        ("engine", "Volatile.Write(ref _fallbackStorageSourceActive, 0)", "fallback edge reset"),
        ("lifecycle", "_primaryVolume = preparation.Volume", "native primary assignment"),
        ("lifecycle", "DesktopSearchMode.Initializing", "fallback pre-publication state"),
        ("lifecycle", "_fallbackReady = true;", "fallback completed snapshot marker"),
        ("window", "InitializeStorageSourceIdentityTracking", "MainWindow identity initialization"),
        ("window", "_searchEngine.StateChanged -= SearchEngine_StateChanged;", "pre-handler ordering remove"),
        ("window", "_searchEngine.StateChanged += StorageSourceIdentity_StateChanged;", "identity pre-handler"),
        ("window", "_searchEngine.StateChanged += SearchEngine_StateChanged;", "base handler restore"),
        ("window", "_storageSourceKey = null;", "folder cache invalidation"),
        ("window", "_filesSourceKey = null;", "Files cache invalidation"),
        ("window", "_storageTypesSourceKey = null;", "Types cache invalidation"),
        ("window", "_storageHistorySourceKey = null;", "History cache invalidation"),
        ("window", "_storageOptimizationSourceKey = null;", "Optimize cache invalidation"),
        ("window", "_storageOptimizationAnalysis = null;", "same-size source reference invalidation"),
        ("window", "_storageKnownLocationReview = null;", "known-location source reference invalidation"),
        ("window", "Interlocked.Increment(ref _storageGeneration);", "folder in-flight invalidation"),
        ("window", "Interlocked.Increment(ref _storageTypeGeneration);", "Types in-flight invalidation"),
        ("window", "Interlocked.Increment(ref _storageHistoryGeneration);", "History in-flight invalidation"),
        ("window", "Interlocked.Increment(ref _storageOptimizationGeneration);", "Optimize in-flight invalidation"),
        ("main", "CreateStorageSourceKey", "shared root cache key"),
        ("files", "_filesSourceKey", "Files shared root-key consumer"),
        ("types", "_storageTypesSourceKey", "Types shared root-key consumer"),
        ("history", "_storageHistorySourceKey", "History shared root-key consumer"),
        ("optimize", "_storageOptimizationSourceKey", "Optimize shared root-key consumer"),
        ("optimize", "ReferenceEquals(analysis, _storageOptimizationAnalysis)", "same-size stale-publication guard"),
        ("gate", "verify_storage_source_identity.py --repo-root $repoRoot --cases 50000", "offline gate wiring"),
        ("protocol", "public const int CurrentVersion = 8;", "protocol v8"),
    )
    for source, needle, label in required:
        checks += require(text[source], needle, label)

    checks += forbid(
        text["window"],
        "Interlocked.Increment(ref _performanceDiskIoGeneration);",
        "indexed-source invalidation of system-wide Disk I/O capture",
    )

    lifecycle = text["lifecycle"]
    apply_start = lifecycle.index("private void ApplyNativePreparation")
    primary_at = lifecycle.index("_primaryVolume = preparation.Volume", apply_start)
    native_publish_at = lifecycle.index("DesktopSearchMode.Native", primary_at)
    if primary_at >= native_publish_at:
        raise AssertionError("native VolumeIdentity must be assigned before Native state publication")
    checks += 1

    build_start = lifecycle.index("private async Task BuildFallbackAsync")
    initializing_at = lifecycle.index("DesktopSearchMode.Initializing", build_start)
    fallback_ready_at = lifecycle.index("_fallbackReady = true;", initializing_at)
    fallback_publish_at = lifecycle.index("DesktopSearchMode.Fallback", fallback_ready_at)
    if not initializing_at < fallback_ready_at < fallback_publish_at:
        raise AssertionError("fallback rebuild must publish Initializing before one completed Fallback snapshot")
    checks += 1

    app_identity_at = text["app"].index("window.InitializeStorageSourceIdentityTracking();")
    app_files_at = text["app"].index("window.InitializeFilesFeature();")
    if app_identity_at >= app_files_at:
        raise AssertionError("storage source identity tracking must initialize before Files subscribes")
    checks += 1

    window = text["window"]
    remove_at = window.index("_searchEngine.StateChanged -= SearchEngine_StateChanged;")
    identity_at = window.index("_searchEngine.StateChanged += StorageSourceIdentity_StateChanged;")
    restore_at = window.index("_searchEngine.StateChanged += SearchEngine_StateChanged;")
    if not remove_at < identity_at < restore_at:
        raise AssertionError("source identity handler must run before the existing MainWindow source handler")
    checks += 1

    if "StorageSourceIdentity" in text["protocol"]:
        raise AssertionError("storage source identity must not add an Indexer protocol operation")
    checks += 1
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
        "PASS: storage source identity verified with "
        f"{model_checks:,} model checks across {args.cases:,} randomized states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
