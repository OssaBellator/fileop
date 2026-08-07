using FileOp.Core.Models;

namespace FileOp.Core.Search;

public sealed class InMemoryFileIndex : IFileIndex, IDisposable
{
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

    public void Dispose() => _gate.Dispose();
}
