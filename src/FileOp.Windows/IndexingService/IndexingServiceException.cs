using FileOp.Core.Indexing.Service;

namespace FileOp.Windows.IndexingService;

public sealed class IndexingServiceException : Exception
{
    public IndexingServiceException(
        IndexingServiceErrorCode code,
        string message,
        bool canRetry = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        CanRetry = canRetry;
    }

    public IndexingServiceErrorCode Code { get; }

    public bool CanRetry { get; }
}
