using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileCrossVolumeMoveHardLinkPathBindingTests
{
    [TestMethod]
    public async Task CanonicalResolverPreservesTheSpecificOpenedHardLinkName()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "FileOp.CrossVolumeMoveHardLinkPath.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var selectedPath = Path.Combine(directory, "selected.txt");
        var retainedLinkPath = Path.Combine(directory, "retained-link.txt");
        File.WriteAllText(selectedPath, "hard-link canonical path binding");

        try
        {
            if (!CreateHardLinkW(retainedLinkPath, selectedPath, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                Assert.Inconclusive(
                    $"The current temp filesystem cannot create the hard-link path-binding fixture; Win32 error {error}.");
                return;
            }

            var resolver = new WindowsFileOperationCanonicalPathResolver();
            var selected = await resolver.ResolveAsync(selectedPath);
            var retained = await resolver.ResolveAsync(retainedLinkPath);

            Assert.AreEqual(FileOperationCanonicalPathState.File, selected.State, selected.ErrorMessage);
            Assert.AreEqual(FileOperationCanonicalPathState.File, retained.State, retained.ErrorMessage);
            Assert.IsTrue(selected.Identity.HasValue);
            Assert.IsTrue(retained.Identity.HasValue);
            Assert.AreEqual(
                selected.Identity.Value,
                retained.Identity.Value,
                "Both names must resolve to the same underlying filesystem identity.");
            Assert.IsTrue(
                string.Equals(
                    Path.GetFullPath(selectedPath),
                    Path.GetFullPath(selected.CanonicalPath),
                    StringComparison.OrdinalIgnoreCase),
                $"Resolving the selected hard-link name returned a different directory entry: '{selected.CanonicalPath}'.");
            Assert.IsTrue(
                string.Equals(
                    Path.GetFullPath(retainedLinkPath),
                    Path.GetFullPath(retained.CanonicalPath),
                    StringComparison.OrdinalIgnoreCase),
                $"Resolving the retained hard-link name returned a different directory entry: '{retained.CanonicalPath}'.");
        }
        finally
        {
            TryDeleteFile(selectedPath);
            TryDeleteFile(retainedLinkPath);
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
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
