using System.IO.Pipes;
using FileOp.Core.Indexing.Service;

namespace FileOp.Windows.IndexingService;

public sealed class IndexingPipeServer
{
    private readonly string _pipeName;
    private readonly IndexingServiceDispatcher _dispatcher;

    public IndexingPipeServer(string pipeName, IndexingServiceDispatcher dispatcher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public async Task RunSingleClientAsync(CancellationToken cancellationToken = default)
    {
        using var pipe = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
        {
            IndexingServiceRequest? request;
            try
            {
                request = await IndexingPipeTransport.ReadAsync<IndexingServiceRequest>(pipe, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException) when (!pipe.IsConnected)
            {
                break;
            }

            if (request is null)
            {
                break;
            }

            var response = await _dispatcher.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
            try
            {
                await IndexingPipeTransport.WriteAsync(pipe, response, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (!pipe.IsConnected)
            {
                break;
            }
        }
    }
}
