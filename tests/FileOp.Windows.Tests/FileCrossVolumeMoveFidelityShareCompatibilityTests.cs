using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileCrossVolumeMoveFidelityShareCompatibilityTests
{
    private const uint Delete = 0x00010000u;
    private const uint FileReadData = 0x00000001u;
    private const uint FileReadAttributes = 0x00000080u;
    private const uint Synchronize = 0x00100000u;
    private const int ErrorSharingViolation = 32;

    [TestMethod]
    public void FidelityReadReopenMustShareDeleteWhileDeleteCapabilityIsLive()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "FileOp.CrossVolumeMoveFidelityShare.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "source.txt");
        File.WriteAllText(path, "source");

        try
        {
            using var deleteCapability = CreateFileW(
                path,
                Delete | FileReadAttributes | Synchronize,
                FileShare.Read,
                IntPtr.Zero,
                FileMode.Open,
                0,
                IntPtr.Zero);
            var deleteCapabilityError = Marshal.GetLastWin32Error();
            Assert.IsFalse(
                deleteCapability.IsInvalid,
                $"Expected a live DELETE-capable source handle; Win32 error {deleteCapabilityError}.");

            using var incompatibleRead = CreateFileW(
                path,
                FileReadData | FileReadAttributes | Synchronize,
                FileShare.Read,
                IntPtr.Zero,
                FileMode.Open,
                0,
                IntPtr.Zero);
            var incompatibleReadError = Marshal.GetLastWin32Error();
            Assert.IsTrue(
                incompatibleRead.IsInvalid,
                "A fidelity read that omits FILE_SHARE_DELETE must not coexist with the already-live DELETE-capable source handle.");
            Assert.AreEqual(ErrorSharingViolation, incompatibleReadError);

            using var compatibleRead = CreateFileW(
                path,
                FileReadData | FileReadAttributes | Synchronize,
                FileShare.Read | FileShare.Delete,
                IntPtr.Zero,
                FileMode.Open,
                0,
                IntPtr.Zero);
            var compatibleReadError = Marshal.GetLastWin32Error();
            Assert.IsFalse(
                compatibleRead.IsInvalid,
                $"A fidelity read sharing READ+DELETE should coexist with the live source-delete capability; Win32 error {compatibleReadError}.");
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        ExactSpelling = true,
        CallingConvention = CallingConvention.Winapi)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);
}
