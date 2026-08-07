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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string lpFileName,
        string lpExistingFileName,
        IntPtr lpSecurityAttributes);
}
