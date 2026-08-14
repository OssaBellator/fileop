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
    private const uint FileWriteData = 0x00000002u;
    private const uint FileWriteEa = 0x00000010u;
    private const uint FileReadAttributes = 0x00000080u;
    private const uint FileWriteAttributes = 0x00000100u;
    private const uint Synchronize = 0x00100000u;
    private const uint PageReadWrite = 0x00000004u;
    private const int ErrorSharingViolation = 32;

    [TestMethod]
    public void FidelityReadReopenMustShareDeleteWhileDeleteCapabilityIsLive()
    {
        WithTemporaryFile(path =>
        {
            using var deleteCapability = OpenDeleteCapability(path);

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
        });
    }

    [TestMethod]
    public void PreExistingMainStreamWriterPreventsDeleteCapabilityAcquisition()
    {
        WithTemporaryFile(path =>
        {
            using var writer = CreateFileW(
                path,
                FileWriteData | Synchronize,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                IntPtr.Zero,
                FileMode.Open,
                0,
                IntPtr.Zero);
            var writerError = Marshal.GetLastWin32Error();
            Assert.IsFalse(writer.IsInvalid, $"Expected pre-existing writer; Win32 error {writerError}.");

            using var deleteCapability = CreateFileW(
                path,
                Delete | FileReadAttributes | Synchronize,
                FileShare.Read,
                IntPtr.Zero,
                FileMode.Open,
                0,
                IntPtr.Zero);
            var error = Marshal.GetLastWin32Error();

            Assert.IsTrue(
                deleteCapability.IsInvalid,
                "A source DELETE capability that omits FILE_SHARE_WRITE must not be acquired while a main-stream writer is already live.");
            Assert.AreEqual(ErrorSharingViolation, error);
        });
    }

    [TestMethod]
    public void WritableMainStreamMappingPreventsDeleteCapabilityAcquisitionEvenAfterFileHandleCloses()
    {
        WithTemporaryFile(path =>
        {
            SafeFileHandle mapping;
            using (var writer = CreateFileW(
                path,
                FileReadData | FileWriteData | Synchronize,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                IntPtr.Zero,
                FileMode.Open,
                0,
                IntPtr.Zero))
            {
                var writerError = Marshal.GetLastWin32Error();
                Assert.IsFalse(writer.IsInvalid, $"Expected writable source handle; Win32 error {writerError}.");

                mapping = CreateFileMappingW(
                    writer,
                    IntPtr.Zero,
                    PageReadWrite,
                    0,
                    0,
                    null);
                var mappingError = Marshal.GetLastWin32Error();
                Assert.IsFalse(mapping.IsInvalid, $"Expected writable mapping; Win32 error {mappingError}.");
            }

            using (mapping)
            using (var deleteCapability = CreateFileW(
                path,
                Delete | FileReadAttributes | Synchronize,
                FileShare.Read,
                IntPtr.Zero,
                FileMode.Open,
                0,
                IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                Assert.IsTrue(
                    deleteCapability.IsInvalid,
                    "A writable main-stream mapping must prevent acquisition of the FILE_SHARE_READ-only destructive source lease even after the mapping's file handle is closed.");
                Assert.AreEqual(ErrorSharingViolation, error);
            }
        });
    }

    [TestMethod]
    public void DeleteCapabilityPreventsNewMainStreamWriterUntilReleased()
    {
        WithTemporaryFile(path =>
        {
            using var deleteCapability = OpenDeleteCapability(path);

            using var writer = CreateFileW(
                path,
                FileWriteData | Synchronize,
                FileShare.Read | FileShare.Delete,
                IntPtr.Zero,
                FileMode.Open,
                0,
                IntPtr.Zero);
            var error = Marshal.GetLastWin32Error();

            Assert.IsTrue(
                writer.IsInvalid,
                "The live source-delete capability must exclude new main-stream write access through its FILE_SHARE_READ-only lease.");
            Assert.AreEqual(ErrorSharingViolation, error);
        });
    }

    [TestMethod]
    public void DeleteCapabilityDoesNotPretendShareModeFreezesAttributesOrEas()
    {
        WithTemporaryFile(path =>
        {
            using var deleteCapability = OpenDeleteCapability(path);

            using var attributeWriter = CreateFileW(
                path,
                FileWriteAttributes | Synchronize,
                FileShare.Read | FileShare.Delete,
                IntPtr.Zero,
                FileMode.Open,
                0,
                IntPtr.Zero);
            var attributeError = Marshal.GetLastWin32Error();
            Assert.IsFalse(
                attributeWriter.IsInvalid,
                $"FILE_WRITE_ATTRIBUTES is intentionally outside the share-mode stability guarantee; Win32 error {attributeError}.");

            using var eaWriter = CreateFileW(
                path,
                FileWriteEa | Synchronize,
                FileShare.Read | FileShare.Delete,
                IntPtr.Zero,
                FileMode.Open,
                0,
                IntPtr.Zero);
            var eaError = Marshal.GetLastWin32Error();
            Assert.IsFalse(
                eaWriter.IsInvalid,
                $"FILE_WRITE_EA is intentionally outside the share-mode stability guarantee; Win32 error {eaError}.");
        });
    }

    [TestMethod]
    public void DeletingOneHardLinkLeavesOtherSourceVolumeEntryValid()
    {
        var directory = CreateTemporaryDirectory();
        var selectedPath = Path.Combine(directory, "selected.txt");
        var otherLinkPath = Path.Combine(directory, "other-link.txt");
        File.WriteAllText(selectedPath, "source");

        try
        {
            Assert.IsTrue(
                CreateHardLinkW(otherLinkPath, selectedPath, IntPtr.Zero),
                $"Expected NTFS hard-link creation; Win32 error {Marshal.GetLastWin32Error()}.");

            File.Delete(selectedPath);

            Assert.IsFalse(File.Exists(selectedPath));
            Assert.IsTrue(File.Exists(otherLinkPath));
            Assert.AreEqual("source", File.ReadAllText(otherLinkPath));
        }
        finally
        {
            TryDeleteFile(selectedPath);
            TryDeleteFile(otherLinkPath);
            TryDeleteDirectory(directory);
        }
    }

    private static SafeFileHandle OpenDeleteCapability(string path)
    {
        var handle = CreateFileW(
            path,
            Delete | FileReadAttributes | Synchronize,
            FileShare.Read,
            IntPtr.Zero,
            FileMode.Open,
            0,
            IntPtr.Zero);
        var error = Marshal.GetLastWin32Error();
        Assert.IsFalse(
            handle.IsInvalid,
            $"Expected a live DELETE-capable source handle; Win32 error {error}.");
        return handle;
    }

    private static void WithTemporaryFile(Action<string> action)
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "source.txt");
        File.WriteAllText(path, "source");
        try
        {
            action(path);
        }
        finally
        {
            TryDeleteFile(path);
            TryDeleteDirectory(directory);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "FileOp.CrossVolumeMoveFidelityShare.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
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

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        ExactSpelling = true,
        CallingConvention = CallingConvention.Winapi)]
    private static extern SafeFileHandle CreateFileMappingW(
        SafeFileHandle hFile,
        IntPtr lpFileMappingAttributes,
        uint flProtect,
        uint dwMaximumSizeHigh,
        uint dwMaximumSizeLow,
        string? lpName);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        ExactSpelling = true,
        CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string lpFileName,
        string lpExistingFileName,
        IntPtr lpSecurityAttributes);
}
