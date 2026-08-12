using System.Diagnostics;
using System.Runtime.InteropServices;
using FileOp.Core.Performance;

namespace FileOp.Windows.Performance;

internal sealed record WindowsPhysicalMemorySnapshot(
    ulong TotalPhysicalBytes,
    ulong AvailablePhysicalBytes,
    uint MemoryLoadPercent);

internal sealed record WindowsPhysicalMemoryQueryResult(
    WindowsPhysicalMemorySnapshot? Snapshot,
    int Win32Error)
{
    public bool Succeeded => Snapshot is not null;
}

internal interface IWindowsSystemPhysicalMemoryApi
{
    WindowsPhysicalMemoryQueryResult Query();
}

internal sealed class WindowsSystemPhysicalMemoryApi
    : IWindowsSystemPhysicalMemoryApi
{
    public WindowsPhysicalMemoryQueryResult Query()
    {
        var native = new MemoryStatusEx
        {
            Length = checked((uint)Marshal.SizeOf<MemoryStatusEx>()),
        };
        if (!GlobalMemoryStatusEx(ref native))
        {
            return new WindowsPhysicalMemoryQueryResult(
                null,
                Marshal.GetLastWin32Error());
        }

        return new WindowsPhysicalMemoryQueryResult(
            new WindowsPhysicalMemorySnapshot(
                native.TotalPhysical,
                native.AvailablePhysical,
                native.MemoryLoad),
            0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
}

public sealed class WindowsSystemPhysicalMemoryStatusProvider
    : ISystemPhysicalMemoryStatusProvider
{
    private readonly IWindowsSystemPhysicalMemoryApi _api;

    public WindowsSystemPhysicalMemoryStatusProvider()
        : this(new WindowsSystemPhysicalMemoryApi())
    {
    }

    internal WindowsSystemPhysicalMemoryStatusProvider(
        IWindowsSystemPhysicalMemoryApi api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    public SystemPhysicalMemoryStatusResult Query()
    {
        if (!OperatingSystem.IsWindows())
        {
            return SystemPhysicalMemoryStatusResult.Unavailable(
                SystemPhysicalMemoryStatusAvailability.Unsupported,
                TimeSpan.Zero,
                "Windows GlobalMemoryStatusEx physical-memory evidence is available only on Windows.");
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            var raw = _api.Query();
            var elapsed = Stopwatch.GetElapsedTime(started);
            if (!raw.Succeeded || raw.Snapshot is not { } snapshot)
            {
                return SystemPhysicalMemoryStatusResult.Unavailable(
                    SystemPhysicalMemoryStatusAvailability.Unavailable,
                    elapsed,
                    raw.Win32Error == 0
                        ? "GlobalMemoryStatusEx returned no physical-memory evidence."
                        : $"GlobalMemoryStatusEx failed with Win32 error {raw.Win32Error}.");
            }

            SystemPhysicalMemoryStatus status;
            try
            {
                status = new SystemPhysicalMemoryStatus(
                    snapshot.TotalPhysicalBytes,
                    snapshot.AvailablePhysicalBytes,
                    snapshot.MemoryLoadPercent);
            }
            catch (ArgumentException exception)
            {
                return SystemPhysicalMemoryStatusResult.Unavailable(
                    SystemPhysicalMemoryStatusAvailability.Unavailable,
                    elapsed,
                    $"GlobalMemoryStatusEx returned malformed physical-memory evidence: {exception.Message}");
            }

            return SystemPhysicalMemoryStatusResult.Available(
                status,
                elapsed,
                "Windows GlobalMemoryStatusEx physical-memory evidence captured. Windows memory load is preserved as an approximate reported percentage; FileOp does not convert these counters into a memory-health score or a RAM-cleanup recommendation.");
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or
            EntryPointNotFoundException or
            TypeInitializationException)
        {
            return SystemPhysicalMemoryStatusResult.Unavailable(
                SystemPhysicalMemoryStatusAvailability.Unsupported,
                Stopwatch.GetElapsedTime(started),
                $"GlobalMemoryStatusEx is unavailable on this Windows environment: {exception.Message}");
        }
    }
}
