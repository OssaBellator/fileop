using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FileOp.Core.Operations;
using Microsoft.Win32.SafeHandles;

namespace FileOp.Windows.Operations;

internal static class WindowsFileSecurityDescriptorDigest
{
    private const int ErrorInsufficientBuffer = 122;
    private const uint MaximumDescriptorBytes = 1024u * 1024u;

    internal static FileSecurityDescriptorEvidence Read(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsInvalid)
        {
            throw new IOException("Cannot query security evidence from an invalid file handle.");
        }

        var requested = FileSecurityDescriptorEvidence.QueriedSecurityInformationMask;
        var succeeded = GetKernelObjectSecurity(
            handle,
            requested,
            IntPtr.Zero,
            0,
            out var bytesNeeded);
        if (succeeded || bytesNeeded == 0)
        {
            throw new IOException("GetKernelObjectSecurity did not report a required self-relative descriptor buffer size.");
        }

        var sizeError = Marshal.GetLastWin32Error();
        if (sizeError != ErrorInsufficientBuffer)
        {
            throw Win32IOException("Sizing owner/group/DACL security-descriptor evidence", sizeError);
        }

        if (bytesNeeded > MaximumDescriptorBytes || bytesNeeded > int.MaxValue)
        {
            throw new InvalidDataException(
                $"The owner/group/DACL security descriptor requires an implausible {bytesNeeded} byte buffer.");
        }

        var buffer = Marshal.AllocHGlobal(checked((int)bytesNeeded));
        try
        {
            if (!GetKernelObjectSecurity(handle, requested, buffer, bytesNeeded, out var actualBytes))
            {
                throw Win32IOException(
                    "Reading owner/group/DACL security-descriptor evidence",
                    Marshal.GetLastWin32Error());
            }

            if (actualBytes == 0 || actualBytes > bytesNeeded || actualBytes > int.MaxValue)
            {
                throw new InvalidDataException(
                    "GetKernelObjectSecurity returned an invalid self-relative descriptor length.");
            }

            var descriptor = new byte[checked((int)actualBytes)];
            Marshal.Copy(buffer, descriptor, 0, descriptor.Length);
            var digest = Convert.ToHexString(SHA256.HashData(descriptor)).ToLowerInvariant();
            return new FileSecurityDescriptorEvidence(requested, digest);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IOException Win32IOException(string action, int error) =>
        new($"{action} failed with Win32 error {error}: {new Win32Exception(error).Message}");

    [DllImport(
        "advapi32.dll",
        SetLastError = true,
        ExactSpelling = true,
        CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(
        SafeFileHandle Handle,
        uint RequestedInformation,
        IntPtr pSecurityDescriptor,
        uint nLength,
        out uint lpnLengthNeeded);
}
