using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FileOp.Core.Performance;
using Microsoft.Win32.SafeHandles;

namespace FileOp.Windows.Performance;

internal sealed record WindowsPhysicalDiskFailurePredictionQueryResult(
    byte[]? Data,
    int Win32Error)
{
    public bool Succeeded => Data is not null;
}

internal interface IWindowsPhysicalDiskFailurePredictionQueryApi
{
    WindowsPhysicalDiskFailurePredictionQueryResult Query(SafeFileHandle handle);
}

internal sealed class WindowsPhysicalDiskFailurePredictionQueryApi
    : IWindowsPhysicalDiskFailurePredictionQueryApi
{
    // CTL_CODE(FILE_DEVICE_MASS_STORAGE=0x2D, 0x0440,
    // METHOD_BUFFERED=0, FILE_ANY_ACCESS=0).
    internal const uint IoctlStoragePredictFailure = 0x002D1100;
    internal const int StoragePredictFailureBytes = 4 + 512;

    public WindowsPhysicalDiskFailurePredictionQueryResult Query(
        SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsInvalid || handle.IsClosed)
        {
            throw new ArgumentException(
                "A valid physical-disk handle is required.",
                nameof(handle));
        }

        var output = new byte[StoragePredictFailureBytes];
        if (!DeviceIoControlNoInput(
                handle,
                IoctlStoragePredictFailure,
                IntPtr.Zero,
                0,
                output,
                checked((uint)output.Length),
                out var bytesReturned,
                IntPtr.Zero))
        {
            return new WindowsPhysicalDiskFailurePredictionQueryResult(
                null,
                Marshal.GetLastWin32Error());
        }

        if (bytesReturned > (uint)output.Length)
        {
            return new WindowsPhysicalDiskFailurePredictionQueryResult(
                null,
                13); // ERROR_INVALID_DATA.
        }

        Array.Resize(ref output, checked((int)bytesReturned));
        return new WindowsPhysicalDiskFailurePredictionQueryResult(output, 0);
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "DeviceIoControl",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlNoInput(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        [Out] byte[] lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}

public sealed class WindowsPhysicalDiskFailurePredictionProvider
    : IPhysicalDiskFailurePredictionProvider
{
    private const int ErrorInvalidFunction = 1;
    private readonly IWindowsPhysicalDiskStorageApi _storageApi;
    private readonly IWindowsPhysicalDiskFailurePredictionQueryApi _predictionApi;

    public WindowsPhysicalDiskFailurePredictionProvider()
        : this(
            new WindowsPhysicalDiskStorageApi(),
            new WindowsPhysicalDiskFailurePredictionQueryApi())
    {
    }

    internal WindowsPhysicalDiskFailurePredictionProvider(
        IWindowsPhysicalDiskStorageApi storageApi,
        IWindowsPhysicalDiskFailurePredictionQueryApi predictionApi)
    {
        _storageApi = storageApi ?? throw new ArgumentNullException(nameof(storageApi));
        _predictionApi = predictionApi ?? throw new ArgumentNullException(nameof(predictionApi));
    }

    public PhysicalDiskFailurePredictionResult Query(int physicalDiskNumber)
    {
        if (physicalDiskNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalDiskNumber));
        }
        if (!OperatingSystem.IsWindows())
        {
            return PhysicalDiskFailurePredictionResult.Unavailable(
                physicalDiskNumber,
                PhysicalDiskFailurePredictionStatus.Unsupported,
                TimeSpan.Zero,
                "Windows storage-stack failure prediction is available only on Windows.");
        }

        var started = Stopwatch.GetTimestamp();
        var open = _storageApi.Open(physicalDiskNumber);
        if (!open.Opened)
        {
            return PhysicalDiskFailurePredictionResult.Unavailable(
                physicalDiskNumber,
                PhysicalDiskFailurePredictionStatus.Unavailable,
                Stopwatch.GetElapsedTime(started),
                $"PhysicalDrive{physicalDiskNumber} could not be opened through FileOp's zero-access storage metadata boundary (Win32 {open.Win32Error}).");
        }

        using var handle = open.Handle!;
        var query = _predictionApi.Query(handle);
        var elapsed = Stopwatch.GetElapsedTime(started);
        if (!query.Succeeded)
        {
            var status = query.Win32Error == ErrorInvalidFunction
                ? PhysicalDiskFailurePredictionStatus.Unsupported
                : PhysicalDiskFailurePredictionStatus.Unavailable;
            var interpretation = status == PhysicalDiskFailurePredictionStatus.Unsupported
                ? "The Windows storage stack does not expose failure prediction for this device."
                : "The Windows storage-stack failure-prediction query could not be completed.";
            return PhysicalDiskFailurePredictionResult.Unavailable(
                physicalDiskNumber,
                status,
                elapsed,
                $"{interpretation} PhysicalDrive{physicalDiskNumber} query returned Win32 {query.Win32Error}.");
        }

        uint rawPredictFailure;
        try
        {
            rawPredictFailure = WindowsPhysicalDiskFailurePredictionParser.Parse(
                query.Data!);
        }
        catch (InvalidDataException exception)
        {
            return PhysicalDiskFailurePredictionResult.Unavailable(
                physicalDiskNumber,
                PhysicalDiskFailurePredictionStatus.Unavailable,
                elapsed,
                $"PhysicalDrive{physicalDiskNumber} returned malformed failure-prediction data: {exception.Message}");
        }

        var evidence = new PhysicalDiskFailurePredictionEvidence(
            physicalDiskNumber,
            rawPredictFailure);
        var detail = evidence.FailurePredicted
            ? "Windows storage-stack failure prediction is currently nonzero. This is reported failure-prediction evidence, not a FileOp severity score."
            : "Windows storage-stack failure prediction is currently zero. This means no failure is predicted by this interface at this time; it is not a comprehensive health verdict.";
        return PhysicalDiskFailurePredictionResult.Available(
            evidence,
            elapsed,
            detail);
    }
}

internal static class WindowsPhysicalDiskFailurePredictionParser
{
    internal const int RequiredBytes = 4 + 512;

    public static uint Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < RequiredBytes)
        {
            throw new InvalidDataException(
                $"STORAGE_PREDICT_FAILURE returned {data.Length} byte(s); at least {RequiredBytes} are required.");
        }

        // VendorSpecific[512] is deliberately ignored. Its format is vendor
        // specific and is not part of FileOp's reviewed evidence contract.
        return BinaryPrimitives.ReadUInt32LittleEndian(data[..4]);
    }
}
