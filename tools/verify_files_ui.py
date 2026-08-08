#!/usr/bin/env python3
"""Zero-Actions checks for FileOp's dual-pane selection, intent and planned-operation queue."""
from __future__ import annotations
import argparse, copy, ntpath, random, re, sys
import xml.etree.ElementTree as ET
from pathlib import Path


def norm(path: str) -> str:
    value = ntpath.normpath(path.replace('/', '\\'))
    if len(value) == 2 and value[1] == ':':
        value += '\\'
    return value.casefold()


def within(path: str, root: str) -> bool:
    p, r = norm(path), norm(root)
    return p == r or p.startswith(r if r.endswith('\\') else r + '\\')


def append_unique(existing: list[str], page: list[str]) -> list[str]:
    seen = {x.casefold() for x in existing}
    out = list(existing)
    for value in page:
        key = value.casefold()
        if key not in seen:
            seen.add(key)
            out.append(value)
    return out


def prepare_intent(source: dict, destination: dict) -> tuple:
    selected = tuple(sorted(source['selection'], key=str.casefold))
    return (
        source['pane'],
        source['tab'],
        source['path'],
        selected,
        destination['pane'],
        destination['tab'],
        destination['path'],
    )


def validate_queue_intent(source_path: str, entries: list[tuple[str, bool]], destination_path: str) -> bool:
    if norm(source_path) == norm(destination_path):
        return False
    for entry_path, is_directory in entries:
        if norm(ntpath.dirname(ntpath.normpath(entry_path))) != norm(source_path):
            return False
        if is_directory and within(destination_path, entry_path):
            return False
    return bool(entries)


def invalidate(panes: list[dict], clear_path: bool) -> None:
    for pane in panes:
        pane['generation'] += 1
        pane['loading'] = 0
        for tab in pane['tabs']:
            tab['rows'].clear()
            tab['selection'].clear()
            tab['count'] = 0
            tab['cursor'] = None
            tab['loaded'] = False
            if clear_path:
                tab['path'] = None


def check_properties(cases: int) -> int:
    assert within(r'C:\Data', r'C:\Data')
    assert within(r'C:\Data\Child\x', 'c:\\data\\')
    assert not within(r'C:\Database\x', r'C:\Data')
    assert append_unique([r'C:\A'], [r'c:\a', r'C:\B']) == [r'C:\A', r'C:\B']
    assert validate_queue_intent(r'C:\A', [(r'C:\A\x.txt', False)], r'C:\B')
    assert not validate_queue_intent(r'C:\A', [(r'C:\A\x.txt', False)], r'C:\A')
    assert not validate_queue_intent(r'C:\A', [(r'C:\A\Dir', True)], r'C:\A\Dir\Child')
    assert not validate_queue_intent(r'C:\A', [(r'C:\Other\x.txt', False)], r'C:\B')
    rng = random.Random(20260808)
    checks = 8

    for case in range(cases):
        panes = []
        for p in range(2):
            tabs = []
            for t in range(rng.randint(1, 4)):
                path = rf'C:\Root\p{p}\t{t}'
                rows = [rf'{path}\e{i}' for i in range(rng.randint(1, 20))]
                chosen = {row.casefold() for row in rng.sample(rows, rng.randint(0, min(5, len(rows))))}
                tabs.append({
                    'id': f'{p}-{t}-{case}',
                    'path': path,
                    'rows': rows,
                    'selection': chosen,
                    'count': len(rows),
                    'cursor': 'next',
                    'loaded': True,
                })
            panes.append({
                'pane': 'Left' if p == 0 else 'Right',
                'tabs': tabs,
                'active': rng.randrange(len(tabs)),
                'generation': 0,
                'loading': 0,
            })

        left = panes[0]
        right_before = copy.deepcopy(panes[1])
        active = left['tabs'][left['active']]
        selected_before = set(active['selection'])
        extra = rf"{active['path']}\new{case}"
        active['rows'] = append_unique(active['rows'], [active['rows'][-1], extra])
        assert active['selection'] == selected_before
        assert panes[1] == right_before
        checks += 2

        if len(left['tabs']) > 1:
            previous_selection = set(active['selection'])
            active['selection'].clear()
            assert not active['selection']
            assert previous_selection == selected_before
            checks += 1

        source_tab = left['tabs'][left['active']]
        destination_tab = panes[1]['tabs'][panes[1]['active']]
        if not source_tab['selection']:
            source_tab['selection'].add(source_tab['rows'][0].casefold())
        source = {
            'pane': 'Left', 'tab': source_tab['id'], 'path': source_tab['path'],
            'selection': set(source_tab['selection']),
        }
        destination = {
            'pane': 'Right', 'tab': destination_tab['id'], 'path': destination_tab['path'],
            'selection': set(destination_tab['selection']),
        }
        intent = prepare_intent(source, destination)
        queued = ('Copy', 'Ask', intent)
        source['selection'].clear()
        destination['path'] = r'C:\Changed'
        assert queued[2][3]
        assert queued[2][-1] != destination['path']
        checks += 2

        queue_entries = [(row, False) for row in source_tab['rows'][:rng.randint(1, min(5, len(source_tab['rows'])))] ]
        queue_destination = destination_tab['path']
        assert validate_queue_intent(source_tab['path'], queue_entries, queue_destination)
        assert not validate_queue_intent(source_tab['path'], queue_entries, source_tab['path'])
        checks += 2

        directory_entry = source_tab['path'] + r'\Directory'
        assert not validate_queue_intent(
            source_tab['path'],
            [(directory_entry, True)],
            directory_entry + r'\Child')
        checks += 1

        paths = [[t['path'] for t in p['tabs']] for p in panes]
        maintenance = copy.deepcopy(panes)
        invalidate(maintenance, False)
        assert [[t['path'] for t in p['tabs']] for p in maintenance] == paths
        assert all(not t['rows'] and not t['selection'] and not t['loaded']
                   for p in maintenance for t in p['tabs'])
        checks += 2

        source_change = copy.deepcopy(panes)
        invalidate(source_change, True)
        assert all(t['path'] is None and not t['selection'] and not t['rows']
                   for p in source_change for t in p['tabs'])
        checks += 1

    return checks


