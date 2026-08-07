using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FileOp.Core.Models;

namespace FileOp.Core.Indexing;

public sealed class FileSystemCrawler
{
    public async IAsyncEnumerable<FileRecord> CrawlAsync(
        string rootPath,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var root = Path.GetFullPath(rootPath);
        var channel = Channel.CreateBounded<FileRecord>(new BoundedChannelOptions(4096)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        _ = Task.Run(async () =>
        {
            Exception? error = null;
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    MatchType = MatchType.Simple,
                };

                var rootDirectory = new DirectoryInfo(root);
                foreach (var info in rootDirectory.EnumerateFileSystemInfos("*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await channel.Writer.WriteAsync(FileRecord.FromFileSystemInfo(info), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                error = exception;
            }
            finally
            {
                channel.Writer.TryComplete(error);
            }
        }, CancellationToken.None);

        await foreach (var record in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return record;
        }
    }
}
