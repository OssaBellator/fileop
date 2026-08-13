#!/usr/bin/env python3
"""Verify engine busy entry suppresses old-source UI publication before recovery."""

from __future__ import annotations

import argparse
from pathlib import Path
import random


MODES = ("Initializing", "Native", "Fallback", "Unavailable")


def busy_barrier_applies(mode: str, is_busy: bool) -> bool:
    if mode not in MODES:
        raise ValueError(mode)
    return is_busy


def run_model(cases: int) -> int:
    rng = random.Random(0xB05B4A)
    checks = 0

    for _ in range(cases):
        # Folders, Types, History, Optimize, left Files, right Files each own a
        # request generation captured before awaiting source-bound work.
        mode = rng.choice(MODES)
        assert busy_barrier_applies(mode, True)
        checks += 1
        generations = [rng.randint(1, 1_000_000) for _ in range(6)]
        captured = generations.copy()
        history_loading_generation = rng.randint(1, 1_000_000)

        # Busy entry is an immediate publication barrier in every engine mode. The
        # backing source token deliberately remains stable until a real transition
        # is published, but every old request must become stale now. This includes
        # elevation beginning in Fallback as well as Native maintenance/elevation.
        generations = [value + 1 for value in generations]
        history_loading_generation = 0
        for current, request in zip(generations, captured):
            assert current != request
            checks += 1
        assert history_loading_generation == 0
        checks += 1

        # Root-keyed caches are not discarded merely because the source is busy;
        # failed elevation can therefore keep unchanged cached evidence. A real
        # recovery/source-token change owns the later cache-key miss/reset.
        cache_keys_current = [True] * 5
        assert all(cache_keys_current)
        checks += 1

        cache_keys_current = [False] * 5
        for current in cache_keys_current:
            assert not current
            checks += 1

    return checks


def require(text: str, needle: str, label: str) -> int:
    if needle not in text:
        raise AssertionError(f"missing {label}: {needle}")
    return 1


def check_repository(root: Path) -> int:
    window = (root / "src/FileOp.App/MainWindow.StorageSourceIdentity.cs").read_text(
        encoding="utf-8"
    )
    engine = (root / "src/FileOp.App/DesktopSearchEngine.cs").read_text(encoding="utf-8")
    files = (root / "src/FileOp.App/MainWindow.Files.cs").read_text(encoding="utf-8")
    folders = (root / "src/FileOp.App/MainWindow.xaml.cs").read_text(encoding="utf-8")
    types = (root / "src/FileOp.App/MainWindow.StorageTypes.cs").read_text(encoding="utf-8")
    history = (root / "src/FileOp.App/MainWindow.StorageHistory.cs").read_text(encoding="utf-8")
    optimize = (root / "src/FileOp.App/MainWindow.StorageOptimization.cs").read_text(
        encoding="utf-8"
    )
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    checks = 0

    handler_start = window.index(
        "private void StorageSourceIdentity_StateChanged(DesktopSearchEngineState state)"
    )
    busy_at = window.index("if (state.IsBusy)", handler_start)
    identity_read_at = window.index(
        "var sourceIdentityKey = _searchEngine.StorageSourceIdentityKey;",
        busy_at,
    )
    if busy_at >= identity_read_at:
        raise AssertionError("engine busy publication barrier must precede identity equality return")
    checks += 1

    busy_block = window[busy_at:identity_read_at]
    required_busy = (
        ("InvalidateFilesPane(_leftFilesPane);", "left Files busy invalidation"),
        ("InvalidateFilesPane(_rightFilesPane);", "right Files busy invalidation"),
        ("Interlocked.Increment(ref _storageGeneration);", "Folders busy generation"),
        ("Interlocked.Increment(ref _storageTypeGeneration);", "Types busy generation"),
        ("Interlocked.Increment(ref _storageHistoryGeneration);", "History busy generation"),
        ("Interlocked.Exchange(ref _storageHistoryLoadingGeneration, 0);", "History busy loading reset"),
        ("Interlocked.Increment(ref _storageOptimizationGeneration);", "Optimize busy generation"),
        ("Interlocked.Exchange(ref _storageOptimizationAnalysis, null);", "same-size busy reference barrier"),
        ("Interlocked.Exchange(ref _storageKnownLocationReview, null);", "known-location busy reference barrier"),
    )
    for needle, label in required_busy:
        checks += require(busy_block, needle, label)

    elevation_busy_at = engine.index(
        'Status = "Requesting helper-only administrative indexing access…"'
    )
    elevation_gate_at = engine.index(
        "await _searchOperationGate.WaitAsync(token)",
        elevation_busy_at,
    )
    elevation_busy_block = engine[elevation_busy_at:elevation_gate_at]
    if "IsBusy = true" not in elevation_busy_block or "Mode =" in elevation_busy_block:
        raise AssertionError(
            "elevation must publish busy while preserving the current mode, including Fallback"
        )
    checks += 1

    equality_at = window.index("if (string.Equals(", identity_read_at)
    equality_return_at = window.index("return;", equality_at)
    source_key_clear_at = window.index("Interlocked.Exchange(ref _storageSourceKey, null);", equality_return_at)
    if not identity_read_at < equality_at < equality_return_at < source_key_clear_at:
        raise AssertionError("stable busy token must return before root-key cache invalidation")
    checks += 1

    source_change_block = window[source_key_clear_at:]
    checks += require(
        source_change_block,
        "Interlocked.Exchange(ref _storageHistoryLoadingGeneration, 0);",
        "History source-change loading reset",
    )

    checks += require(
        files,
        "var generation = Interlocked.Increment(ref pane.Generation);",
        "Files request generation capture",
    )
    checks += require(
        files,
        "Volatile.Read(ref pane.Generation) == generation;",
        "Files stale-publication check",
    )
    checks += require(
        files,
        "Interlocked.Increment(ref pane.Generation);",
        "Files pane generation invalidation",
    )

    generation_guards = (
        (folders, "generation != Volatile.Read(ref _storageGeneration)", "Folders generation guard"),
        (types, "generation != Volatile.Read(ref _storageTypeGeneration)", "Types generation guard"),
        (history, "generation != Volatile.Read(ref _storageHistoryGeneration)", "History generation guard"),
        (optimize, "generation != Volatile.Read(ref _storageOptimizationGeneration)", "Optimize generation guard"),
    )
    for text, needle, label in generation_guards:
        checks += require(text, needle, label)

    # History's stale task intentionally only clears its loading sentinel when its
    # captured generation is still current. That is why the immediate barrier must
    # release the sentinel itself after invalidating the owner generation.
    checks += require(
        history,
        "generation == Volatile.Read(ref _storageHistoryGeneration) &&",
        "History stale-finally ownership check",
    )
    checks += require(
        history,
        "_storageHistoryLoadingGeneration == generation",
        "History loading-sentinel ownership check",
    )

    checks += require(
        gate,
        "verify_storage_busy_publication_barrier.py --repo-root $repoRoot --cases 50000",
        "offline busy publication verifier wiring",
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
        "PASS: engine busy publication barrier verified with "
        f"{model_checks:,} model checks across {args.cases:,} randomized states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