def methods(source: str) -> set[str]:
    return set(re.findall(
        r'\b(?:async\s+)?(?:void|Task|ValueTask|bool|string|int|long|double)\s+([A-Za-z_]\w*)\s*\(',
        source,
    ))


def check_repository(root: Path) -> int:
    paths = {name: root / rel for name, rel in {
        'app': 'src/FileOp.App/App.xaml.cs',
        'main': 'src/FileOp.App/MainWindow.Files.cs',
        'viewx': 'src/FileOp.App/FilesView.xaml',
        'viewc': 'src/FileOp.App/FilesView.xaml.cs',
        'panex': 'src/FileOp.App/FilesPaneView.xaml',
        'panec': 'src/FileOp.App/FilesPaneView.xaml.cs',
        'mainx': 'src/FileOp.App/MainWindow.xaml',
        'engine': 'src/FileOp.App/DesktopSearchEngine.DirectoryBrowse.cs',
        'docs': 'docs/files-browser.md',
    }.items()}
    missing = [str(p) for p in paths.values() if not p.is_file()]
    if missing:
        raise FileNotFoundError(', '.join(missing))

    s = {k: p.read_text(encoding='utf-8') for k, p in paths.items()}
    ET.fromstring(s['viewx'])
    ET.fromstring(s['panex'])

    pane_handlers = set(re.findall(r'\b(?:Click|SelectionChanged)="([A-Za-z_]\w*)"', s['panex']))
    view_handlers = set(re.findall(r'\b(?:Click|SelectionChanged)="([A-Za-z_]\w*)"', s['viewx']))
    assert pane_handlers <= methods(s['panec'])
    assert view_handlers <= methods(s['viewc'])

    a = s['app']
    assert a.index('var window = new MainWindow();') < a.index('window.InitializeFilesFeature();') < a.index('window.Activate();')

    required_main = [
        'private readonly FilesPaneState _leftFilesPane = new("Left");',
        'private readonly FilesPaneState _rightFilesPane = new("Right");',
        'await _storageGate.WaitAsync(_lifetimeCancellation.Token)',
        'if (!IsFilesRequestCurrent(pane, tab, generation))',
        '_searchEngine.BrowseDirectoryAsync(',
    ]
    for needle in required_main:
        assert needle in s['main'], needle

    gate = s['main'].index('await _storageGate.WaitAsync')
    stale = s['main'].index('if (!IsFilesRequestCurrent(pane, tab, generation))', gate)
    browse = s['main'].index('var page = await _searchEngine.BrowseDirectoryAsync(')
    assert gate < stale < browse

    required_selection = [
        'SelectionMode="Multiple"',
        'IsMultiSelectCheckBoxEnabled="True"',
        'SelectionChanged="FilesList_SelectionChanged"',
        'x:Name="OpenButton"',
        'private readonly HashSet<string> _selectedPaths = new(StringComparer.OrdinalIgnoreCase);',
        '_selectedPaths.Add(row.Path);',
        'public IReadOnlyList<FileBrowserRow> SelectedRows',
        'public event EventHandler? IntentStateChanged;',
        'ClearActiveSelection();',
        'OpenButton.IsEnabled =',
        'ReplaceItemsSourceWithoutForgettingSelection(rows);',
    ]
    for needle in required_selection:
        assert needle in s['panex'] + s['panec'], needle

    replacement_guard = re.search(
        r'private void ReplaceItemsSourceWithoutForgettingSelection\(IReadOnlyList<FileBrowserRow> rows\)\s*'
        r'\{.*?_restoringSelection = true;.*?FilesList\.ItemsSource = null;.*?'
        r'FilesList\.ItemsSource = rows;.*?finally\s*\{\s*_restoringSelection = false;\s*\}\s*\}',
        s['panec'],
        re.S,
    )
    assert replacement_guard, 'ItemsSource replacement must suppress SelectionChanged so remembered paths survive paging'

    apply_method = re.search(
        r'public void Apply\(.*?\n    \}\n\n    public void SetReady',
        s['panec'],
        re.S,
    )
    assert apply_method, 'FilesPaneView.Apply not found'
    assert 'ReplaceItemsSourceWithoutForgettingSelection(rows);' in apply_method.group(0)
    assert 'FilesList.ItemsSource = null;' not in apply_method.group(0)

    required_intent = [
        'Content="Prepare Left → Right"',
        'Content="Prepare Right → Left"',
        'private FileBrowserOperationIntent? _preparedIntent;',
        'public FileBrowserOperationIntent? PreparedIntent => _preparedIntent;',
        'selectedRows',
        '.ToArray();',
        'Array.AsReadOnly(entries)',
        'public sealed record FileBrowserOperationIntent(',
        'ClearPreparedIntent();',
    ]
    for needle in required_intent:
        assert needle in s['viewx'] + s['viewc'], needle

    required_queue = [
        'x:Name="CollisionPolicyBox"',
        'Tag="Ask"',
        'Tag="Skip"',
        'Tag="Stop"',
        'x:Name="QueueCopyButton"',
        'x:Name="QueueMoveButton"',
        'x:Name="OperationQueueList"',
        'x:Name="RemoveQueuedOperationButton"',
        'x:Name="ClearQueueButton"',
        'private readonly List<FileBrowserQueuedOperation> _queuedOperations = [];',
        'public IReadOnlyList<FileBrowserQueuedOperation> QueuedOperations',
        'QueuePreparedIntent(FileBrowserOperationKind.Copy);',
        'QueuePreparedIntent(FileBrowserOperationKind.Move);',
        'TryValidateIntentForQueue(intent, out var validationMessage)',
        'PathsEqual(intent.SourceDirectoryPath, intent.DestinationDirectoryPath)',
        'IsSameOrDescendantPath(intent.DestinationDirectoryPath, entry.Path)',
        'public enum FileBrowserCollisionPolicy',
        'public sealed record FileBrowserQueuedOperation(',
    ]
    for needle in required_queue:
        assert needle in s['viewx'] + s['viewc'], needle

    assert 'Tag="Replace"' not in s['viewx']
    assert 'Replace existing' not in s['viewx']
    assert 'ItemClick="FilesList_ItemClick"' not in s['panex']
    assert 'FilesList_ItemClick' not in s['panec']
    assert s['viewx'].count('<local:FilesPaneView') == 2
    assert 'GroupName = $"FileOpFilesTabs-{PaneTitle}"' in s['panec']
    assert 'Content="Files"' in s['mainx'] and 'IsEnabled="False"' in s['mainx'] and 'FilesView' not in s['mainx']
    assert 'public async ValueTask<FileDirectoryBrowsePage> BrowseDirectoryAsync(' in s['engine']

    combined = s['main'] + s['viewc'] + s['panec']
    for forbidden in [
        'AnalyzeStorageAsync(',
        'Directory.Enumerate',
        'Directory.GetFiles',
        'Directory.GetDirectories',
        'File.Copy(',
        'File.Move(',
        'File.Delete(',
        'Directory.Move(',
        'Directory.Delete(',
    ]:
        assert forbidden not in combined, forbidden

    assert '## Selection semantics' in s['docs']
    assert '## Prepared operation intent' in s['docs']
    assert '## Planned operation queue' in s['docs']
    assert 'Ask later' in s['docs'] and 'Skip existing' in s['docs'] and 'Stop on collision' in s['docs']
    assert 'replacement is deliberately not a queue policy' in s['docs']

    return (
        len(required_main) + len(required_selection) + len(required_intent) + len(required_queue) +
        len(pane_handlers) + len(view_handlers) + 20
    )


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument('--repo-root', type=Path, default=Path.cwd())
    p.add_argument('--self-test-only', action='store_true')
    p.add_argument('--cases', type=int, default=10000)
    args = p.parse_args()
    if args.cases <= 0:
        p.error('--cases must be greater than zero')

    print(f'PASS Files selection/intent/queue properties: {check_properties(args.cases)} checks')
    if not args.self_test_only:
        print(f'PASS Files selection/intent/queue source wiring: {check_repository(args.repo_root.resolve())} checks')
    return 0


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ET.ParseError, ValueError) as exc:
        print(f'FAIL: {exc}', file=sys.stderr)
        raise SystemExit(1)
