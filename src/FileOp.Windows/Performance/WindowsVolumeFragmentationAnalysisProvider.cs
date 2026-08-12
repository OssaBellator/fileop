using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using FileOp.Core.Performance;

namespace FileOp.Windows.Performance;

internal sealed record WindowsVolumeFragmentationMetrics(
    uint FilePercentFragmentation,
    double AverageFragmentsPerFile,
    ulong TotalFiles,
    ulong TotalFragmentedFiles,
    ulong TotalFreeSpaceExtents,
    ulong LargestFreeSpaceExtentBytes,
    double AverageFreeSpacePerExtentBytes,
    ulong VolumeSizeBytes,
    ulong UsedSpaceBytes,
    ulong FreeSpaceBytes);

internal sealed record WindowsVolumeFragmentationApiResult(
    uint ReturnCode,
    bool? DefragRecommended,
    WindowsVolumeFragmentationMetrics? Metrics);

internal interface IWindowsVolumeFragmentationApi
{
    ValueTask<WindowsVolumeFragmentationApiResult> AnalyzeAsync(
        string canonicalVolumeRoot,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class WindowsVolumeFragmentationApi : IWindowsVolumeFragmentationApi
{
    private const int MaximumEnumeratedVolumes = 128;

    public async ValueTask<WindowsVolumeFragmentationApiResult> AnalyzeAsync(
        string canonicalVolumeRoot,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalVolumeRoot);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        cancellationToken.ThrowIfCancellationRequested();

        var connectionOptions = new ConnectionOptions
        {
            Timeout = timeout,
        };
        var scope = new ManagementScope(@"\\.\root\cimv2", connectionOptions);
        scope.Connect();
        cancellationToken.ThrowIfCancellationRequested();

        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery("SELECT Name FROM Win32_Volume"),
            new EnumerationOptions
            {
                ReturnImmediately = true,
                Rewindable = false,
                Timeout = timeout,
            });
        using var volumes = searcher.Get();

        ManagementObject? target = null;
        var enumerated = 0;
        foreach (ManagementObject volume in volumes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            enumerated++;
            if (enumerated > MaximumEnumeratedVolumes)
            {
                volume.Dispose();
                target?.Dispose();
                throw new InvalidDataException(
                    $"Win32_Volume enumeration exceeded FileOp's {MaximumEnumeratedVolumes:N0}-volume lookup bound.");
            }

            var name = volume["Name"] as string;
            if (string.Equals(name, canonicalVolumeRoot, StringComparison.OrdinalIgnoreCase))
            {
                target = volume;
                break;
            }

            volume.Dispose();
        }

        if (target is null)
        {
            throw new FileNotFoundException(
                $"Win32_Volume did not expose the requested local volume root {canonicalVolumeRoot}.");
        }

        using (target)
        using (var output = await InvokeDefragAnalysisAsync(
            target,
            timeout,
            cancellationToken).ConfigureAwait(false))
        {
            var returnCode = ReadUInt32(output, "ReturnValue");
            if (returnCode != 0)
            {
                return new WindowsVolumeFragmentationApiResult(
                    returnCode,
                    null,
                    null);
            }

            var recommended = ReadBoolean(output, "DefragRecommended");
            if (output["DefragAnalysis"] is not ManagementBaseObject analysis)
            {
                throw new InvalidDataException(
                    "Win32_Volume.DefragAnalysis returned success without a Win32_DefragAnalysis payload.");
            }

            using (analysis)
            {
                var metrics = new WindowsVolumeFragmentationMetrics(
                    ReadUInt32(analysis, "FilePercentFragmentation"),
                    ReadDouble(analysis, "AverageFragmentsPerFile"),
                    ReadUInt64(analysis, "TotalFiles"),
                    ReadUInt64(analysis, "TotalFragmentedFiles"),
                    ReadUInt64(analysis, "TotalFreeSpaceExtents"),
                    ReadUInt64(analysis, "LargestFreeSpaceExtent"),
                    ReadDouble(analysis, "AverageFreeSpacePerExtent"),
                    ReadUInt64(analysis, "VolumeSize"),
                    ReadUInt64(analysis, "UsedSpace"),
                    ReadUInt64(analysis, "FreeSpace"));
                return new WindowsVolumeFragmentationApiResult(
                    returnCode,
                    recommended,
                    metrics);
            }
        }
    }

    private static async Task<ManagementBaseObject> InvokeDefragAnalysisAsync(
        ManagementObject volume,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var observer = new ManagementOperationObserver();
        var completion = new TaskCompletionSource<ManagementBaseObject>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ManagementBaseObject? output = null;

        observer.ObjectReady += (_, eventArgs) =>
        {
            var previous = Interlocked.Exchange(ref output, eventArgs.NewObject);
            previous?.Dispose();
        };
        observer.Completed += (_, eventArgs) =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Exchange(ref output, null)?.Dispose();
                completion.TrySetCanceled(cancellationToken);
                return;
            }
            if (eventArgs.Status != ManagementStatus.NoError)
            {
                Interlocked.Exchange(ref output, null)?.Dispose();
                completion.TrySetException(new InvalidOperationException(
                    $"Asynchronous Win32_Volume.DefragAnalysis completed with WMI status {eventArgs.Status}."));
                return;
            }

