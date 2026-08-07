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
            _records.AddRange(records);
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

        if (query.MinimumSize is { } minimumSize &&
            query.MaximumSize is { } maximumSize &&
            minimumSize == maximumSize)
        {
            if (record.Length != minimumSize)
            {
                return false;
            }
        }
        else
        {
            if (query.MinimumSize is { } lowerBound && record.Length <= lowerBound)
            {
                return false;
            }

            if (query.MaximumSize is { } upperBound && record.Length >= upperBound)
            {
                return false;
            }
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
