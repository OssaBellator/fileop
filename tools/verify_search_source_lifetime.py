#!/usr/bin/env python3
"""Verify displayed Search evidence follows the backing index source lifetime."""
from __future__ import annotations

import argparse
from pathlib import Path
import random

UINT32_MAX = (1 << 32) - 1


def source_event(
    generation: int,
    source_invalidation_count: int,
    last_token: str | None,
    new_token: str | None,
    busy: bool,
) -> tuple[int, int, str | None, bool, tuple[int, int, str | None] | None]:
    if busy:
        source_invalidation_count += 1
        generation = (generation + 1) & UINT32_MAX

    changed = new_token != last_token
    clear: tuple[int, int, str | None] | None = None
    if changed:
        source_invalidation_count += 1
        generation = (generation + 1) & UINT32_MAX
        last_token = new_token
        clear = (generation, source_invalidation_count, new_token)

    return generation, source_invalidation_count, last_token, changed, clear


def user_event(generation: int) -> int:
    return (generation + 1) & UINT32_MAX


def can_apply_clear(
    generation: int,
    source_invalidation_count: int,
    current_token: str | None,
    clear: tuple[int, int, str | None] | None,
) -> bool:
    if clear is None:
        return False

    invalidation_generation, initial_source_count, captured_token = clear
    source_delta = source_invalidation_count - initial_source_count
    generation_delta = (generation - invalidation_generation) & UINT32_MAX
    return (
        0 <= source_delta <= UINT32_MAX
        and generation_delta == source_delta
        and current_token == captured_token
    )