            var result = Interlocked.Exchange(ref output, null);
            if (result is null)
            {
                completion.TrySetException(new InvalidDataException(
                    "Asynchronous Win32_Volume.DefragAnalysis completed without output parameters."));
                return;
            }
            completion.TrySetResult(result);
        };

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                observer.Cancel();
            }
            catch (ManagementException)
            {
                // The completion callback or linked timeout remains the source of truth.
            }
        });

        volume.InvokeMethod(
            observer,
            "DefragAnalysis",
            null,
            new InvokeMethodOptions { Timeout = timeout });

        try
        {
            return await completion.Task.ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Exchange(ref output, null)?.Dispose();
            throw;
        }
    }

    private static uint ReadUInt32(ManagementBaseObject value, string propertyName) =>
        Convert.ToUInt32(RequireProperty(value, propertyName), CultureInfo.InvariantCulture);

    private static ulong ReadUInt64(ManagementBaseObject value, string propertyName) =>
        Convert.ToUInt64(RequireProperty(value, propertyName), CultureInfo.InvariantCulture);

    private static double ReadDouble(ManagementBaseObject value, string propertyName) =>
        Convert.ToDouble(RequireProperty(value, propertyName), CultureInfo.InvariantCulture);

    private static bool ReadBoolean(ManagementBaseObject value, string propertyName) =>
        Convert.ToBoolean(RequireProperty(value, propertyName), CultureInfo.InvariantCulture);

    private static object RequireProperty(ManagementBaseObject value, string propertyName) =>
        value[propertyName] ?? throw new InvalidDataException(
            $"Win32_Volume.DefragAnalysis omitted required property {propertyName}.");
}

