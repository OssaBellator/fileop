using FileOp.Core.Models;
using FileOp.Core.Storage;

namespace FileOp.Core.Search;

public sealed class InMemoryFileIndex : IFileIndex, IStorageAnalytics, IDisposable
{
    private const int MaximumStorageEntryLimit = 4_096;
    private readonly List<FileRecord> _records = [];
    private readonly ReaderWriterLockSlim _gate = new(LockRecursionPolicy.NoRecursion);

    public int Count
    {
        get
        {
            _gate.EnterReadLock();
            try
            {
                return _records.Count;
            }
            finally
            {
                _gate.ExitReadLock();
            }
        }
    }

    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _gate.EnterWriteLock();
        try
        {
            _records.Clear();
        }
        finally
        {
            _gate.ExitWriteLock();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask AddBatchAsync(IReadOnlyList<FileRecord> records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        cancellationToken.ThrowIfCancellationRequested();

        _gate.EnterWriteLock();
        try
        {
            foreach (var record in records)
            {
                Upsert(record);
            }
        }
        finally
        {
            _gate.ExitWriteLock();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ApplyChangesAsync(IReadOnlyList<FileIndexChange> changes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        cancellationToken.ThrowIfCancellationRequested();

        _gate.EnterWriteLock();
        try
        {
            foreach (var change in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                switch (change.Kind)
                {
                    case FileIndexChangeKind.Upsert when change.Record is { } record:
                        Upsert(record);
                        break;
                    case FileIndexChangeKind.Delete when change.Identity is { } identity:
                        Delete(identity, change.Path);
                        break;
                    default:
                        throw new InvalidOperationException("The file-index change is missing the data required by its change kind.");
                }
            }
        }
        finally
        {
            _gate.ExitWriteLock();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<FileRecord>> SearchAsync(
        FileSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var matches = new List<(FileRecord Record, int Score)>(Math.Min(query.Limit, 256));

        _gate.EnterReadLock();
        try
        {
            foreach (var record in _records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Matches(record, query))
                {
                    continue;
                }

                matches.Add((record, Score(record, query)));
            }
        }
        finally
        {
            _gate.ExitReadLock();
        }

        IReadOnlyList<FileRecord> result = matches
            .OrderByDescending(static match => match.Score)
            .ThenBy(static match => match.Record.Name, StringComparer.OrdinalIgnoreCase)
            .Take(query.Limit)
            .Select(static match => match.Record)
            .ToArray();

        return ValueTask.FromResult(result);
    }

    public ValueTask<StorageDirectoryAnalysis> AnalyzeDirectoryAsync(
        string rootPath,
        int maxEntries = 256,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (maxEntries <= 0 || maxEntries > MaximumStorageEntryLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxEntries),
                maxEntries,
                $"Storage analysis entry limits must be between 1 and {MaximumStorageEntryLimit:N0}.");
        }

        var normalizedRoot = NormalizeIndexedPath(rootPath);
        var aggregates = new Dictionary<string, MutableStorageAggregate>(StringComparer.OrdinalIgnoreCase);

        _gate.EnterReadLock();
        try
        {
            var recordsByPath = _records.ToDictionary(
                static record => record.Path,
                StringComparer.OrdinalIgnoreCase);
            var scopedRecords = new List<(FileRecord Record, string DirectPath)>();

            foreach (var record in _records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directPath = FindDirectChildPath(record, normalizedRoot);
                if (directPath is not null)
                {
                    scopedRecords.Add((record, directPath));
                }
            }

            var canonicalHardLinkPaths = scopedRecords
                .Where(static item => !item.Record.IsDirectory && item.Record.Identity.HasValue)
                .GroupBy(static item => item.Record.Identity!.Value)
                .ToDictionary(
                    static group => group.Key,
                    static group => group
                        .Select(static item => item.Record.Path)
                        .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                        .First());

            foreach (var item in scopedRecords)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = item.Record;
                var directPath = item.DirectPath;

                if (!aggregates.TryGetValue(directPath, out var aggregate))
                {
                    recordsByPath.TryGetValue(directPath, out var directRecord);
                    aggregate = new MutableStorageAggregate(
                        directPath,
                        directRecord?.Name ?? GetIndexedName(directPath),
                        directRecord?.IsDirectory ?? !string.Equals(record.Path, directPath, StringComparison.OrdinalIgnoreCase));
                    aggregates.Add(directPath, aggregate);
                }

                var ownsPhysicalAllocation = record.IsDirectory ||
                    record.Identity is null ||
                    string.Equals(
                        canonicalHardLinkPaths[record.Identity.Value],
                        record.Path,
                        StringComparison.OrdinalIgnoreCase);
                aggregate.Add(record, ownsPhysicalAllocation);
            }
        }
        finally
        {
            _gate.ExitReadLock();
        }

        var allEntries = aggregates.Values
            .Select(static aggregate => aggregate.ToEntry())
            .OrderByDescending(static entry => entry.TreemapBytes)
            .ThenByDescending(static entry => entry.LogicalBytes)
            .ThenBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var logicalBytes = allEntries.Sum(static entry => entry.LogicalBytes);
        var allocatedBytes = allEntries.All(static entry => entry.AllocatedBytes.HasValue)
            ? allEntries.Sum(static entry => entry.AllocatedBytes!.Value)
            : null;

        return ValueTask.FromResult(new StorageDirectoryAnalysis(
            normalizedRoot,
            logicalBytes,
            allocatedBytes,
            allEntries.Sum(static entry => entry.FileCount),
            allEntries.Sum(static entry => entry.DirectoryCount),
            allEntries.Sum(static entry => entry.HardLinkAliasCount),
            allEntries.Length,
            allEntries.Take(maxEntries).ToArray()));
    }

    private void Upsert(FileRecord record)
    {
        var existingIndex = _records.FindIndex(
            item => string.Equals(item.Path, record.Path, StringComparison.OrdinalIgnoreCase));

        if (existingIndex >= 0)
        {
            _records[existingIndex] = record;
        }
        else
        {
            _records.Add(record);
        }
    }

    private void Delete(FileIdentity identity, string? path)
    {
        _records.RemoveAll(item =>
            item.Identity == identity &&
            (path is null || string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool Matches(FileRecord record, FileSearchQuery query)
    {
        if (query.Extensions.Count > 0)
        {
            var extension = record.Extension.TrimStart('.');
            if (!query.Extensions.Contains(extension))
            {
                return false;
            }
        }

        var hasSizeFilter = query.ExactSize.HasValue || query.MinimumSize.HasValue || query.MaximumSize.HasValue;
        if (hasSizeFilter && record.IsDirectory)
        {
            return false;
        }

        if (query.ExactSize is { } exactSize && record.Length != exactSize)
        {
            return false;
        }

        if (query.MinimumSize is { } lowerBound && record.Length <= lowerBound)
        {
            return false;
        }

        if (query.MaximumSize is { } upperBound && record.Length >= upperBound)
        {
            return false;
        }

        foreach (var term in query.Terms)
        {
            if (!record.Name.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                !record.Path.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static int Score(FileRecord record, FileSearchQuery query)
    {
        var score = 0;
        foreach (var term in query.Terms)
        {
            if (record.Name.Equals(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 100;
            }
            else if (record.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 50;
            }
            else if (record.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 20;
            }
            else if (record.Path.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 5;
            }
        }

        return score;
    }

    private static string? FindDirectChildPath(FileRecord record, string rootPath)
    {
        if (string.Equals(NormalizeIndexedPath(record.ParentPath), rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return record.Path;
        }

        var prefix = rootPath.EndsWith('\\') || rootPath.EndsWith('/')
            ? rootPath
            : rootPath + "\\";
        if (!record.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var remainder = record.Path[prefix.Length..];
        if (remainder.Length == 0)
        {
            return null;
        }

        var separatorIndex = remainder.IndexOfAny(['\\', '/']);
        var childName = separatorIndex < 0 ? remainder : remainder[..separatorIndex];
        return prefix + childName;
    }

    private static string NormalizeIndexedPath(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.Length == 3 &&
            char.IsLetter(trimmed[0]) &&
            trimmed[1] == ':' &&
            (trimmed[2] == '\\' || trimmed[2] == '/'))
        {
            return $"{char.ToUpperInvariant(trimmed[0])}:\\";
        }

        trimmed = trimmed.TrimEnd('\\', '/');
        return string.IsNullOrEmpty(trimmed) ? path : trimmed;
    }

    private static string GetIndexedName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var separatorIndex = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        return separatorIndex < 0 ? trimmed : trimmed[(separatorIndex + 1)..];
    }

    public void Dispose() => _gate.Dispose();

    private sealed class MutableStorageAggregate
    {
        private long _allocatedBytes;
        private bool _allocatedKnown = true;
        private int _fileCount;
        private int _directoryCount;
        private int _hardLinkAliasCount;

        public MutableStorageAggregate(string path, string name, bool isDirectory)
        {
            Path = path;
            Name = name;
            IsDirectory = isDirectory;
        }

        public string Path { get; }

        public string Name { get; }

        public bool IsDirectory { get; }

        public long LogicalBytes { get; private set; }

        public void Add(FileRecord record, bool ownsPhysicalAllocation)
        {
            if (record.IsDirectory)
            {
                _directoryCount++;
                return;
            }

            _fileCount++;
            LogicalBytes += record.Length;
            if (!ownsPhysicalAllocation)
            {
                _hardLinkAliasCount++;
                return;
            }

            if (record.AllocatedLength is { } allocatedLength)
            {
                _allocatedBytes += allocatedLength;
            }
            else
            {
                _allocatedKnown = false;
            }
        }

        public StorageDirectoryEntry ToEntry() => new(
            Path,
            Name,
            IsDirectory,
            LogicalBytes,
            _allocatedKnown ? _allocatedBytes : null,
            _fileCount,
            _directoryCount,
            _hardLinkAliasCount);
    }
}
