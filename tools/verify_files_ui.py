#!/usr/bin/env python3
"""Zero-Actions checks for FileOp's exact dual-pane Files UI."""
from __future__ import annotations
import argparse, copy, random, re, sys
import xml.etree.ElementTree as ET
from pathlib import Path


def norm(path: str) -> str:
    value = path.replace('/', '\\').rstrip('\\')
    if len(value) == 2 and value[1] == ':': value += '\\'
    return value.casefold()


def within(path: str, root: str) -> bool:
    p, r = norm(path), norm(root)
    return p == r or p.startswith(r if r.endswith('\\') else r + '\\')


def append_unique(existing: list[str], page: list[str]) -> list[str]:
    seen = {x.casefold() for x in existing}; out = list(existing)
    for value in page:
        if value.casefold() not in seen:
            seen.add(value.casefold()); out.append(value)
    return out


def invalidate(panes: list[dict], clear_path: bool) -> None:
    for pane in panes:
        pane['generation'] += 1; pane['loading'] = 0
        for tab in pane['tabs']:
            tab['rows'].clear(); tab['count'] = 0; tab['cursor'] = None; tab['loaded'] = False
            if clear_path: tab['path'] = None


def check_properties(cases: int) -> int:
    assert within(r'C:\Data', r'C:\Data')
    assert within(r'C:\Data\Child\x', 'c:\\data\\')
    assert not within(r'C:\Database\x', r'C:\Data')
    assert append_unique([r'C:\A'], [r'c:\a', r'C:\B']) == [r'C:\A', r'C:\B']
    rng = random.Random(20260808); checks = 4
    for case in range(cases):
        panes = []
        for p in range(2):
            tabs = []
            for t in range(rng.randint(1, 4)):
                path = rf'C:\Root\p{p}\t{t}'
                rows = [rf'{path}\e{i}' for i in range(rng.randint(0, 16))]
                tabs.append({'path': path, 'rows': rows, 'count': len(rows), 'cursor': 'next', 'loaded': True})
            panes.append({'tabs': tabs, 'active': rng.randrange(len(tabs)), 'generation': 0, 'loading': 0})
        right = copy.deepcopy(panes[1]); active = panes[0]['tabs'][panes[0]['active']]
        active['rows'] = append_unique(active['rows'], [*active['rows'][-1:], rf"{active['path']}\new{case}"])
        panes[0]['generation'] += 1; assert panes[1] == right; checks += 1
        paths = [[t['path'] for t in p['tabs']] for p in panes]
        maintenance = copy.deepcopy(panes); invalidate(maintenance, False)
        assert [[t['path'] for t in p['tabs']] for p in maintenance] == paths
        assert all(not t['rows'] and not t['loaded'] for p in maintenance for t in p['tabs']); checks += 2
        source = copy.deepcopy(panes); invalidate(source, True)
        assert all(t['path'] is None and not t['rows'] for p in source for t in p['tabs']); checks += 1
    return checks


def methods(source: str) -> set[str]:
    return set(re.findall(r'\b(?:async\s+)?(?:void|Task|ValueTask|bool|string|int|long|double)\s+([A-Za-z_]\w*)\s*\(', source))


def check_repository(root: Path) -> int:
    paths = {name: root / rel for name, rel in {
        'app':'src/FileOp.App/App.xaml.cs', 'main':'src/FileOp.App/MainWindow.Files.cs',
        'viewx':'src/FileOp.App/FilesView.xaml', 'viewc':'src/FileOp.App/FilesView.xaml.cs',
        'panex':'src/FileOp.App/FilesPaneView.xaml', 'panec':'src/FileOp.App/FilesPaneView.xaml.cs',
        'mainx':'src/FileOp.App/MainWindow.xaml', 'engine':'src/FileOp.App/DesktopSearchEngine.DirectoryBrowse.cs'}.items()}
    missing = [str(p) for p in paths.values() if not p.is_file()]
    if missing: raise FileNotFoundError(', '.join(missing))
    s = {k:p.read_text(encoding='utf-8') for k,p in paths.items()}
    ET.fromstring(s['viewx']); ET.fromstring(s['panex'])
    handlers = set(re.findall(r'\b(?:Click|ItemClick)="([A-Za-z_]\w*)"', s['panex']))
    assert handlers <= methods(s['panec'])
    a = s['app']; assert a.index('var window = new MainWindow();') < a.index('window.InitializeFilesFeature();') < a.index('window.Activate();')
    required = [
        'private readonly FilesPaneState _leftFilesPane = new("Left");',
        'private readonly FilesPaneState _rightFilesPane = new("Right");',
        'public List<FilesTabState> Tabs { get; } = [];', 'public Guid ActiveTabId { get; set; }',
        'public int Generation { get; set; }', 'public int LoadingGeneration { get; set; }',
        'public List<FileBrowserRow> Rows { get; } = [];', 'public FileDirectoryBrowseCursor? NextCursor { get; set; }',
        'ResetAllFilesTabs(clearPath: false)', 'ResetAllFilesTabs(clearPath: true)',
        'await _storageGate.WaitAsync(_lifetimeCancellation.Token)', '_searchEngine.BrowseDirectoryAsync(',
        'if (!IsFilesRequestCurrent(pane, tab, generation))']
    for needle in required: assert needle in s['main'], needle
    gate = s['main'].index('await _storageGate.WaitAsync')
    stale = s['main'].index('if (!IsFilesRequestCurrent(pane, tab, generation))', gate)
    browse = s['main'].index('var page = await _searchEngine.BrowseDirectoryAsync(')
    assert gate < stale < browse
    for forbidden in ['AnalyzeStorageAsync(', 'StorageDirectoryAnalysis', 'Directory.Enumerate', '_filesRows', '_filesGeneration', '_filesLoadedForSource']:
        assert forbidden not in s['main'], forbidden
    assert s['viewx'].count('<local:FilesPaneView') == 2
    assert 'public FilesPaneView LeftPane => LeftPaneControl;' in s['viewc'] and 'public FilesPaneView RightPane => RightPaneControl;' in s['viewc']
    assert (s['viewc'] + s['panec']).count('public sealed record FileBrowserRow(') == 1
    assert 'GroupName = $"FileOpFilesTabs-{PaneTitle}"' in s['panec']
    assert 'Content="Files"' in s['mainx'] and 'IsEnabled="False"' in s['mainx'] and 'FilesView' not in s['mainx']
    assert 'public async ValueTask<FileDirectoryBrowsePage> BrowseDirectoryAsync(' in s['engine']
    return len(required) + 6 + len(handlers) + 7


def main() -> int:
    p = argparse.ArgumentParser(); p.add_argument('--repo-root', type=Path, default=Path.cwd()); p.add_argument('--self-test-only', action='store_true'); p.add_argument('--cases', type=int, default=10000)
    args = p.parse_args()
    if args.cases <= 0: p.error('--cases must be greater than zero')
    print(f'PASS exact dual-pane Files UI properties: {check_properties(args.cases)} checks')
    if not args.self_test_only: print(f'PASS exact dual-pane Files UI/source wiring: {check_repository(args.repo_root.resolve())} checks')
    return 0

if __name__ == '__main__':
    try: raise SystemExit(main())
    except (AssertionError, FileNotFoundError, ET.ParseError, ValueError) as exc:
        print(f'FAIL: {exc}', file=sys.stderr); raise SystemExit(1)
