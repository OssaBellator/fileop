using FileOp.Core.Storage;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class StoragePhysicalReclaimTests
{
    [TestMethod]
    public void AllSingletonCopiesKeepSmallestAllocationAndReclaimOthers()
    {
        var matches = MatchSet("a", "b", "c");
        var evidence = new[]
        {
            Evidence("a", 1, 1, 1, 100),
            Evidence("b", 1, 2, 1, 300),
            Evidence("c", 1, 3, 1, 200),
        };

        var result = StoragePhysicalReclaimAnalyzer.Analyze([matches], evidence);

        Assert.AreEqual(StoragePhysicalReclaimEvidenceStatus.Verified, result.Status);
        Assert.AreEqual(500L, result.ReclaimableBytesUpperBound);
        Assert.AreEqual(1, result.MatchingSets.Count);
        Assert.AreEqual(3, result.MatchingSets[0].UniquePhysicalFileCount);
        Assert.AreEqual(3, result.MatchingSets[0].SingletonLinkPhysicalFileCount);
    }

    [TestMethod]
    public void ExistingHardLinkedCopyCanRemainWhileSingletonCopyIsReclaimable()
    {
        var matches = MatchSet("a", "alias-a", "b");
        var sharedIdentity = new StoragePhysicalFileIdentity(7, 100);
        var evidence = new[]
        {
            new StoragePhysicalFileEvidence("a", sharedIdentity, 2, 128),
            new StoragePhysicalFileEvidence("alias-a", sharedIdentity, 2, 128),
            Evidence("b", 7, 200, 1, 512),
        };

        var result = StoragePhysicalReclaimAnalyzer.Analyze([matches], evidence);

        Assert.AreEqual(StoragePhysicalReclaimEvidenceStatus.Verified, result.Status);
        Assert.AreEqual(512L, result.ReclaimableBytesUpperBound);
        Assert.AreEqual(2, result.MatchingSets[0].UniquePhysicalFileCount);
        Assert.AreEqual(1, result.MatchingSets[0].SingletonLinkPhysicalFileCount);
        Assert.AreEqual(2, result.MatchingSets[0].PhysicalFiles[0].SamplePaths.Count);
    }

    [TestMethod]
    public void HardLinkAliasesOfSamePhysicalFileDoNotBecomeReclaimBytes()
    {
        var identity = new StoragePhysicalFileIdentity(1, 42);
        var result = StoragePhysicalReclaimAnalyzer.Analyze(
            [MatchSet("a", "b")],
            [
                new StoragePhysicalFileEvidence("a", identity, 2, 4096),
                new StoragePhysicalFileEvidence("b", identity, 2, 4096),
            ]);

        Assert.AreEqual(StoragePhysicalReclaimEvidenceStatus.Verified, result.Status);
        Assert.AreEqual(0L, result.ReclaimableBytesUpperBound);
        Assert.AreEqual(1, result.MatchingSets[0].UniquePhysicalFileCount);
    }

    [TestMethod]
    public void DistinctMultiLinkFilesRemainNonReclaimableFromSampledPaths()
    {
        var result = StoragePhysicalReclaimAnalyzer.Analyze(
            [MatchSet("a", "b")],
            [
                Evidence("a", 1, 10, 2, 100),
                Evidence("b", 1, 20, 3, 200),
            ]);

        Assert.AreEqual(StoragePhysicalReclaimEvidenceStatus.Verified, result.Status);
        Assert.AreEqual(0L, result.ReclaimableBytesUpperBound);
        Assert.AreEqual(0, result.MatchingSets[0].SingletonLinkPhysicalFileCount);
    }

    [TestMethod]
    public void MissingPhysicalEvidenceFailsClosed()
    {
        var result = StoragePhysicalReclaimAnalyzer.Analyze(
            [MatchSet("a", "b")],
            [Evidence("a", 1, 10, 1, 100)]);

        Assert.AreEqual(StoragePhysicalReclaimEvidenceStatus.Unavailable, result.Status);
        Assert.AreEqual(0L, result.ReclaimableBytesUpperBound);
        Assert.AreEqual(0, result.MatchingSets.Count);
    }

    [TestMethod]
    public void AliasCountCannotExceedCurrentHardLinkCount()
    {
        var identity = new StoragePhysicalFileIdentity(1, 10);
        var result = StoragePhysicalReclaimAnalyzer.Analyze(
            [MatchSet("a", "b")],
            [
                new StoragePhysicalFileEvidence("a", identity, 1, 100),
                new StoragePhysicalFileEvidence("b", identity, 1, 100),
            ]);

        Assert.AreEqual(StoragePhysicalReclaimEvidenceStatus.Unavailable, result.Status);
        Assert.AreEqual(0L, result.ReclaimableBytesUpperBound);
    }

    [TestMethod]
    public void PhysicalIdentityCannotAppearInTwoContentMatchSets()
    {
        var identity = new StoragePhysicalFileIdentity(1, 10);
        var result = StoragePhysicalReclaimAnalyzer.Analyze(
            [MatchSet("a", "b"), MatchSet("c", "d")],
            [
                new StoragePhysicalFileEvidence("a", identity, 2, 100),
                Evidence("b", 1, 20, 1, 100),
                new StoragePhysicalFileEvidence("c", identity, 2, 100),
                Evidence("d", 1, 30, 1, 100),
            ]);

        Assert.AreEqual(StoragePhysicalReclaimEvidenceStatus.Unavailable, result.Status);
        Assert.AreEqual(0L, result.ReclaimableBytesUpperBound);
    }

    [TestMethod]
    public void ExtremeSingletonAllocationsSaturateAfterChoosingKeeper()
    {
        var result = StoragePhysicalReclaimAnalyzer.Analyze(
            [MatchSet("a", "b", "c")],
            [
                Evidence("a", 1, 1, 1, long.MaxValue),
                Evidence("b", 1, 2, 1, long.MaxValue),
                Evidence("c", 1, 3, 1, long.MaxValue),
            ]);

        Assert.AreEqual(StoragePhysicalReclaimEvidenceStatus.Verified, result.Status);
        Assert.AreEqual(long.MaxValue, result.ReclaimableBytesUpperBound);
    }

    private static StorageVerifiedContentMatchSet MatchSet(params string[] paths) =>
        new(paths.Select(Path.GetFullPath).ToArray());

    private static StoragePhysicalFileEvidence Evidence(
        string path,
        uint volume,
        ulong fileIndex,
        uint links,
        long allocatedBytes) =>
        new(
            Path.GetFullPath(path),
            new StoragePhysicalFileIdentity(volume, fileIndex),
            links,
            allocatedBytes);
}
