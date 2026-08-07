using System.ComponentModel;
using FileOp.Core.Models;
using FileOp.Windows.Ntfs;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class NtfsSnapshotNamespaceExpanderTests
{
    [TestMethod]
    public void ExpandMultiLinkFileCreatesOneRecordPerNamespacePath()
    {
        var volume = Volume();
        var entry = Entry();
        var metadata = Metadata(2);
        var directories = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\Alpha"] = 10,
            [@"C:\Beta"] = 20,
        };
        var expander = new NtfsSnapshotNamespaceExpander(
            new FakeHardLinkEnumerator([@"C:\Alpha\file.bin", @"C:\Beta\renamed.bin"]));

        var records = expander.Expand(
            volume,
            entry,
            @"C:\Alpha\file.bin",
            metadata,
            directories);

        Assert.AreEqual(2, records.Count);
        Assert.IsTrue(records.Any(record =>
            record.Path == @"C:\Alpha\file.bin" &&
            record.Identity == new FileIdentity(volume.VolumeIdentity, 100) &&
            record.ParentIdentity == new FileIdentity(volume.VolumeIdentity, 10)));
        Assert.IsTrue(records.Any(record =>
            record.Path == @"C:\Beta\renamed.bin" &&
            record.Identity == new FileIdentity(volume.VolumeIdentity, 100) &&
            record.ParentIdentity == new FileIdentity(volume.VolumeIdentity, 20)));
    }

    [TestMethod]
    public void ExpandMultiLinkFileRejectsIncompleteEnumeration()
    {
        var volume = Volume();
        var directories = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\Alpha"] = 10,
        };
        var expander = new NtfsSnapshotNamespaceExpander(
            new FakeHardLinkEnumerator([@"C:\Alpha\file.bin"]));

        Assert.ThrowsExactly<NtfsIndexResnapshotRequiredException>(() =>
            expander.Expand(volume, Entry(), @"C:\Alpha\file.bin", Metadata(2), directories));
    }

    [TestMethod]
    public void ExpandMultiLinkFileConvertsDisappearingBaselineToResnapshot()
    {
        var volume = Volume();
        var expander = new NtfsSnapshotNamespaceExpander(new MissingHardLinkEnumerator());

        var exception = Assert.ThrowsExactly<NtfsIndexResnapshotRequiredException>(() =>
            expander.Expand(
                volume,
                Entry(),
                @"C:\Alpha\file.bin",
                Metadata(2),
                new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase)));

        Assert.IsInstanceOfType<Win32Exception>(exception.InnerException);
    }

    private static NtfsVolume Volume() => new(@"C:\", @"\\.\C:", "Test", 0x1234);

    private static NtfsMftEntry Entry() =>
        new(
            100,
            10,
            500,
            DateTimeOffset.UnixEpoch,
            UsnReason.None,
            FileAttributes.Normal,
            "file.bin");

    private static NtfsFileMetadata Metadata(uint links) =>
        new(
            42,
            4096,
            links,
            false,
            DateTimeOffset.UnixEpoch,
            FileAttributes.Normal);

    private sealed class FakeHardLinkEnumerator(IReadOnlyList<string> paths) : INtfsHardLinkEnumerator
    {
        public IReadOnlyList<string> Enumerate(string path) => paths;
    }

    private sealed class MissingHardLinkEnumerator : INtfsHardLinkEnumerator
    {
        public IReadOnlyList<string> Enumerate(string path) => throw new Win32Exception(2);
    }
}
