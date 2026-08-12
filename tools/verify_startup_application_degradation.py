#!/usr/bin/env python3
"""Verify bounded Diagnostics-Performance startup degradation compatibility evidence."""
from __future__ import annotations

import argparse
import random
from pathlib import Path

Event = tuple[int, int, int, int]


def select_visible(events: list[Event], max_events: int) -> tuple[list[Event], bool]:
    if max_events <= 0:
        raise ValueError("max_events must be positive")
    return list(events[:max_events]), len(events) > max_events


def valid_result(events: list[Event], max_events: int, more: bool) -> bool:
    if len(events) > max_events:
        return False
    if more and len(events) != max_events:
        return False
    ids = [event[0] for event in events]
    if len(ids) != len(set(ids)):
        return False
    recorded = [event[1] for event in events]
    return all(recorded[index] >= recorded[index + 1] for index in range(len(recorded) - 1))


def run_model(cases: int, seed: int) -> int:
    rng = random.Random(seed)
    checks = 0

    empty, empty_more = select_visible([], 20)
    assert empty == []
    assert not empty_more
    assert valid_result(empty, 20, empty_more)
    assert not valid_result([], 20, True)
    assert select_visible([(1, 10, 20, 30), (2, 9, 5, 99)], 1)[1]
    assert valid_result([(1, 10, 1, 10)], 1, False)
    checks += 6

    for case in range(cases):
        count = rng.randint(2, 40)
        max_events = rng.randint(1, 20)
        base_time = case * 100_000 + 100_000
        events: list[Event] = []
        for index in range(count):
            # Total/degradation are intentionally independent. Compatibility
            # evidence does not assume degradation <= total or derive a score.
            total = rng.randint(0, 20_000_000)
            degradation = rng.randint(0, 20_000_000)
            events.append((index + 1, base_time - index, total, degradation))

        visible, more = select_visible(events, max_events)
        assert visible == events[:max_events]
        assert more == (count > max_events)
        assert len(visible) == min(count, max_events)
        assert len(visible) <= max_events
        assert len({event[0] for event in visible}) == len(visible)
        assert all(
            visible[index][1] >= visible[index + 1][1]
            for index in range(len(visible) - 1)
        )
        assert valid_result(visible, max_events, more)
        checks += 7

        # Changing raw duration evidence cannot change which newest records are
        # retained because the UI/provider contract never duration-ranks them.
        remapped = [
            (record_id, recorded, rng.getrandbits(32), rng.getrandbits(32))
            for record_id, recorded, _, _ in events
        ]
        remapped_visible, remapped_more = select_visible(remapped, max_events)
        assert [event[:2] for event in remapped_visible] == [event[:2] for event in visible]
        assert remapped_more == more
        checks += 2

        duplicate = list(visible)
        duplicate[-1] = duplicate[0]
        assert not valid_result(duplicate, max_events, more)
        checks += 1

        out_of_order = list(visible)
        if len(out_of_order) == 1:
            out_of_order.append((count + 100, out_of_order[0][1] + 1, 0, 0))
            assert not valid_result(out_of_order, max_events + 1, False)
        else:
            out_of_order[0], out_of_order[-1] = out_of_order[-1], out_of_order[0]
            assert not valid_result(out_of_order, max_events, more)
        checks += 1

    return checks


def require(text: str, needle: str, label: str) -> int:
    if needle not in text:
        raise AssertionError("missing %s: %s" % (label, needle))
    return 1


def forbid(text: str, needle: str, label: str) -> int:
    if needle in text:
        raise AssertionError("forbidden %s: %s" % (label, needle))
    return 1


