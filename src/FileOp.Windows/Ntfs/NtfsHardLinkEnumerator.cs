using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FileOp.Windows.Ntfs;

public sealed class NtfsHardLinkEnumerator : INtfsHardLinkEnumerator
{
    private const int InitialBufferLength = 1024;
    private const int ErrorHandleEof = 38;
    private const int ErrorMoreData = 234;

    public IReadOnlyList<string> Enumerate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var rootPath = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("The hard-link path must have a volume root.", nameof(path));
        }

        // FindFirstFileNameW is still subject to MAX_PATH unless the hosting process opts
        // into long-path behavior. The NTFS engine can run under hosts other than FileOp.App,
        // so use the extended-length form here rather than relying on each host manifest.
        var apiPath = ToExtendedLengthPath(fullPath);
        var buffer = new char[InitialBufferLength];
        uint length = (uint)buffer.Length;
        var handle = FindFirstFileNameW(apiPath, 0, ref length, buffer);

        if (handle.IsInvalid && Marshal.GetLastWin32Error() == ErrorMoreData)
        {
            handle.Dispose();
            buffer = new char[checked((int)length)];
            length = (uint)buffer.Length;
            handle = FindFirstFileNameW(apiPath, 0, ref length, buffer);
        }

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(
                error,
                $"Could not enumerate hard links for {fullPath}: {new Win32Exception(error).Message}");
        }

        using (handle)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ExpandLinkName(rootPath, ReadBuffer(buffer)),
            };

            while (true)
            {
                length = (uint)buffer.Length;
                if (FindNextFileNameW(handle, ref length, buffer))
                {
                    result.Add(ExpandLinkName(rootPath, ReadBuffer(buffer)));
                    continue;
                }

                var error = Marshal.GetLastWin32Error();
                if (error == ErrorHandleEof)
                {
                    break;
                }

                if (error == ErrorMoreData)
                {
                    buffer = new char[checked((int)length)];
                    length = (uint)buffer.Length;
                    if (FindNextFileNameW(handle, ref length, buffer))
                    {
                        result.Add(ExpandLinkName(rootPath, ReadBuffer(buffer)));
                        continue;
                    }

                    error = Marshal.GetLastWin32Error();
                    if (error == ErrorHandleEof)
                    {
                        break;
                    }
                }

                throw new Win32Exception(
                    error,
                    $"Could not continue hard-link enumeration for {fullPath}: {new Win32Exception(error).Message}");
            }

            return result.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    internal static string ExpandLinkName(string rootPath, string linkName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(linkName);

        var relativeName = linkName.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(rootPath, relativeName));
    }

    private static string ToExtendedLengthPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return path;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\\?\UNC\" + path[2..];
        }

        return @"\\?\" + path;
    }

    private static string ReadBuffer(char[] buffer)
    {
        var terminator = Array.IndexOf(buffer, '\0');
        var length = terminator >= 0 ? terminator : buffer.Length;
        return new string(buffer, 0, length);
    }

    private sealed class SafeFindFileHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeFindFileHandle()
            : base(true)
        {
        }

        protected override bool ReleaseHandle() => FindClose(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFindFileHandle FindFirstFileNameW(
        string lpFileName,
        uint dwFlags,
        ref uint StringLength,
        [Out] char[] LinkName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextFileNameW(
        SafeFindFileHandle hFindStream,
        ref uint StringLength,
        [Out] char[] LinkName);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr hFindFile);
}
