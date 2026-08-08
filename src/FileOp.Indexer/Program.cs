using System.Diagnostics;
using FileOp.Windows.IndexingService;

namespace FileOp.Indexer;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!TryParseArguments(args, out var pipeName, out var parentProcessId, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            Console.Error.WriteLine("FileOp.Indexer could not resolve LocalApplicationData for the current user.");
            return 1;
        }

        Process parentProcess;
        try
        {
            parentProcess = Process.GetProcessById(parentProcessId);
        }
        catch (ArgumentException)
        {
            return 0;
        }

        using (parentProcess)
        using (var lifetimeCancellation = new CancellationTokenSource())
        {
            void CancelLifetime(object? sender, EventArgs eventArgs) => lifetimeCancellation.Cancel();

            try
            {
                parentProcess.EnableRaisingEvents = true;
                parentProcess.Exited += CancelLifetime;
                if (parentProcess.HasExited)
                {
                    lifetimeCancellation.Cancel();
                }

                Console.CancelKeyPress += CancelOnConsoleInterrupt;
                var databaseDirectory = Path.Combine(localAppData, "FileOp", "Index");
                using var backend = new StorageHistoryIndexingServiceBackend(databaseDirectory);
                var dispatcher = new IndexingServiceDispatcher(backend);
                var server = new IndexingPipeServer(pipeName, parentProcessId, dispatcher);
                await server.RunSingleClientAsync(lifetimeCancellation.Token).ConfigureAwait(false);
                return 0;
            }
            catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
            {
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FileOp.Indexer failed: {exception}");
                return 1;
            }
            finally
            {
                parentProcess.Exited -= CancelLifetime;
                Console.CancelKeyPress -= CancelOnConsoleInterrupt;
            }

            void CancelOnConsoleInterrupt(object? sender, ConsoleCancelEventArgs eventArgs)
            {
                eventArgs.Cancel = true;
                lifetimeCancellation.Cancel();
            }
        }
    }

    private static bool TryParseArguments(
        string[] args,
        out string pipeName,
        out int parentProcessId,
        out string error)
    {
        pipeName = string.Empty;
        parentProcessId = 0;
        error = string.Empty;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--pipe" when index + 1 < args.Length:
                    pipeName = args[++index];
                    break;

                case "--parent-pid" when index + 1 < args.Length &&
                    int.TryParse(args[index + 1], out var parsedProcessId) && parsedProcessId > 0:
                    parentProcessId = parsedProcessId;
                    index++;
                    break;

                default:
                    error = $"Unknown or incomplete FileOp.Indexer argument: {args[index]}";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(pipeName))
        {
            error = "FileOp.Indexer requires --pipe <name>.";
            return false;
        }

        if (pipeName.Length > 200 || pipeName.IndexOfAny(['\\', '/', ':']) >= 0)
        {
            error = "FileOp.Indexer received an invalid pipe name.";
            return false;
        }

        if (parentProcessId <= 0)
        {
            error = "FileOp.Indexer requires --parent-pid <positive process id>.";
            return false;
        }

        return true;
    }
}