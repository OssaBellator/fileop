using System.Runtime.InteropServices;

namespace FileOp.Windows.Storage;

public sealed class WindowsKnownFolderPathResolver
{
    private const uint CoinitApartmentThreaded = 0x2;
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private static readonly Guid DownloadsFolderId =
        new("374DE290-123F-4565-9164-39C4925E467B");

    public string GetDownloadsPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows known-folder resolution is available only on Windows.");
        }

        var initializeResult = CoInitializeEx(IntPtr.Zero, CoinitApartmentThreaded);
        var mustUninitialize = initializeResult >= 0;
        if (initializeResult < 0 && initializeResult != RpcEChangedMode)
        {
            Marshal.ThrowExceptionForHR(initializeResult);
        }

        IntPtr pathPointer = IntPtr.Zero;
        try
        {
            var folderId = DownloadsFolderId;
            var result = SHGetKnownFolderPath(
                ref folderId,
                0,
                IntPtr.Zero,
                out pathPointer);
            if (result < 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }

            var path = Marshal.PtrToStringUni(pathPointer);
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidDataException(
                    "Windows returned an empty Downloads known-folder path.");
            }

            return Path.GetFullPath(path);
        }
        finally
        {
            if (pathPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pathPointer);
            }
            if (mustUninitialize)
            {
                CoUninitialize();
            }
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        ref Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);
}
