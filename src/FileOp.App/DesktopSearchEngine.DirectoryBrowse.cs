using FileOp.Core.Indexing.Service;
using FileOp.Core.Models;
using FileOp.Core.Search;
using FileOp.Windows.IndexingService;

namespace FileOp.App;

internal sealed partial class DesktopSearchEngine
{
    private const int MaximumDirectoryBrowsePageSize = 1_024;

    public async ValueTask<FileDirectoryBrowsePage> BrowseDirectoryAsync(
        string directoryPath,
        int pageSize = 256,
        FileDirectoryBrowseCursor? cursor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ThrowIfDisposed();
        if (pageSize <= 0 || pageSize > MaximumDirectoryBrowsePageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                pageSize,
                $"Directory browse page sizes must be between 1 and {MaximumDirectoryBrowsePageSize:N0}.");
        }

        var fullPath = NormalizeBrowsePath(directoryPath);
        ValidateBrowseCursor(cursor, fullPath);

        await _searchOperationGate.WaitAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_nativeSession is { Client.IsConnected: true } && _primaryVolume is not null)
            {
                await _nativeOperationGate.WaitAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
                try
                {
                    if (_nativeSession is { Client.IsConnected: true } session &&
                        _primaryVolume is { } volume)
                    {
                        EnsurePathWithinRoot(fullPath, volume.RootPath);
                        return await BrowseNativeDirectoryAsync(
                            session,
                            volume,
                            fullPath,
                            pageSize,
                            cursor).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _nativeOperationGate.Release();
                }
            }

            if (!_fallbackReady || string.IsNullOrWhiteSpace(_fallbackRoot))
            {
                throw new InvalidOperationException("Directory browsing is not currently available.");
            }

            EnsurePathWithinRoot(fullPath, _fallbackRoot);
            return await BrowseFallbackDirectoryAsync(fullPath, pageSize, cursor).ConfigureAwait(false);
        }
        finally
        {
            _searchOperationGate.Release();
        }
    }

    private async ValueTask<FileDirectoryBrowsePage> BrowseNativeDirectoryAsync(
        IndexingServiceProcessSession session,
        IndexingVolumeDescriptor volume,
        string directoryPath,
        int pageSize,
        FileDirectoryBrowseCursor? cursor)
    {
        var requestedPageSize = pageSize;
        while (true)
        {
            try
            {
                var response = await session.Client.BrowseDirectoryAsync(
                    new IndexingDirectoryBrowseRequest(
                        volume.VolumeIdentity,
                        volume.RootPath,
                        directoryPath,
                        requestedPageSize,
                        cursor),
                    _lifetimeCancellation.Token).ConfigureAwait(false);
                return new FileDirectoryBrowsePage(
                    response.DirectoryPath,
                    response.TotalCount,
                    response.Entries.Select(ToFileRecord).ToArray(),
                    response.NextCursor);
            }
            catch (IndexingServiceRemoteException exception)
                when (exception.Error.Code == IndexingServiceErrorCode.ResponseTooLarge && requestedPageSize > 16)
            {
                requestedPageSize = Math.Max(16, requestedPageSize / 2);
            }
        }
    }

    private async ValueTask<FileDirectoryBrowsePage> BrowseFallbackDirectoryAsync(
        string directoryPath,
        int pageSize,
        FileDirectoryBrowseCursor? cursor)
    {
        // The fallback source is already a completed in-memory crawler snapshot. Reading
        // it here does not touch the filesystem again. Native mode uses the SQLite
        // parent-identity path instead and does not materialize the full index.
        var snapshot = await _fallbackIndex.SearchAsync(
            FileSearchQuery.Parse(string.Empty, int.MaxValue),
            _lifetimeCancellation.Token).ConfigureAwait(false);
        var directChildren = snapshot
            .Where(record =>
                !string.IsNullOrWhiteSpace(record.ParentPath) &&
                string.Equals(
                    NormalizeBrowsePath(record.ParentPath),
                    directoryPath,
                    StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static record => record.IsDirectory)
            .ThenBy(static record => record.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static record => record.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        IEnumerable<FileRecord> remaining = directChildren;
        if (cursor is not null)
        {
            remaining = remaining.Where(record => CompareBrowseOrder(record, cursor) > 0);
        }

        var pageEntries = remaining.Take(pageSize + 1).ToList();
        FileDirectoryBrowseCursor? nextCursor = null;
        if (pageEntries.Count > pageSize)
        {
            pageEntries.RemoveAt(pageEntries.Count - 1);
            var last = pageEntries[^1];
            nextCursor = new FileDirectoryBrowseCursor(last.IsDirectory, last.Name, last.Path);
        }

        return new FileDirectoryBrowsePage(
            directoryPath,
            directChildren.Length,
            pageEntries,
            nextCursor);
    }

    private static int CompareBrowseOrder(FileRecord record, FileDirectoryBrowseCursor cursor)
    {
        var recordRank = record.IsDirectory ? 0 : 1;
        var cursorRank = cursor.IsDirectory ? 0 : 1;
        var rankComparison = recordRank.CompareTo(cursorRank);
        if (rankComparison != 0)
        {
            return rankComparison;
        }

        var nameComparison = StringComparer.OrdinalIgnoreCase.Compare(record.Name, cursor.Name);
        return nameComparison != 0
            ? nameComparison
            : StringComparer.OrdinalIgnoreCase.Compare(record.Path, cursor.Path);
    }

    private static void ValidateBrowseCursor(FileDirectoryBrowseCursor? cursor, string directoryPath)
    {
        if (cursor is null)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(cursor.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(cursor.Path);
        if (!Path.IsPathFullyQualified(cursor.Path))
        {
            throw new ArgumentException("Directory browse cursor paths must be absolute.", nameof(cursor));
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(cursor.Path));
        if (string.IsNullOrWhiteSpace(parent) ||
            !string.Equals(
                NormalizeBrowsePath(parent),
                directoryPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The directory browse cursor does not belong to the requested directory.",
                nameof(cursor));
        }
    }

    private static string NormalizeBrowsePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(root) &&
            string.Equals(
                fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
