using System.ComponentModel;
using System.Runtime.InteropServices;
using FileOp.Windows.Ntfs;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class NtfsHardLinkEnumeratorTests
{
    [TestMethod]
    public void EnumerateReturnsAllHardLinkNames()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"fileop-hardlinks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var original = Path.Combine(directory, "original.bin");
        var alias = Path.Combine(directory, "alias.bin");

        try
        {
            File.WriteAllBytes(original, [1, 2, 3, 4]);
            if (!CreateHardLinkW(alias, original, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var links = new NtfsHardLinkEnumerator().Enumerate(original);

            Assert.IsTrue(links.Contains(Path.GetFullPath(original), StringComparer.OrdinalIgnoreCase));
            Assert.IsTrue(links.Contains(Path.GetFullPath(alias), StringComparer.OrdinalIgnoreCase));
            Assert.AreEqual(2, links.Count);
        }
        finally
        {
            if (File.Exists(alias))
            {
                File.Delete(alias);
            }

            if (File.Exists(original))
            {
                File.Delete(original);
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void EnumerateSupportsPathsBeyondMaxPath()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), $"fileop-hardlinks-{Guid.NewGuid():N}");
        var directory = Path.Combine(
            baseDirectory,
            new string('a', 80),
            new string('b', 80),
            new string('c', 80));
        var original = Path.Combine(directory, "original.bin");
        var alias = Path.Combine(directory, "alias.bin");
        var extendedBaseDirectory = ToExtendedLengthPath(baseDirectory);
        var extendedDirectory = ToExtendedLengthPath(directory);
        var extendedOriginal = ToExtendedLengthPath(original);
        var extendedAlias = ToExtendedLengthPath(alias);

        Assert.IsTrue(original.Length > 260, "The test path must exercise Win32 long-path handling.");

        try
        {
            Directory.CreateDirectory(extendedDirectory);
            File.WriteAllBytes(extendedOriginal, [1, 2, 3, 4]);
            if (!CreateHardLinkW(extendedAlias, extendedOriginal, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var links = new NtfsHardLinkEnumerator().Enumerate(original);

            Assert.IsTrue(links.Contains(Path.GetFullPath(original), StringComparer.OrdinalIgnoreCase));
            Assert.IsTrue(links.Contains(Path.GetFullPath(alias), StringComparer.OrdinalIgnoreCase));
            Assert.AreEqual(2, links.Count);
        }
        finally
        {
            if (Directory.Exists(extendedBaseDirectory))
            {
                Directory.Delete(extendedBaseDirectory, recursive: true);
            }
        }
    }

    private static string ToExtendedLengthPath(string path) => @"\\?\" + Path.GetFullPath(path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string lpFileName,
        string lpExistingFileName,
        IntPtr lpSecurityAttributes);
}