def run_model(cases: int, seed: int) -> int:
    checks = 0

    # Busy with an unchanged token invalidates in-flight work only.
    generation, source_count, token, changed, clear = source_event(
        10,
        4,
        "native:1:1",
        "native:1:1",
        busy=True,
    )
    assert generation == 11
    assert source_count == 5
    assert token == "native:1:1"
    assert not changed and clear is None
    checks += 4

    # Token change owns a clear. Later source-only invalidation keeps it valid.
    generation, source_count, token, changed, clear = source_event(
        20,
        7,
        "native:1:1",
        "native:1:2",
        busy=False,
    )
    assert changed and clear == (21, 8, "native:1:2")
    assert can_apply_clear(generation, source_count, token, clear)
    checks += 2

    generation, source_count, token, changed_again, ignored = source_event(
        generation,
        source_count,
        token,
        token,
        busy=True,
    )
    assert not changed_again and can_apply_clear(
        generation,
        source_count,
        token,
        clear,
    )
    checks += 1

    # A later user/query generation permanently supersedes that clear. Another
    # failed source-busy interval with the same token must not resurrect it.
    generation = user_event(generation)
    assert not can_apply_clear(generation, source_count, token, clear)
    checks += 1

    generation, source_count, token, changed_again, ignored = source_event(
        generation,
        source_count,
        token,
        token,
        busy=True,
    )
    assert not changed_again and not can_apply_clear(
        generation,
        source_count,
        token,
        clear,
    )
    checks += 1

    old_clear = clear
    generation, source_count, token, changed_again, new_clear = source_event(
        generation,
        source_count,
        token,
        "fallback:2",
        busy=False,
    )
    assert changed_again and not can_apply_clear(
        generation,
        source_count,
        token,
        old_clear,
    )
    assert can_apply_clear(generation, source_count, token, new_clear)
    checks += 2

    rng = random.Random(seed)
    token_kinds = (None, "native", "fallback")
    for index in range(cases):
        generation = rng.randrange(0, UINT32_MAX + 1)
        source_count = rng.randint(0, 100_000)
        token_kind = rng.choice(token_kinds)
        last_token = (
            None
            if token_kind is None
            else f"native:{index % 17}:{index % 23}"
            if token_kind == "native"
            else f"fallback:{index % 29}"
        )

        source_changed = rng.random() < 0.45
        if source_changed:
            choices = [
                None,
                f"native:{(index + 1) % 19}:{(index + 2) % 31}",
                f"fallback:{(index + 3) % 37}",
            ]
            new_token = rng.choice(choices)
            if new_token == last_token:
                new_token = f"fallback:changed:{index}"
        else:
            new_token = last_token
        busy = rng.random() < 0.35

        initial_generation = generation
        initial_source_count = source_count
        (
            generation,
            source_count,
            current_token,
            changed,
            maybe_clear,
        ) = source_event(
            generation,
            source_count,
            last_token,
            new_token,
            busy,
        )
        expected_increment = (1 if busy else 0) + (1 if source_changed else 0)
        assert generation == ((initial_generation + expected_increment) & UINT32_MAX)
        assert source_count == initial_source_count + expected_increment
        assert current_token == new_token
        assert changed == source_changed
        assert (maybe_clear is not None) == source_changed
        checks += 5

        # Guarantee a token-change clear for ownership-race checks in every state.
        guaranteed_token = f"native:guaranteed:{index}"
        if guaranteed_token == current_token:
            guaranteed_token += ":next"
        (
            generation,
            source_count,
            current_token,
            changed,
            clear,
        ) = source_event(
            generation,
            source_count,
            current_token,
            guaranteed_token,
            busy=False,
        )
        assert changed and can_apply_clear(
            generation,
            source_count,
            current_token,
            clear,
        )
        checks += 1

        # Later source-only work keeps the pending clear valid.
        generation, source_count, current_token, changed_busy, ignored = source_event(
            generation,
            source_count,
            current_token,
            current_token,
            busy=True,
        )
        assert not changed_busy and can_apply_clear(
            generation,
            source_count,
            current_token,
            clear,
        )
        checks += 1

        # User/query work supersedes it permanently, even if a later failed busy
        # interval advances the source-owned counters again.
        generation = user_event(generation)
        assert not can_apply_clear(
            generation,
            source_count,
            current_token,
            clear,
        )
        checks += 1

        generation, source_count, current_token, changed_busy, ignored = source_event(
            generation,
            source_count,
            current_token,
            current_token,
            busy=True,
        )
        assert not changed_busy and not can_apply_clear(
            generation,
            source_count,
            current_token,
            clear,
        )
        checks += 1

        # A newer token transition cancels the old captured token and owns a new clear.
        old_clear = clear
        newer_token = f"fallback:newer:{index}"
        (
            generation,
            source_count,
            current_token,
            changed_newer,
            newer_clear,
        ) = source_event(
            generation,
            source_count,
            current_token,
            newer_token,
            busy=False,
        )
        assert changed_newer and not can_apply_clear(
            generation,
            source_count,
            current_token,
            old_clear,
        )
        assert can_apply_clear(
            generation,
            source_count,
            current_token,
            newer_clear,
        )
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
        ("private readonly object _searchSourceIdentityGate = new();", "source callback serialization gate"),
        ("private int _lastSearchSourceOwnedGeneration;", "latest source-owned generation"),
        ("private long _searchSourceInvalidationCount;", "source invalidation counter"),
        ("InitializeSearchSourceIdentityTracking", "Search lifetime initializer"),
        ("_searchEngine.StateChanged += SearchSourceIdentity_StateChanged;", "Search pre-handler subscription"),
        ("lock (_searchSourceIdentityGate)", "source-event serialization"),
        ("if (state.IsBusy)", "all-mode busy publication barrier"),
        ("InvalidateSearchFromSource();", "source-owned generation invalidation"),
        ("_searchEngine.StorageSourceIdentityKey", "backing source token read"),
        ("Interlocked.Exchange(\n                ref _lastSearchSourceIdentityKey,", "atomic previous-token exchange"),
        ("QueueSearchPresentationInvalidation(\n                _lastSearchSourceOwnedGeneration,", "token-change clear baseline generation"),
        ("Volatile.Read(ref _searchSourceInvalidationCount)", "token-change clear baseline source count"),
        ("Interlocked.Increment(ref _searchSourceInvalidationCount);", "source invalidation counter advance"),
        ("_lastSearchSourceOwnedGeneration = Interlocked.Increment(ref _searchGeneration);", "source-owned request token"),
        ("DispatcherQueue.TryEnqueue", "UI-thread presentation invalidation"),
        ("await _searchGate.WaitAsync(_lifetimeCancellation.Token);", "stale request gate drain"),
        ("_searchGate.Release();", "stale request gate release"),
        ("await Task.Yield();", "post-request UI turn ordering"),
        ("var currentGeneration = Volatile.Read(ref _searchGeneration);", "current request generation read"),
        ("var currentSourceInvalidationCount = Volatile.Read(", "current source invalidation count read"),
        ("var sourceInvalidationDelta =", "source invalidation delta"),
        ("var generationDelta = unchecked(", "modular generation delta"),
        ("sourceInvalidationDelta > uint.MaxValue", "generation-wrap bound"),
        ("generationDelta != (uint)sourceInvalidationDelta", "user-owned generation suppression"),
        ("sourceIdentityKey,\n                        _searchEngine.StorageSourceIdentityKey", "captured/current token equality"),
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

    handler_at = search_lifetime.index(
        "private void SearchSourceIdentity_StateChanged(DesktopSearchEngineState state)"
    )
    handler_lock_at = search_lifetime.index("lock (_searchSourceIdentityGate)", handler_at)
    busy_at = search_lifetime.index("if (state.IsBusy)", handler_lock_at)
    token_at = search_lifetime.index(
        "var sourceIdentityKey = _searchEngine.StorageSourceIdentityKey;",
        busy_at,
    )
    queue_at = search_lifetime.index(
        "QueueSearchPresentationInvalidation(",
        token_at,
    )
    if not handler_at < handler_lock_at < busy_at < token_at < queue_at:
        raise AssertionError(
            "Search source callback must serialize busy/token invalidation before queueing presentation work"
        )
    checks += 1

    invalidate_at = search_lifetime.index("private void InvalidateSearchFromSource()")
    source_count_at = search_lifetime.index(
        "Interlocked.Increment(ref _searchSourceInvalidationCount);",
        invalidate_at,
    )
    source_generation_at = search_lifetime.index(
        "_lastSearchSourceOwnedGeneration = Interlocked.Increment(ref _searchGeneration);",
        source_count_at,
    )
    if not invalidate_at < source_count_at < source_generation_at:
        raise AssertionError(
            "source invalidation count must advance before its matching Search generation"
        )
    checks += 1

    wait_at = search_lifetime.index("await _searchGate.WaitAsync(_lifetimeCancellation.Token);")
    release_at = search_lifetime.index("_searchGate.Release();", wait_at)
    yield_at = search_lifetime.index("await Task.Yield();", release_at)
    final_lock_at = search_lifetime.index("lock (_searchSourceIdentityGate)", yield_at)
    current_generation_at = search_lifetime.index(
        "var currentGeneration = Volatile.Read(ref _searchGeneration);",
        final_lock_at,
    )
    current_source_count_at = search_lifetime.index(
        "var currentSourceInvalidationCount = Volatile.Read(",
        current_generation_at,
    )
    source_delta_at = search_lifetime.index(
        "var sourceInvalidationDelta =",
        current_source_count_at,
    )
    generation_delta_at = search_lifetime.index(
        "var generationDelta = unchecked(",
        source_delta_at,
    )
    user_guard_at = search_lifetime.index(
        "generationDelta != (uint)sourceInvalidationDelta",
        generation_delta_at,
    )
    current_token_at = search_lifetime.index(
        "_searchEngine.StorageSourceIdentityKey",
        user_guard_at,
    )
    clear_at = search_lifetime.index("_results.Clear();", current_token_at)
    if not (
        wait_at < release_at < yield_at < final_lock_at
        < current_generation_at < current_source_count_at < source_delta_at
        < generation_delta_at < user_guard_at < current_token_at < clear_at
    ):
        raise AssertionError(
            "presentation invalidation must drain stale Search work, yield, then compare generation/source deltas and token before clearing"
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
