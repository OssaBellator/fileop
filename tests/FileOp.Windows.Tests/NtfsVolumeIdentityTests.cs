using FileOp.Windows.Ntfs;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class NtfsVolumeIdentityTests
{
    [TestMethod]
    public void VolumeGuidProducesStableIdentityIndependentOfSerial()
    {
        const string volumeGuidPath = @"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\";
        var first = new NtfsVolume(@"C:\", @"\\.\C:", "First", 0x11111111, volumeGuidPath);
        var second = new NtfsVolume(@"D:\", @"\\.\D:", "Second", 0x22222222, volumeGuidPath.ToUpperInvariant());

        Assert.AreEqual(first.VolumeIdentity, second.VolumeIdentity);
        Assert.AreNotEqual((ulong)first.SerialNumber, first.VolumeIdentity);
    }

    [TestMethod]
    public void DifferentVolumeGuidsProduceDifferentIdentityTokens()
    {
        var first = new NtfsVolume(
            @"C:\",
            @"\\.\C:",
            "First",
            0x12345678,
            @"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\");
        var second = new NtfsVolume(
            @"D:\",
            @"\\.\D:",
            "Second",
            0x12345678,
            @"\\?\Volume{fedcba98-7654-3210-fedc-ba9876543210}\");

        Assert.AreNotEqual(first.VolumeIdentity, second.VolumeIdentity);
    }

    [TestMethod]
    public void MissingOrInvalidVolumeGuidFallsBackToSerial()
    {
        var missing = new NtfsVolume(@"C:\", @"\\.\C:", "Missing", 0x12345678);
        var invalid = new NtfsVolume(@"D:\", @"\\.\D:", "Invalid", 0x12345678, "not-a-volume-guid");

        Assert.AreEqual(0x12345678UL, missing.VolumeIdentity);
        Assert.AreEqual(0x12345678UL, invalid.VolumeIdentity);
    }
}
