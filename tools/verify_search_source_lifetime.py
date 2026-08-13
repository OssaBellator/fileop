#!/usr/bin/env python3
"""Verify displayed Search evidence follows the backing index source lifetime."""
from __future__ import annotations

import argparse
from pathlib import Path
import random


def source_event(
    generation: int,
    last_token: str | None,
    new_token: str | None,
    busy: bool,
) -> tuple[int, str | None, int | None]:
    if busy:
        generation += 1

    clear_owner: int | None = None
    if new_token != last_token:
        generation += 1
        clear_owner = generation
        last_token = new_token

    return generation, last_token, clear_owner


def apply_clear(generation: int, clear_owner: int | None, displayed: bool) -> bool:
    if clear_owner is not None and clear_owner == generation:
        return False
    return displayed


def run_model(cases: int, seed: int) -> int:
    checks = 0

    generation = 10
    query_generation = generation
    displayed = True
    token = "native:1:1"
    generation, current_token, clear_owner = source_event(
        generation,
        token,
        token,
        busy=True,
    )
    assert generation == 11
    assert query_generation != generation
    assert clear_owner is None
    assert apply_clear(generation, clear_owner, displayed)
    checks += 4

    generation = 3
    generation, current_token, clear_owner = source_event(
        generation,
        "native:1:1",
        "native:1:2",
        busy=False,
    )
    assert clear_owner == generation and current_token == "native:1:2"
    assert not apply_clear(generation, clear_owner, displayed=True)
    checks += 2

    generation = 7
    generation, current_token, clear_owner = source_event(
        generation,
        "native:1:1",
        "fallback:1",
        busy=False,
    )
    fresh_query_generation = generation + 1
    assert apply_clear(fresh_query_generation, clear_owner, displayed=True)
    assert fresh_query_generation != clear_owner
    checks += 2

    generation = 1
    generation, current_token, clear_owner = source_event(
        generation,
        "native:1:1",
        None,
        busy=False,
    )
    assert clear_owner == generation and current_token is None
    generation, current_token, clear_owner = source_event(
        generation,
        None,
        "fallback:2",
        busy=False,
    )
    assert clear_owner == generation and current_token == "fallback:2"
    checks += 2

    generation = 2
    unchanged_generation, current_token, clear_owner = source_event(
        generation,
        "fallback:1",
        "fallback:1",
        busy=False,
    )
    assert unchanged_generation == generation and clear_owner is None
    checks += 1

    rng = random.Random(seed)
    for index in range(cases):
        generation = rng.randint(0, 100_000)
        token_kind = rng.randrange(3)
        last_token = (
            None
            if token_kind == 0
            else f"native:{index % 17}:{index % 23}"
            if token_kind == 1
            else f"fallback:{index % 29}"
        )
        source_changed = rng.random() < 0.45
        if source_changed:
            choices = [
                None,
                f"native:{(index + 1) % 19}:{(index + 2) % 31}",
                f"fallback:{(index + 3) % 37}",
            ]
            new_token = choices[rng.randrange(len(choices))]
            if new_token == last_token:
                new_token = f"fallback:changed:{index}"
        else:
            new_token = last_token

        busy = rng.random() < 0.35
        query_generation = generation
        displayed = rng.random() < 0.80
        after_generation, current_token, clear_owner = source_event(
            generation,
            last_token,
            new_token,
            busy,
        )
        expected_generation = (
            generation
            + (1 if busy else 0)
            + (1 if source_changed else 0)
        )
        assert after_generation == expected_generation
        assert (clear_owner is not None) == source_changed
        assert (query_generation == after_generation) == (
            not busy and not source_changed
        )
        immediate_display = apply_clear(
            after_generation,
            clear_owner,
            displayed,
        )
        assert immediate_display == (False if source_changed else displayed)

        fresh_query_generation = after_generation + 1
        late_display = apply_clear(
            fresh_query_generation,
            clear_owner,
            displayed,
        )
        assert late_display == displayed
        assert fresh_query_generation != clear_owner

        failed_elevation_generation, failed_token, failed_clear = source_event(
            generation,
            last_token,
            last_token,
            busy=True,
        )
        assert failed_clear is None and failed_token == last_token
        assert apply_clear(
            failed_elevation_generation,
            failed_clear,
            displayed,
        ) == displayed

        routine_generation, routine_token, routine_clear = source_event(
            generation,
            last_token,
            last_token,
            busy=False,
        )
        assert (
            routine_generation == generation
            and routine_token == last_token
            and routine_clear is None
        )
        checks += 9

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
    search_lifetime = (
        root / "src/FileOp.App/MainWindow.SearchSourceIdentity.cs"
    ).read_text(encoding="utf-8")
    app = (root / "src/FileOp.App/App.xaml.cs").read_text(encoding="utf-8")
    main = (root / "src/FileOp.App/MainWindow.xaml.cs").read_text(encoding="utf-8")
    storage_lifetime = (
        root / "src/FileOp.App/MainWindow.StorageSourceIdentity.cs"
    ).read_text(encoding="utf-8")
    engine_lifetime = (
        root / "src/FileOp.App/DesktopSearchEngine.StorageSourceIdentity.cs"
    ).read_text(encoding="utf-8")
    engine = (root / "src/FileOp.App/DesktopSearchEngine.cs").read_text(encoding="utf-8")
    files = (root / "src/FileOp.App/MainWindow.Files.cs").read_text(encoding="utf-8")
    protocol = (
        root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs"
    ).read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    checks = 0

    for needle, label in (
        ("InitializeSearchSourceIdentityTracking", "Search lifetime initializer"),
        ("_searchEngine.StateChanged += SearchSourceIdentity_StateChanged;", "Search pre-handler subscription"),
        ("if (state.IsBusy)", "all-mode busy publication barrier"),
        ("Interlocked.Increment(ref _searchGeneration);", "Search generation invalidation"),
        ("_searchEngine.StorageSourceIdentityKey", "backing source token read"),
        ("Interlocked.Exchange(\n            ref _lastSearchSourceIdentityKey,", "atomic previous-token exchange"),
        ("StringComparison.Ordinal", "exact backing-token comparison"),
        ("var invalidationGeneration = Interlocked.Increment(ref _searchGeneration);", "display invalidation owner"),
        ("DispatcherQueue.TryEnqueue", "UI-thread presentation invalidation"),
        ("await _searchGate.WaitAsync(_lifetimeCancellation.Token);", "stale request gate drain"),
        ("_searchGate.Release();", "stale request gate release"),
        ("await Task.Yield();", "post-request UI turn ordering"),
        ("invalidationGeneration != Volatile.Read(ref _searchGeneration)", "fresh-query clear suppression"),
        ("catch (ObjectDisposedException) when (_closed)", "shutdown-safe queued invalidation"),
        ("_results.Clear();", "old presentation clear"),
        ("Search source changed. Search again", "explicit stale-source recovery wording"),
    ):
        checks += require(search_lifetime, needle, label)

    checks += forbid(
        search_lifetime,
        "RunSearchAsync(",
        "automatic Search rerun from source callback",
    )
    checks += forbid(
        search_lifetime,
        "_searchEngine.SearchAsync(",
        "direct Search work from source callback",
    )

    wait_at = search_lifetime.index("await _searchGate.WaitAsync(_lifetimeCancellation.Token);")
    release_at = search_lifetime.index("_searchGate.Release();", wait_at)
    yield_at = search_lifetime.index("await Task.Yield();", release_at)
    invalidation_check_at = search_lifetime.index(
        "invalidationGeneration != Volatile.Read(ref _searchGeneration)",
        yield_at,
    )
    clear_at = search_lifetime.index("_results.Clear();", invalidation_check_at)
    if not wait_at < release_at < yield_at < invalidation_check_at < clear_at:
        raise AssertionError(
            "source-change presentation must drain stale Search work, yield, then revalidate before clearing"
        )
    checks += 1

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

    storage_init = app.index("window.InitializeStorageSourceIdentityTracking();")
    search_init = app.index("window.InitializeSearchSourceIdentityTracking();")
    files_init = app.index("window.InitializeFilesFeature();")
    if not storage_init < search_init < files_init:
        raise AssertionError(
            "startup must install backing-token tracking before Search, then Files"
        )
    checks += 1

    checks += require(
        main,
        "private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)\n    {\n        Interlocked.Increment(ref _searchGeneration);",
        "atomic Search debounce invalidation",
    )
    checks += require(
        main,
        "var generation = Interlocked.Increment(ref _searchGeneration);",
        "atomic Search request generation",
    )

    search_call = main.index("var matches = await _searchEngine.SearchAsync(")
    post_search_generation = main.index(
        "if (_closed || generation != Volatile.Read(ref _searchGeneration))",
        search_call,
    )
    results_clear = main.index("_results.Clear();", post_search_generation)
    if not search_call < post_search_generation < results_clear:
        raise AssertionError(
            "RunSearchAsync must reject stale generations after the engine read before publishing rows"
        )
    checks += 1

    checks += require(
        storage_lifetime,
        "if (_activeSection == AppSection.Search && !_filesVisible && SearchBox.IsEnabled)",
        "no hidden post-elevation Search while Files is visible",
    )
    checks += require(files, "_filesVisible = true;", "Files visibility tracking")
    checks += require(
        files,
        "_activeSection = AppSection.Search;",
        "Files shares Search section identity",
    )

    for needle, label in (
        ("native:{volume.VolumeIdentity:X16}", "native physical/source token"),
        ("fallback:{Volatile.Read(ref _fallbackStorageSourceGeneration)}", "fallback snapshot token"),
    ):
        checks += require(engine_lifetime, needle, label)

    checks += require(protocol, "public const int CurrentVersion = 8;", "protocol v8")
    checks += require(
        gate,
        "verify_search_source_lifetime.py --repo-root $repoRoot --cases 50000",
        "offline gate wiring",
    )
    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0x156)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = f" and {source_checks:,} source checks" if args.repo_root else ""
    print(
        "PASS: Search source lifetime verifier: "
        f"{model_checks:,} model checks across {args.cases:,} randomized states{suffix}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