public sealed class WindowsVolumeFragmentationAnalysisProvider
    : IVolumeFragmentationAnalysisProvider
{
    private readonly IWindowsVolumeFragmentationApi _api;

    public WindowsVolumeFragmentationAnalysisProvider()
        : this(new WindowsVolumeFragmentationApi())
    {
    }

    internal WindowsVolumeFragmentationAnalysisProvider(
        IWindowsVolumeFragmentationApi api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    public async ValueTask<VolumeFragmentationAnalysisResult> AnalyzeAsync(
        string volumeRoot,
        VolumeFragmentationAnalysisBudget budget,
        CancellationToken cancellationToken = default)
    {
        var canonicalRoot = VolumeFragmentationDriveRoot.RequireCanonical(volumeRoot);
        ArgumentNullException.ThrowIfNull(budget);
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindows())
        {
            return VolumeFragmentationAnalysisResult.Unavailable(
                canonicalRoot,
                budget,
                VolumeFragmentationAnalysisStatus.Unsupported,
                null,
                TimeSpan.Zero,
                "Legacy Win32_Volume fragmentation analysis compatibility evidence is available only on Windows.");
        }

        var started = Stopwatch.GetTimestamp();
        using var timeoutCancellation = new CancellationTokenSource(budget.Timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);

        try
        {
            var raw = await _api.AnalyzeAsync(
                canonicalRoot,
                budget.Timeout,
                linkedCancellation.Token).ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(started);

            if (raw.ReturnCode != 0)
            {
                var status = raw.ReturnCode switch
                {
                    1 => VolumeFragmentationAnalysisStatus.PermissionRequired,
                    2 => VolumeFragmentationAnalysisStatus.Unsupported,
                    6 => VolumeFragmentationAnalysisStatus.Cancelled,
                    _ => VolumeFragmentationAnalysisStatus.Unavailable,
                };
                return VolumeFragmentationAnalysisResult.Unavailable(
                    canonicalRoot,
                    budget,
                    status,
                    raw.ReturnCode,
                    elapsed,
                    DescribeProviderReturnCode(raw.ReturnCode));
            }

            if (raw.DefragRecommended is not { } recommended ||
                raw.Metrics is not { } metrics)
            {
                return VolumeFragmentationAnalysisResult.Unavailable(
                    canonicalRoot,
                    budget,
                    VolumeFragmentationAnalysisStatus.Unavailable,
                    0,
                    elapsed,
                    "Win32_Volume.DefragAnalysis returned success without complete structured fragmentation evidence.");
            }

            VolumeFragmentationEvidence evidence;
            try
            {
                evidence = new VolumeFragmentationEvidence(
                    canonicalRoot,
                    recommended,
                    metrics.FilePercentFragmentation,
                    metrics.AverageFragmentsPerFile,
                    metrics.TotalFiles,
                    metrics.TotalFragmentedFiles,
                    metrics.TotalFreeSpaceExtents,
                    metrics.LargestFreeSpaceExtentBytes,
                    metrics.AverageFreeSpacePerExtentBytes,
                    metrics.VolumeSizeBytes,
                    metrics.UsedSpaceBytes,
                    metrics.FreeSpaceBytes);
            }
            catch (ArgumentException exception)
            {
                return VolumeFragmentationAnalysisResult.Unavailable(
                    canonicalRoot,
                    budget,
                    VolumeFragmentationAnalysisStatus.Unavailable,
                    0,
                    elapsed,
                    $"Win32_Volume.DefragAnalysis returned malformed structured evidence: {exception.Message}");
            }

            return VolumeFragmentationAnalysisResult.Completed(
                budget,
                evidence,
                elapsed,
                "Windows legacy Win32_Volume.DefragAnalysis compatibility evidence completed. The Windows-reported recommendation and fragmentation fields are evidence only; FileOp did not run defrag or retrim and does not convert this result into an optimization recommendation.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            return VolumeFragmentationAnalysisResult.Unavailable(
                canonicalRoot,
                budget,
                VolumeFragmentationAnalysisStatus.Cancelled,
                null,
                Stopwatch.GetElapsedTime(started),
                $"Win32_Volume.DefragAnalysis exceeded FileOp's explicit {budget.Timeout.TotalSeconds:N0}-second analysis timeout and cancellation was requested.");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure(
                canonicalRoot,
                budget,
                VolumeFragmentationAnalysisStatus.PermissionRequired,
                started,
                exception.Message);
        }
        catch (ManagementException exception)
        {
            var status = exception.ErrorCode switch
            {
                ManagementStatus.AccessDenied => VolumeFragmentationAnalysisStatus.PermissionRequired,
                ManagementStatus.NotSupported or
                ManagementStatus.InvalidClass or
                ManagementStatus.InvalidMethod or
                ManagementStatus.MethodNotImplemented => VolumeFragmentationAnalysisStatus.Unsupported,
                ManagementStatus.CallCanceled => VolumeFragmentationAnalysisStatus.Cancelled,
                _ => VolumeFragmentationAnalysisStatus.Unavailable,
            };
            return Failure(
                canonicalRoot,
                budget,
                status,
                started,
                $"WMI {exception.ErrorCode}: {exception.Message}");
        }
        catch (Exception exception) when (
            exception is COMException or
            FileNotFoundException or
            InvalidDataException or
            InvalidOperationException or
            NotSupportedException)
        {
            return Failure(
                canonicalRoot,
                budget,
                VolumeFragmentationAnalysisStatus.Unavailable,
                started,
                exception.Message);
        }
    }

    private static VolumeFragmentationAnalysisResult Failure(
        string canonicalRoot,
        VolumeFragmentationAnalysisBudget budget,
        VolumeFragmentationAnalysisStatus status,
        long started,
        string detail) =>
        VolumeFragmentationAnalysisResult.Unavailable(
            canonicalRoot,
            budget,
            status,
            null,
            Stopwatch.GetElapsedTime(started),
            $"Win32_Volume fragmentation analysis compatibility evidence was unavailable: {detail}");

    private static string DescribeProviderReturnCode(uint returnCode) =>
        returnCode switch
        {
            1 => "Win32_Volume.DefragAnalysis returned Access Denied (1). No fragmentation evidence was produced.",
            2 => "Win32_Volume.DefragAnalysis returned Not Supported (2). No fragmentation evidence was produced.",
            3 => "Win32_Volume.DefragAnalysis returned Volume Dirty Bit Is Set (3). No fragmentation evidence was produced.",
            4 => "Win32_Volume.DefragAnalysis returned Not Enough Free Space (4). No fragmentation evidence was produced.",
            5 => "Win32_Volume.DefragAnalysis returned Corrupt Master File Table Detected (5). No fragmentation evidence was produced.",
            6 => "Win32_Volume.DefragAnalysis returned Call Canceled (6). No fragmentation evidence was produced.",
            7 => "Win32_Volume.DefragAnalysis returned Call Cancellation Request Too Late (7). FileOp does not treat that as completed analysis evidence.",
            8 => "Win32_Volume.DefragAnalysis reported that the defrag engine is already running (8). No fragmentation evidence was produced.",
            9 => "Win32_Volume.DefragAnalysis could not connect to the defrag engine (9). No fragmentation evidence was produced.",
            10 => "Win32_Volume.DefragAnalysis reported a defrag engine error (10). No fragmentation evidence was produced.",
            11 => "Win32_Volume.DefragAnalysis returned Unknown Error (11). No fragmentation evidence was produced.",
            _ => $"Win32_Volume.DefragAnalysis returned unrecognized provider code {returnCode}. No fragmentation evidence was produced.",
        };
}
