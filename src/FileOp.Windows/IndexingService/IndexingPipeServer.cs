using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using FileOp.Core.Indexing.Service;
using Microsoft.Win32.SafeHandles;

namespace FileOp.Windows.IndexingService;

public sealed class IndexingPipeServer
{
    private readonly string _pipeName;
    private readonly int _expectedClientProcessId;
    private readonly IndexingServiceDispatcher _dispatcher;

    public IndexingPipeServer(
        string pipeName,
        int expectedClientProcessId,
        IndexingServiceDispatcher dispatcher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedClientProcessId);
        _pipeName = pipeName;
        _expectedClientProcessId = expectedClientProcessId;
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

        await WaitForExpectedClientAsync(pipe, cancellationToken).ConfigureAwait(false);
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
            catch (InvalidDataException) when (response.Success)
            {
                var tooLarge = new IndexingServiceResponse(
                    IndexingServiceProtocol.CurrentVersion,
                    request.RequestId,
                    false,
                    JsonSerializer.SerializeToElement<object?>(null),
                    new IndexingServiceError(
                        IndexingServiceErrorCode.ResponseTooLarge,
                        "The indexing service result exceeded the IPC frame limit. Retry with a smaller result limit.",
                        CanRetry: true));
                await IndexingPipeTransport.WriteAsync(pipe, tooLarge, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (!pipe.IsConnected)
            {
                break;
            }
        }
    }

    private async Task WaitForExpectedClientAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "FileOp.Indexer could not identify the process connected to its named pipe.");
            }

            if (processId == (uint)_expectedClientProcessId)
            {
                return;
            }

            // The pipe name is random and CurrentUserOnly, but an unrelated process under
            // the same Windows account must still not be able to claim an elevated indexing
            // channel. Reject it and continue waiting for the exact desktop process that
            // launched this helper.
            pipe.Disconnect();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);
}
