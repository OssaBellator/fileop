using FileOp.Core.Models;
using FileOp.Windows.Ntfs;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class NtfsSnapshotNamespaceExpanderTests
{
    [TestMethod]
    public void ExpandMultiLinkFileCreatesOneRecordPerNamespacePath()
    {
        var volume = new NtfsVolume(@"C:\", @"\\.\C:", "Test", 0x1234);
        var entry = new NtfsMftEntry(
            100,
            10,
            500,
            DateTimeOffset.UnixEpoch,
            UsnReason.None,
            FileAttributes.Normal,
            "file.bin");
        var metadata = new NtfsFileMetadata(
            42,
            4096,
            2,
            false,
            DateTimeOffset.UnixEpoch,
            FileAttributes.Normal);
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
        var volume = new NtfsVolume(@"C:\", @"\\.\C:", "Test", 0x1234);
        var entry = new NtfsMftEntry(
            100,
            10,
            500,
            DateTimeOffset.UnixEpoch,
            UsnReason.None,
            FileAttributes.Normal,
            "file.bin");
        var metadata = new NtfsFileMetadata(
            42,
            4096,
            2,
            false,
            DateTimeOffset.UnixEpoch,
            FileAttributes.Normal);
        var directories = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\Alpha"] = 10,
        };
        var expander = new NtfsSnapshotNamespaceExpander(
            new FakeHardLinkEnumerator([@"C:\Alpha\file.bin"]));

        Assert.ThrowsExactly<NtfsIndexResnapshotRequiredException>(() =>
            expander.Expand(volume, entry, @"C:\Alpha\file.bin", metadata, directories));
    }

    private sealed class FakeHardLinkEnumerator(IReadOnlyList<string> paths) : INtfsHardLinkEnumerator
    {
        public IReadOnlyList<string> Enumerate(string path) => paths;
    }
}
