using FileOp.Core.Models;

namespace FileOp.Core.Search;

public interface IFileDirectoryBrowser
{
    ValueTask<FileDirectoryBrowsePage> BrowseDirectoryAsync(
        string directoryPath,
        int pageSize = 256,
        FileDirectoryBrowseCursor? cursor = null,
        CancellationToken cancellationToken = default);
}
