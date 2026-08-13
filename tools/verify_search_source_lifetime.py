#!/usr/bin/env python3
"""Verify displayed Search evidence follows the backing index source lifetime."""
from __future__ import annotations

import argparse
from pathlib import Path
import random


def source_event(
    generation: int,
    last_source_generation: int,
    last_token: str | None,
    new_token: str | None,
    busy: bool,
) -> tuple[int, int, str | None, bool, str | None]:
    if busy:
        generation += 1
        last_source_generation = generation

    changed = new_token != last_token
    captured_clear_token: str | None = None
    if changed:
        generation += 1
        last_source_generation = generation
        last_token = new_token
        captured_clear_token = new_token

    return (
        generation,
        last_source_generation,
        last_token,
        changed,
        captured_clear_token,
    )


def user_event(generation: int) -> int:
    return generation + 1


def can_apply_clear(
    generation: int,
    last_source_generation: int,
    current_token: str | None,
    captured_clear_token: str | None,
) -> bool:
    return (
        generation == last_source_generation
        and current_token == captured_clear_token
    )


def run_model(cases: int, seed: int) -> int:
    checks = 0

    # Busy with an unchanged token invalidates an in-flight query but does not
    # manufacture a displayed-result clear.
    generation, source_generation, token, changed, captured = source_event(
        10,
        9,
        "native:1:1",
        "native:1:1",
        busy=True,
    )
    assert generation == 11
    assert source_generation == 11
    assert token == "native:1:1"
    assert not changed and captured is None
    checks += 4

    # Token change owns a clear. A later source-owned busy generation keeps the
    # clear valid, while a user-owned generation cancels it. If that user query is
    # then invalidated by another source busy event, the clear becomes valid again.
    generation, source_generation, token, changed, captured = source_event(
        20,
        19,
        "native:1:1",
        "native:1:2",
        busy=False,
    )
    assert changed and captured == "native:1:2"
    assert can_apply_clear(generation, source_generation, token, captured)
    checks += 2

    generation, source_generation, token, changed_again, ignored = source_event(
        generation,
        source_generation,
        token,
        token,
        busy=True,
    )
    assert not changed_again and can_apply_clear(
        generation,
        source_generation,
        token,
        captured,
    )
    checks += 1

    generation = user_event(generation)
    assert not can_apply_clear(generation, source_generation, token, captured)
    checks += 1

    generation, source_generation, token, changed_again, ignored = source_event(
        generation,
        source_generation,
        token,
        token,
        busy=True,
    )
    assert not changed_again and can_apply_clear(
        generation,
        source_generation,
        token,
        captured,
    )
    checks += 1

    old_capture = captured
    generation, source_generation, token, changed_again, new_capture = source_event(
        generation,
        source_generation,
        token,
        "fallback:2",
        busy=False,
    )
    assert changed_again and not can_apply_clear(
        generation,
        source_generation,
        token,
        old_capture,
    )
    assert can_apply_clear(
        generation,
        source_generation,
        token,
        new_capture,
    )
    checks += 2

    rng = random.Random(seed)
    token_kinds = (None, "native", "fallback")
    for index in range(cases):
        generation = rng.randint(1, 100_000)
        latest_was_source_owned = rng.random() < 0.5
        last_source_generation = (
            generation if latest_was_source_owned else generation - 1
        )
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
        initial_source_generation = last_source_generation
        (
            generation,
            last_source_generation,
            current_token,
            changed,
            captured,
        ) = source_event(
            generation,
            last_source_generation,
            last_token,
            new_token,
            busy,
        )
        expected_generation = (
            initial_generation
            + (1 if busy else 0)
            + (1 if source_changed else 0)
        )
        expected_source_generation = (
            expected_generation
            if busy or source_changed
            else initial_source_generation
        )
        assert generation == expected_generation
        assert current_token == new_token
        assert changed == source_changed
        assert captured == (new_token if source_changed else None)
        assert last_source_generation == expected_source_generation
        checks += 5

        # Use a guaranteed token-change clear to exercise ownership races in every
        # randomized state, independently of the first event's shape.
        guaranteed_token = f"native:guaranteed:{index}"
        if guaranteed_token == current_token:
            guaranteed_token += ":next"
        (
            generation,
            last_source_generation,
            current_token,
            changed,
            captured,
        ) = source_event(
            generation,
            last_source_generation,
            current_token,
            guaranteed_token,
            busy=False,
        )
        assert changed and can_apply_clear(
            generation,
            last_source_generation,
            current_token,
            captured,
        )
        checks += 1

        generation = user_event(generation)
        assert not can_apply_clear(
            generation,
            last_source_generation,
            current_token,
            captured,
        )
        checks += 1

        (
            generation,
            last_source_generation,
            current_token,
            changed_after_user,
            ignored_capture,
        ) = source_event(
            generation,
            last_source_generation,
            current_token,
            current_token,
            busy=True,
        )
        assert not changed_after_user and can_apply_clear(
            generation,
            last_source_generation,
            current_token,
            captured,
        )
        checks += 1

        newer_token = f"fallback:newer:{index}"
        old_capture = captured
        (
            generation,
            last_source_generation,
            current_token,
            changed_newer,
            newer_capture,
        ) = source_event(
            generation,
            last_source_generation,
            current_token,
            newer_token,
            busy=False,
        )
        assert changed_newer and not can_apply_clear(
            generation,
            last_source_generation,
            current_token,
            old_capture,
        )
        assert can_apply_clear(
            generation,
            last_source_generation,
            current_token,
            newer_capture,
        )
        checks += 2

        (
            generation,
            last_source_generation,
            current_token,
            changed_failed,
            failed_capture,
        ) = source_event(
            generation,
            last_source_generation,
            current_token,
            current_token,
            busy=True,
        )
        assert (
            not changed_failed
            and failed_capture is None
            and can_apply_clear(
                generation,
                last_source_generation,
                current_token,
                newer_capture,
            )
        )
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
        ("InitializeSearchSourceIdentityTracking", "Search lifetime initializer"),
        ("_searchEngine.StateChanged += SearchSourceIdentity_StateChanged;", "Search pre-handler subscription"),
        ("lock (_searchSourceIdentityGate)", "source-event serialization"),
        ("if (state.IsBusy)", "all-mode busy publication barrier"),
        ("InvalidateSearchFromSource();", "source-owned generation invalidation"),
        ("_searchEngine.StorageSourceIdentityKey", "backing source token read"),
        ("Interlocked.Exchange(\n                ref _lastSearchSourceIdentityKey,", "atomic previous-token exchange"),
        ("StringComparison.Ordinal", "exact backing-token comparison"),
        ("QueueSearchPresentationInvalidation(sourceIdentityKey);", "token-change display invalidation"),
        ("_lastSearchSourceOwnedGeneration = Interlocked.Increment(ref _searchGeneration);", "source-owned request token"),
        ("DispatcherQueue.TryEnqueue", "UI-thread presentation invalidation"),
        ("await _searchGate.WaitAsync(_lifetimeCancellation.Token);", "stale request gate drain"),
        ("_searchGate.Release();", "stale request gate release"),
        ("await Task.Yield();", "post-request UI turn ordering"),
        ("var currentGeneration = Volatile.Read(ref _searchGeneration);", "current request generation read"),
        ("currentGeneration != _lastSearchSourceOwnedGeneration", "user-owned generation suppression"),
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
        "QueueSearchPresentationInvalidation(sourceIdentityKey);",
        token_at,
    )
    if not handler_at < handler_lock_at < busy_at < token_at < queue_at:
        raise AssertionError(
            "Search source callback must serialize busy/token invalidation before queueing presentation work"
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
    source_owner_at = search_lifetime.index(
        "currentGeneration != _lastSearchSourceOwnedGeneration",
        current_generation_at,
    )
    current_token_at = search_lifetime.index(
        "_searchEngine.StorageSourceIdentityKey",
        source_owner_at,
    )
    clear_at = search_lifetime.index("_results.Clear();", current_token_at)
    if not (
        wait_at < release_at < yield_at < final_lock_at
        < current_generation_at < source_owner_at < current_token_at < clear_at
    ):
        raise AssertionError(
            "presentation invalidation must drain stale Search work, yield, then validate source ownership/token before clearing"
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