def check_repository(root: Path) -> int:
    core = (root / "src/FileOp.Core/Performance/StartupApplicationDegradation.cs").read_text(encoding="utf-8")
    provider = (root / "src/FileOp.Windows/Performance/WindowsStartupApplicationDegradationProvider.cs").read_text(encoding="utf-8")
    project = (root / "src/FileOp.Windows/FileOp.Windows.csproj").read_text(encoding="utf-8")
    tests = (root / "tests/FileOp.Windows.Tests/StartupApplicationDegradationTests.cs").read_text(encoding="utf-8")
    compatibility_tests = (root / "tests/FileOp.Windows.Tests/StartupApplicationDegradationCompatibilityTests.cs").read_text(encoding="utf-8")
    docs = (root / "docs/startup-application-degradation.md").read_text(encoding="utf-8")
    parent = (root / "tools/verify_machine_process_activity.py").read_text(encoding="utf-8")
    wrapper = (root / "tools/verify_background_process_activity.py").read_text(encoding="utf-8")
    gate = (root / "tools/test-local.ps1").read_text(encoding="utf-8")
    protocol = (root / "src/FileOp.Core/Indexing/Service/IndexingServiceProtocol.cs").read_text(encoding="utf-8")

    checks = 0
    required = [
        (core, "DefaultReadBudget = TimeSpan.FromSeconds(2)", "default read budget"),
        (core, "MaximumReadBudget = TimeSpan.FromSeconds(10)", "hard read budget"),
        (core, "DefaultMaxEvents = 20", "default event cap"),
        (core, "MaximumEvents = 100", "hard event cap"),
        (core, "TotalTimeMilliseconds", "raw total-time evidence"),
        (core, "DegradationTimeMilliseconds", "raw degradation-time evidence"),
        (core, "MoreMatchingEventsAvailable", "explicit truncation evidence"),
        (core, "events must remain newest-first", "newest-first invariant"),
        (provider, '"Microsoft-Windows-Diagnostics-Performance/Operational"', "exact event channel"),
        (provider, '"Microsoft-Windows-Diagnostics-Performance"', "exact event provider"),
        (provider, "ApplicationDegradationEventId = 101", "exact compatibility event id"),
        (provider, "MaximumEventXmlCharacters = 65_536", "event XML bound"),
        (provider, "ReverseDirection = true", "newest-first event query"),
        (provider, "TolerateQueryErrors = false", "fail-closed query errors"),
        (provider, "budget.MaxEvents + 1", "explicit hidden-event peek"),
        (provider, "reader.ReadEvent(remaining)", "bounded event read"),
        (provider, "reader.CancelReading();", "caller cancellation"),
        (provider, "GetRemainingReadTimeout", "shared total read budget"),
        (provider, "ParseEventXml(record.ToXml())", "named XML parsing path"),
        (provider, 'RequireData(data, "Name")', "required component name"),
        (provider, 'RequireData(data, "TotalTime")', "required total time"),
        (provider, 'RequireData(data, "DegradationTime")', "required degradation time"),
        (provider, 'RequireData(data, "StartTime")', "required incident time"),
        (project, '<PackageReference Include="System.Diagnostics.EventLog" Version="10.0.10" />', "pinned EventLog package"),
        (tests, "ParserPreservesNamedCompatibilityFields", "parser regression"),
        (tests, "ResultPreservesNewestFirstOrderAndExplicitTruncation", "ordering/truncation regression"),
        (tests, "RemainingReadTimeoutUsesOneSharedBudget", "timeout regression"),
        (compatibility_tests, "ParserRejectsOversizedCompatibilityXml", "XML size regression"),
        (compatibility_tests, "EmptyCompatibilityResultDoesNotClaimFastStartupOnWindows", "empty-result regression"),
        (docs, "optional compatibility telemetry", "compatibility boundary"),
        (docs, "not proof", "empty-result boundary"),
        (docs, "does **not** assume `DegradationTime <= TotalTime`", "no duration heuristic"),
        (docs, "does not inspect or mutate", "no registration heuristic"),
        (parent, "from verify_startup_application_degradation import (", "parent imports startup verifier"),
        (parent, "run_startup_degradation_model(args.cases", "parent runs startup model"),
        (parent, "check_startup_degradation_repository(root)", "parent runs startup source checks"),
        (wrapper, "from verify_machine_process_activity import main", "stable machine wrapper"),
        (gate, "verify_background_process_activity.py --repo-root $repoRoot --cases 50000", "existing offline gate"),
        (protocol, "public const int CurrentVersion = 8;", "protocol v8 stability"),
    ]
    for text, needle, label in required:
        checks += require(text, needle, label)

    for needle in (
        "FormatDescription(",
        "EventLogWatcher",
        "Microsoft.Win32.Registry",
        "RegistryKey",
        "StartupTask",
        "GetProcessesByName",
        "Process.GetProcesses",
        "FileSystemWatcher",
        "PeriodicTimer",
        "DispatcherQueueTimer",
        "Kill(",
        "CloseMainWindow",
        "PriorityClass =",
        "ProcessorAffinity =",
    ):
        checks += forbid(provider + "\n" + docs, needle, "localized/registration/control/poller path")

    for needle in (
        "HealthScore",
        "ImpactScore",
        "StartupScore",
        "DegradationTimeMilliseconds /",
        "TotalTimeMilliseconds /",
        "OrderByDescending",
    ):
        checks += forbid(core + "\n" + provider, needle, "score/threshold/ranking heuristic")

    return checks


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", type=Path)
    parser.add_argument("--cases", type=int, default=50_000)
    parser.add_argument("--seed", type=int, default=0x510A710)
    args = parser.parse_args()
    if args.cases < 1:
        parser.error("--cases must be positive")

    model_checks = run_model(args.cases, args.seed)
    source_checks = check_repository(args.repo_root.resolve()) if args.repo_root else 0
    suffix = " and %s source checks" % format(source_checks, ",") if args.repo_root else ""
    print(
        "PASS: startup application degradation compatibility evidence verified with %s model assertions across %s randomized histories%s."
        % (format(model_checks, ","), format(args.cases, ","), suffix)
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
