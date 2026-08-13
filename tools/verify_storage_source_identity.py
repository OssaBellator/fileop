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
    return ntpath.normpath(path.replace("/", "\\")).casefold()


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


def post_elevation_refresh(previous: Source, current: Source) -> bool:
    return (
        root_cache_key(previous) == root_cache_key(current)
        and identity_key(previous) is not None
        and identity_key(previous) == identity_key(current)
    )


def track_native(
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
        changed = tracked_session != session or tracked_identity != identity
        tracked_session = session
        tracked_identity = identity
        if busy:
            return generation, False, tracked_session, tracked_identity
        if changed or not active:
            generation += 1
        return generation, True, tracked_session, tracked_identity
    if mode != "Native":
        return generation, False, None, None
    return generation, active, tracked_session, tracked_identity


def track_fallback(
    generation: int,
    active: bool,
    mode: str,
    ready: bool,
) -> tuple[int, bool]:
    if mode == "Fallback" and ready:
        return (generation, True) if active else (generation + 1, True)
    return (generation, False) if mode != "Fallback" else (generation, active)


def run_model(cases: int) -> int:
    rng = random.Random(0x154CACE)
    checks = 0

    for index in range(cases):
        drive = chr(ord("C") + rng.randrange(4))
        root = (
            f"{drive}:\\"
            if rng.random() < 0.5
            else f"{drive}:\\Users\\Example{index % 37}"
        )
        other_root = f"{chr(ord('G') + rng.randrange(4))}:\\"
        native_identity = rng.getrandbits(64)
        replacement_identity = native_identity
        while replacement_identity == native_identity:
            replacement_identity = rng.getrandbits(64)
        native_generation = rng.randint(1, 1_000_000)
        fallback_generation = rng.randint(1, 1_000_000)

        native = Source("Native", root, native_identity, native_generation)
        comparisons = (
            (Source("Native", root.swapcase(), native_identity, native_generation), True),
            (Source("Native", root, replacement_identity, native_generation), False),
            (Source("Native", root, native_identity, native_generation + 1), False),
            (Source("Native", other_root, native_identity, native_generation), False),
            (Source("Fallback", root, fallback_generation=fallback_generation), False),
            (Source("Initializing", None), False),
        )
        for current, expected in comparisons:
            assert cache_is_current(native, current) is expected
            checks += 1

        fallback = Source("Fallback", root, fallback_generation=fallback_generation)
        assert cache_is_current(
            fallback,
            Source("Fallback", root.swapcase(), fallback_generation=fallback_generation),
        )
        assert not cache_is_current(
            fallback,
            Source("Fallback", root, fallback_generation=fallback_generation + 1),
        )
        checks += 2

        assert post_elevation_refresh(
            native,
            Source("Native", root.swapcase(), native_identity, native_generation),
        )
        assert not post_elevation_refresh(
            native,
            Source("Native", root, native_identity, native_generation + 1),
        )
        assert not post_elevation_refresh(
            native,
            Source("Native", other_root, native_identity, native_generation),
        )
        assert not post_elevation_refresh(native, fallback)
        checks += 4

        session = rng.randint(1, 1_000_000)
        native_state = (native_generation, True, session, native_identity)
        repeated = track_native(*native_state, "Native", False, session, native_identity)
        assert repeated == native_state
        busy = track_native(*repeated, "Native", True, session, native_identity)
        assert busy == (native_generation, False, session, native_identity)
        repeated_busy = track_native(*busy, "Native", True, session, native_identity)
        assert repeated_busy == busy
        recovered = track_native(
            *repeated_busy,
            "Native",
            False,
            session,
            native_identity,
        )
        assert recovered == (native_generation + 1, True, session, native_identity)
        replacement_session = session + 1_000_001
        new_session = track_native(
            *recovered,
            "Native",
            False,
            replacement_session,
            native_identity,
        )
        assert new_session == (
            native_generation + 2,
            True,
            replacement_session,
            native_identity,
        )
        new_identity = track_native(
            *new_session,
            "Native",
            False,
            replacement_session,
            replacement_identity,
        )
        assert new_identity == (
            native_generation + 3,
            True,
            replacement_session,
            replacement_identity,
        )
        left_native = track_native(*new_identity, "Fallback", False, None, None)
        assert left_native == (native_generation + 3, False, None, None)
        reentered = track_native(
            *left_native,
            "Native",
            False,
            replacement_session,
            replacement_identity,
        )
        assert reentered == (
            native_generation + 4,
            True,
            replacement_session,
            replacement_identity,
        )
        checks += 8

        fallback_state = (fallback_generation, True)
        assert track_fallback(*fallback_state, "Fallback", True) == fallback_state
        initializing = track_fallback(*fallback_state, "Initializing", False)
        assert initializing == (fallback_generation, False)
        rebuilt = track_fallback(*initializing, "Fallback", True)
        assert rebuilt == (fallback_generation + 1, True)
        native_mode = track_fallback(*rebuilt, "Native", False)
        assert native_mode == (fallback_generation + 1, False)
        assert track_fallback(*native_mode, "Fallback", True) == (
            fallback_generation + 2,
            True,
        )
        checks += 5

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
        ("engine", "_nativeStorageSourceGeneration", "native source generation"),
        ("engine", "ReferenceEquals(_trackedNativeStorageSession, nativeSession)", "native helper-session tracking"),
        ("engine", "_trackedNativeStorageVolumeIdentity != nativeVolume.VolumeIdentity", "native physical identity tracking"),
        ("engine", "Volatile.Write(ref _nativeStorageSourceActive, 0);", "native maintenance inactive edge"),
        ("engine", "var wasActive = Interlocked.Exchange(ref _nativeStorageSourceActive, 1);", "native recovery edge"),
        ("engine", "sessionChanged || identityChanged || wasActive == 0", "native recovery/source-change advance"),
        ("engine", "Interlocked.Increment(ref _nativeStorageSourceGeneration)", "native generation advance"),
        ("engine", "Interlocked.Exchange(ref _fallbackStorageSourceActive, 1)", "single fallback publication edge"),
        ("lifecycle", "_primaryVolume = preparation.Volume", "native primary assignment"),
        ("lifecycle", "_fallbackReady = true;", "fallback completed snapshot marker"),
        ("window", "InitializeStorageSourceIdentityTracking", "MainWindow identity initialization"),
        ("window", "if (state.IsBusy)", "all-mode busy publication barrier"),
        ("window", "previousSourceIdentityKey = Interlocked.Exchange(", "atomic identity-token handoff"),
        ("window", "_searchEngine.StateChanged += StorageSourceIdentity_StateChanged;", "identity pre-handler"),
        ("window", "Interlocked.Exchange(ref _storageSourceKey, null);", "folder cache invalidation"),
        ("window", "Interlocked.Exchange(ref _filesSourceKey, null);", "Files cache invalidation"),
        ("window", "Interlocked.Exchange(ref _storageTypesSourceKey, null);", "Types cache invalidation"),
        ("window", "Interlocked.Exchange(ref _storageHistorySourceKey, null);", "History cache invalidation"),
        ("window", "Interlocked.Exchange(ref _storageOptimizationSourceKey, null);", "Optimize cache invalidation"),
        ("window", "Interlocked.Exchange(ref _storageOptimizationAnalysis, null);", "same-size source reference invalidation"),
        ("window", "Interlocked.Exchange(ref _storageKnownLocationReview, null);", "known-location source reference invalidation"),
        ("window", "Interlocked.Increment(ref _storageGeneration);", "folder in-flight invalidation"),
        ("window", "Interlocked.Increment(ref _storageTypeGeneration);", "Types in-flight invalidation"),
        ("window", "Interlocked.Increment(ref _storageHistoryGeneration);", "History in-flight invalidation"),
        ("window", "Interlocked.Increment(ref _storageOptimizationGeneration);", "Optimize in-flight invalidation"),
        ("main", "CreateStorageSourceKey", "shared root cache key"),
        ("main", "var storageIdentityBeforeElevation = _searchEngine.StorageSourceIdentityKey;", "pre-elevation backing identity capture"),
        ("main", "var currentSourceIdentityKey = _searchEngine.StorageSourceIdentityKey;", "post-elevation backing identity capture"),
        ("main", "storageIdentityBeforeElevation", "post-elevation identity comparison"),
        ("files", "_filesSourceKey", "Files shared root-key consumer"),
        ("types", "_storageTypesSourceKey", "Types shared root-key consumer"),
        ("types", "if (string.Equals(sourceKey, _storageTypesSourceKey", "Types key-driven reload contract"),
        ("history", "_storageHistorySourceKey", "History shared root-key consumer"),
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
        raise AssertionError("native identity must be assigned before Native publication")
    checks += 1

    build_start = lifecycle.index("private async Task BuildFallbackAsync")
    initializing_at = lifecycle.index("DesktopSearchMode.Initializing", build_start)
    fallback_ready_at = lifecycle.index("_fallbackReady = true;", initializing_at)
    fallback_publish_at = lifecycle.index("DesktopSearchMode.Fallback", fallback_ready_at)
    if not initializing_at < fallback_ready_at < fallback_publish_at:
        raise AssertionError("fallback rebuild publication ordering changed")
    checks += 1

    app_identity_at = text["app"].index("window.InitializeStorageSourceIdentityTracking();")
    app_files_at = text["app"].index("window.InitializeFilesFeature();")
    if app_identity_at >= app_files_at:
        raise AssertionError("identity tracking must initialize before Files subscribes")
    checks += 1

    window = text["window"]
    busy_at = window.index("if (state.IsBusy)")
    key_at = window.index("var sourceIdentityKey = _searchEngine.StorageSourceIdentityKey;")
    if busy_at >= key_at:
        raise AssertionError("all-mode busy invalidation must precede identity equality return")
    checks += 1

    remove_at = window.index("_searchEngine.StateChanged -= SearchEngine_StateChanged;")
    identity_at = window.index("_searchEngine.StateChanged += StorageSourceIdentity_StateChanged;")
    restore_at = window.index("_searchEngine.StateChanged += SearchEngine_StateChanged;")
    if not remove_at < identity_at < restore_at:
        raise AssertionError("identity handler must precede the existing MainWindow handler")
    checks += 1

    main = text["main"]
    elevation_start = main.index("private async void EnableFastIndexButton_Click")
    before_identity_at = main.index("var storageIdentityBeforeElevation", elevation_start)
    elevate_at = main.index("await _searchEngine.TryElevateAsync", before_identity_at)
    after_identity_at = main.index("var currentSourceIdentityKey", elevate_at)
    identity_compare_at = main.index("storageIdentityBeforeElevation", after_identity_at)
    if not before_identity_at < elevate_at < after_identity_at < identity_compare_at:
        raise AssertionError("post-elevation refresh must bind both pre/post backing-source identities")
    checks += 1

    checks += forbid(text["protocol"], "StorageSourceIdentity", "new protocol surface")
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
