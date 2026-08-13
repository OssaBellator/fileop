#!/usr/bin/env python3
"""Verify displayed Search evidence follows the backing index source lifetime."""
from __future__ import annotations

import argparse
from pathlib import Path
import random


def source_event(
    generation: int,
    source_change_sequence: int,
    last_token: str | None,
    new_token: str | None,
    busy: bool,
) -> tuple[int, int, str | None, bool, tuple[int, str | None] | None]:
    if busy:
        generation += 1

    changed = new_token != last_token
    clear: tuple[int, str | None] | None = None
    if changed:
        generation += 1
        source_change_sequence += 1
        last_token = new_token
        clear = (source_change_sequence, new_token)

    return generation, source_change_sequence, last_token, changed, clear


def request_generation_event(generation: int) -> int:
    """Model any non-source Search generation increment."""
    return generation + 1


def can_apply_clear(
    source_change_sequence: int,
    current_token: str | None,
    clear: tuple[int, str | None] | None,
) -> bool:
    if clear is None:
        return False
    clear_sequence, captured_token = clear
    return (
        clear_sequence == source_change_sequence
        and captured_token == current_token
    )


def run_model(cases: int, seed: int) -> int:
    checks = 0

    # Busy with an unchanged token invalidates in-flight requests but does not own
    # a displayed-result clear.
    generation, sequence, token, changed, clear = source_event(
        10,
        2,
        "native:1",
        "native:1",
        busy=True,
    )
    assert generation == 11
    assert sequence == 2
    assert not changed
    assert clear is None
    checks += 4

    # A real token transition always owns a clear.
    generation, sequence, token, changed, clear = source_event(
        20,
        5,
        "native:1",
        "native:2",
        busy=False,
    )
    assert changed
    assert can_apply_clear(sequence, token, clear)
    checks += 2

    # Later source-only busy work does not cancel that clear.
    generation, sequence, token, changed_busy, busy_clear = source_event(
        generation,
        sequence,
        token,
        token,
        busy=True,
    )
    assert not changed_busy and busy_clear is None
    assert can_apply_clear(sequence, token, clear)
    checks += 2

    # Neither text/debounce/elevation invalidation nor a query generation is
    # allowed to cancel the source-owned clear. A query racing this transition may
    # be cleared once and must be run again after the source-change presentation.
    generation = request_generation_event(generation)
    assert can_apply_clear(sequence, token, clear)
    checks += 1

    # A newer token transition supersedes the old clear and owns its own clear.
    generation, sequence, token, changed_newer, newer_clear = source_event(
        generation,
        sequence,
        token,
        "fallback:1",
        busy=False,
    )
    assert changed_newer
    assert not can_apply_clear(sequence, token, clear)
    assert can_apply_clear(sequence, token, newer_clear)
    checks += 3

    # Token strings may cycle back; source-change sequence still prevents an old
    # queued clear for the same string from becoming current again.
    generation, sequence, token, _, old_a = source_event(
        0,
        0,
        "X",
        "A",
        busy=False,
    )
    generation, sequence, token, _, _ = source_event(
        generation,
        sequence,
        token,
        "B",
        busy=False,
    )
    generation, sequence, token, _, new_a = source_event(
        generation,
        sequence,
        token,
        "A",
        busy=False,
    )
    assert not can_apply_clear(sequence, token, old_a)
    assert can_apply_clear(sequence, token, new_a)
    checks += 2

    rng = random.Random(seed)
    token_kinds = (None, "native", "fallback")
    for index in range(cases):
        generation = rng.randint(0, 1_000_000)
        sequence = rng.randint(0, 100_000)
        token_kind = rng.choice(token_kinds)
        last_token = (
            None
            if token_kind is None
            else f"native:{index % 23}"
            if token_kind == "native"
            else f"fallback:{index % 23}"
        )

        source_changed = rng.random() < 0.45
        if source_changed:
            choices = [
                None,
                f"native:{(index + 1) % 29}",
                f"fallback:{(index + 2) % 31}",
            ]
            new_token = rng.choice(choices)
            if new_token == last_token:
                new_token = f"native:changed:{index}"
        else:
            new_token = last_token
        busy = rng.random() < 0.35

        initial_generation = generation
        initial_sequence = sequence
        generation, sequence, current_token, changed, maybe_clear = source_event(
            generation,
            sequence,
            last_token,
            new_token,
            busy,
        )
        expected_generation_increment = (
            (1 if busy else 0) + (1 if source_changed else 0)
        )
        assert generation == initial_generation + expected_generation_increment
        assert sequence == initial_sequence + (1 if source_changed else 0)
        assert changed == source_changed
        checks += 3

        # Guarantee a token transition in every randomized state.
        guaranteed_token = f"native:guaranteed:{index}"
        if guaranteed_token == current_token:
            guaranteed_token += ":next"
        generation, sequence, current_token, changed, clear = source_event(
            generation,
            sequence,
            current_token,
            guaranteed_token,
            busy=False,
        )
        assert changed and can_apply_clear(sequence, current_token, clear)
        checks += 1

        # Later same-token source busy does not cancel the clear.
        generation, sequence, current_token, changed_busy, no_clear = source_event(
            generation,
            sequence,
            current_token,
            current_token,
            busy=True,
        )
        assert not changed_busy and no_clear is None and can_apply_clear(
            sequence,
            current_token,
            clear,
        )
        checks += 1

        # Arbitrary Search generation increments do not cancel the source-owned
        # presentation boundary. This includes the elevation-click race that the
        # earlier generation-delta design misclassified.
        generation = request_generation_event(generation)
        assert can_apply_clear(sequence, current_token, clear)
        checks += 1
        generation = request_generation_event(generation)
        assert can_apply_clear(sequence, current_token, clear)
        checks += 1

        # A newer source transition invalidates the old clear and owns the next one.
        newer_token = f"fallback:new:{index}"
        if newer_token == current_token:
            newer_token += ":next"
        generation, sequence, current_token, changed_new, new_clear = source_event(
            generation,
            sequence,
            current_token,
            newer_token,
            busy=False,
        )
        assert changed_new
        assert not can_apply_clear(sequence, current_token, clear)
        assert can_apply_clear(sequence, current_token, new_clear)
        checks += 3

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
        ("private long _searchSourceChangeSequence;", "source-change sequence"),
        ("InitializeSearchSourceIdentityTracking", "Search lifetime initializer"),
        ("_searchEngine.StateChanged += SearchSourceIdentity_StateChanged;", "Search pre-handler subscription"),
        ("lock (_searchSourceIdentityGate)", "source-event serialization"),
        ("if (state.IsBusy)", "all-mode Search busy publication barrier"),
        ("Interlocked.Increment(ref _searchGeneration);", "Search generation invalidation"),
        ("_searchEngine.StorageSourceIdentityKey", "backing source token read"),
        ("Interlocked.Exchange(\n                ref _lastSearchSourceIdentityKey,", "atomic previous-token exchange"),
        ("var sourceChangeSequence = Interlocked.Increment(\n                ref _searchSourceChangeSequence);", "source-change sequence advance"),
        ("QueueSearchPresentationInvalidation(\n                sourceChangeSequence,", "source-owned clear queue"),
        ("DispatcherQueue.TryEnqueue", "UI-thread presentation invalidation"),
        ("await _searchGate.WaitAsync(_lifetimeCancellation.Token);", "stale/racing request gate drain"),
        ("_searchGate.Release();", "Search gate release"),
        ("await Task.Yield();", "post-request UI turn ordering"),
        ("sourceChangeSequence != Volatile.Read(\n                        ref _searchSourceChangeSequence)", "newer-source clear suppression"),
        ("sourceIdentityKey,\n                        _searchEngine.StorageSourceIdentityKey", "captured/current token equality"),
        ("catch (ObjectDisposedException) when (_closed)", "shutdown-safe queued invalidation"),
        ("_results.Clear();", "old presentation clear"),
        ("Search source changed. Search again", "explicit stale-source recovery wording"),
    ):
        checks += require(search_lifetime, needle, label)

    for needle, label in (
        ("_searchSourceInvalidationCount", "obsolete source invalidation counter"),
        ("_lastSearchSourceOwnedGeneration", "obsolete source-owned generation"),
        ("generationDelta", "request-generation clear cancellation"),
        ("sourceInvalidationDelta", "source/request delta clear cancellation"),
        ("RunSearchAsync(", "automatic Search rerun from source callback"),
        ("_searchEngine.SearchAsync(", "direct Search work from source callback"),
    ):
        checks += forbid(search_lifetime, needle, label)

    handler_at = search_lifetime.index(
        "private void SearchSourceIdentity_StateChanged(DesktopSearchEngineState state)"
    )
    handler_lock_at = search_lifetime.index("lock (_searchSourceIdentityGate)", handler_at)
    busy_at = search_lifetime.index("if (state.IsBusy)", handler_lock_at)
    token_at = search_lifetime.index(
        "var sourceIdentityKey = _searchEngine.StorageSourceIdentityKey;",
        busy_at,
    )
    sequence_at = search_lifetime.index(
        "var sourceChangeSequence = Interlocked.Increment(",
        token_at,
    )
    queue_at = search_lifetime.index(
        "QueueSearchPresentationInvalidation(",
        sequence_at,
    )
    if not handler_at < handler_lock_at < busy_at < token_at < sequence_at < queue_at:
        raise AssertionError(
            "Search source callback must serialize busy/token invalidation before queueing the source-owned clear"
        )
    checks += 1

    wait_at = search_lifetime.index("await _searchGate.WaitAsync(_lifetimeCancellation.Token);")
    release_at = search_lifetime.index("_searchGate.Release();", wait_at)
    yield_at = search_lifetime.index("await Task.Yield();", release_at)
    final_lock_at = search_lifetime.index("lock (_searchSourceIdentityGate)", yield_at)
    sequence_guard_at = search_lifetime.index(
        "sourceChangeSequence != Volatile.Read(",
        final_lock_at,
    )
    token_guard_at = search_lifetime.index(
        "_searchEngine.StorageSourceIdentityKey",
        sequence_guard_at,
    )
    clear_at = search_lifetime.index("_results.Clear();", token_guard_at)
    if not (
        wait_at < release_at < yield_at < final_lock_at
        < sequence_guard_at < token_guard_at < clear_at
    ):
        raise AssertionError(
            "presentation invalidation must drain Search work, yield, then require the latest source-change sequence/token before clearing"
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

    # #155's inherited Storage barrier must also cover Fallback-mode elevation.
    storage_handler_at = storage_lifetime.index(
        "private void StorageSourceIdentity_StateChanged(DesktopSearchEngineState state)"
    )
    storage_busy_at = storage_lifetime.index("if (state.IsBusy)", storage_handler_at)
    storage_identity_at = storage_lifetime.index(
        "var sourceIdentityKey = _searchEngine.StorageSourceIdentityKey;",
        storage_busy_at,
    )
    if not storage_handler_at < storage_busy_at < storage_identity_at:
        raise AssertionError(
            "stacked Search lifetime must inherit #155's all-mode Storage busy publication barrier"
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

    for needle, label in (
        ("native:{volume.VolumeIdentity:X16}", "native physical/source token"),
        ("fallback:{Volatile.Read(ref _fallbackStorageSourceGeneration)}", "fallback snapshot token"),
    ):
        checks += require(engine_lifetime, needle, label)

    checks += require(protocol, "public const int CurrentVersion = 8;", "protocol v8")
    checks += require(
        gate,
        "verify_search_source_lifetime.py --repo-root $repoRoot --cases 50000",
        "offline Search lifetime gate wiring",
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
