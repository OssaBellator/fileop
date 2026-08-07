using FileOp.Windows.IndexingService;

namespace FileOp.Indexer;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!TryParseArguments(args, out var pipeName, out var databaseDirectory, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        using var lifetimeCancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            lifetimeCancellation.Cancel();
        };

        try
        {
            using var backend = new NtfsIndexingServiceBackend(databaseDirectory);
            var dispatcher = new IndexingServiceDispatcher(backend);
            var server = new IndexingPipeServer(pipeName, dispatcher);
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
    }

    private static bool TryParseArguments(
        IReadOnlyList<string> args,
        out string pipeName,
        out string databaseDirectory,
        out string error)
    {
        pipeName = string.Empty;
        databaseDirectory = string.Empty;
        error = string.Empty;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--pipe" when index + 1 < args.Count:
                    pipeName = args[++index];
                    break;

                case "--database-directory" when index + 1 < args.Count:
                    databaseDirectory = args[++index];
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

        if (string.IsNullOrWhiteSpace(databaseDirectory))
        {
            error = "FileOp.Indexer requires --database-directory <path>.";
            return false;
        }

        databaseDirectory = Path.GetFullPath(databaseDirectory);
        return true;
    }
}
