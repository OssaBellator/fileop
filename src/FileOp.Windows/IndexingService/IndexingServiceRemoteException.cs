using FileOp.Core.Indexing.Service;

namespace FileOp.Windows.IndexingService;

public sealed class IndexingServiceRemoteException : Exception
{
    public IndexingServiceRemoteException(IndexingServiceError error)
        : base(error?.Message)
    {
        Error = error ?? throw new ArgumentNullException(nameof(error));
    }

    public IndexingServiceError Error { get; }
}
