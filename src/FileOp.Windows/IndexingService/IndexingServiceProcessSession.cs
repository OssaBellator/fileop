using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;

namespace FileOp.Windows.IndexingService;

public sealed class IndexingServiceProcessSession : IAsyncDisposable
{
    private const int ErrorCancelled = 1223;
    private readonly Process _process;
    private bool _disposed;

    private IndexingServiceProcessSession(Process process, IndexingServiceClient client, bool isElevated)
    {
        _process = process;
        Client = client;
        IsElevated = isElevated;
    }

    public IndexingServiceClient Client { get; }

    public bool IsElevated { get; }

    public static async Task<IndexingServiceProcessSession> StartAsync(
        string serviceExecutablePath,
        bool elevated,
        TimeSpan? connectTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceExecutablePath);

        if (elevated && !WindowsProcessElevation.CanElevateCurrentIdentityInPlace())
        {
            throw new InvalidOperationException(
                "This indexing session cannot use credential-over-the-shoulder elevation. " +
                "FileOp only elevates the helper when the current account has a split administrator token, " +
                "so the elevated helper keeps the same Windows user identity as the current-user-only pipe.");
        }

        var executable = Path.GetFullPath(serviceExecutablePath);
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("The FileOp indexing helper executable was not found.", executable);
        }

        var pipeName = $"fileop-indexer-{Environment.ProcessId}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}";
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (elevated)
        {
            startInfo.Verb = "runas";
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows did not start the FileOp indexing helper.");
        }
        catch (Win32Exception exception) when (elevated && exception.NativeErrorCode == ErrorCancelled)
        {
            throw new OperationCanceledException("Administrative indexing access was declined.", exception);
        }

        var client = new IndexingServiceClient(pipeName);
        try
        {
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(connectTimeout ?? TimeSpan.FromSeconds(30));
            try
            {
                await client.ConnectAsync(timeoutCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The FileOp indexing helper did not open its IPC channel in time.");
            }

            var hello = await client.HelloAsync("FileOp desktop", cancellationToken).ConfigureAwait(false);
            if (elevated && !hello.IsElevated)
            {
                throw new InvalidOperationException(
                    "The FileOp indexing helper was requested with elevation but did not receive an elevated token.");
            }

            return new IndexingServiceProcessSession(process, client, hello.IsElevated);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            TryTerminate(process);
            process.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await Client.DisposeAsync().ConfigureAwait(false);

        if (!_process.HasExited)
        {
            using var exitCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await _process.WaitForExitAsync(exitCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryTerminate(_process);
            }
        }

        _process.Dispose();
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }
}
