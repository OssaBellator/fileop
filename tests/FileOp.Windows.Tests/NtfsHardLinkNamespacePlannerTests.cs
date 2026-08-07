using FileOp.Core.Models;
using FileOp.Windows.Ntfs;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class NtfsHardLinkNamespacePlannerTests
{
    [TestMethod]
    public void PlanRefreshAddsJournalLinkAndKeepsExistingAlias()
    {
        var volume = Volume();
        var identity = new FileIdentity(volume.VolumeIdentity, 100);
        var alphaParent = new FileIdentity(volume.VolumeIdentity, 10);
        var betaParent = new FileIdentity(volume.VolumeIdentity, 20);
        var existing = new[] { Record(@"C:\Alpha\file.bin", identity, alphaParent) };

        var mutations = NtfsHardLinkNamespacePlanner.PlanRefresh(
            volume,
            Change(100, 20, "alias.bin"),
            identity,
            Metadata(2),
            existing,
            @"C:\Beta\alias.bin",
            betaParent,
            [@"C:\Alpha\file.bin", @"C:\Beta\alias.bin"]);

        Assert.AreEqual(2, mutations.Count);
        Assert.AreEqual(0, mutations.Count(mutation => mutation.Kind == NtfsIndexMutationKind.Delete));
        Assert.IsTrue(mutations.Any(mutation =>
            mutation.Record?.Path == @"C:\Alpha\file.bin" &&
            mutation.Record.ParentIdentity == alphaParent));
        Assert.IsTrue(mutations.Any(mutation =>
            mutation.Record?.Path == @"C:\Beta\alias.bin" &&
            mutation.Record.ParentIdentity == betaParent));
    }

    [TestMethod]
    public void PlanRefreshDeletesRemovedAliasAndRefreshesRemainingMetadata()
    {
        var volume = Volume();
        var identity = new FileIdentity(volume.VolumeIdentity, 100);
        var alphaParent = new FileIdentity(volume.VolumeIdentity, 10);
        var betaParent = new FileIdentity(volume.VolumeIdentity, 20);
        var existing = new[]
        {
            Record(@"C:\Alpha\file.bin", identity, alphaParent, length: 1),
            Record(@"C:\Beta\alias.bin", identity, betaParent, length: 1),
        };

        var mutations = NtfsHardLinkNamespacePlanner.PlanRefresh(
            volume,
            Change(100, 10, "file.bin"),
            identity,
            Metadata(1, length: 42),
            existing,
            @"C:\Alpha\file.bin",
            alphaParent,
            [@"C:\Alpha\file.bin"]);

        Assert.AreEqual(2, mutations.Count);
        Assert.IsTrue(mutations.Any(mutation =>
            mutation.Kind == NtfsIndexMutationKind.Delete &&
            mutation.Path == @"C:\Beta\alias.bin"));
        Assert.IsTrue(mutations.Any(mutation =>
            mutation.Kind == NtfsIndexMutationKind.Upsert &&
            mutation.Record?.Path == @"C:\Alpha\file.bin" &&
            mutation.Record.Length == 42));
    }

    [TestMethod]
    public void PlanRefreshRejectsUnknownAdditionalLinkParent()
    {
        var volume = Volume();
        var identity = new FileIdentity(volume.VolumeIdentity, 100);
        var alphaParent = new FileIdentity(volume.VolumeIdentity, 10);
        var existing = new[] { Record(@"C:\Alpha\file.bin", identity, alphaParent) };

        Assert.ThrowsExactly<NtfsIndexResnapshotRequiredException>(() =>
            NtfsHardLinkNamespacePlanner.PlanRefresh(
                volume,
                Change(100, 10, "file.bin"),
                identity,
                Metadata(2),
                existing,
                @"C:\Alpha\file.bin",
                alphaParent,
                [@"C:\Alpha\file.bin", @"C:\Unknown\alias.bin"]));
    }

    [TestMethod]
    public void PlanRefreshRejectsLinkCountMismatch()
    {
        var volume = Volume();
        var identity = new FileIdentity(volume.VolumeIdentity, 100);
        var alphaParent = new FileIdentity(volume.VolumeIdentity, 10);

        Assert.ThrowsExactly<NtfsIndexResnapshotRequiredException>(() =>
            NtfsHardLinkNamespacePlanner.PlanRefresh(
                volume,
                Change(100, 10, "file.bin"),
                identity,
                Metadata(2),
                [Record(@"C:\Alpha\file.bin", identity, alphaParent)],
                @"C:\Alpha\file.bin",
                alphaParent,
                [@"C:\Alpha\file.bin"]));
    }

    private static NtfsVolume Volume() => new(@"C:\", @"\\.\C:", "Test", 0x1234);

    private static NtfsJournalChange Change(ulong fileReference, ulong parentReference, string name) =>
        new(
            NtfsJournalChangeKind.HardLinkRefresh,
            fileReference,
            parentReference,
            name,
            1000,
            DateTimeOffset.UnixEpoch,
            UsnReason.HardLinkChange,
            FileAttributes.Normal);

    private static NtfsFileMetadata Metadata(uint links, long length = 42) =>
        new(
            length,
            4096,
            links,
            false,
            DateTimeOffset.UnixEpoch,
            FileAttributes.Normal);

    private static FileRecord Record(
        string path,
        FileIdentity identity,
        FileIdentity parentIdentity,
        long length = 42) =>
        new(
            path,
            Path.GetFileName(path),
            Path.GetDirectoryName(path) ?? string.Empty,
            Path.GetExtension(path),
            length,
            false,
            DateTimeOffset.UnixEpoch,
            FileAttributes.Normal,
            identity,
            parentIdentity,
            4096);
}
