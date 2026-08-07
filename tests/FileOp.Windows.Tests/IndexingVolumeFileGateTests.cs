using FileOp.Windows.IndexingService;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class IndexingVolumeFileGateTests
{
    [TestMethod]
    public void ReadLeasesCanCoexistAndBlockMaintenance()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var databasePath = Path.Combine(directory, "volume.sqlite");
            var firstGate = new IndexingVolumeFileGate(databasePath);
            var secondGate = new IndexingVolumeFileGate(databasePath);

            using var firstRead = firstGate.TryAcquireRead();
            using var secondRead = secondGate.TryAcquireRead();

            Assert.IsNotNull(firstRead);
            Assert.IsNotNull(secondRead);
            Assert.IsNull(secondGate.TryAcquireMaintenance());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void MaintenanceLeaseBlocksReadersAndOtherMaintenance()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var databasePath = Path.Combine(directory, "volume.sqlite");
            var firstGate = new IndexingVolumeFileGate(databasePath);
            var secondGate = new IndexingVolumeFileGate(databasePath);

            using var maintenance = firstGate.TryAcquireMaintenance();

            Assert.IsNotNull(maintenance);
            Assert.IsNull(secondGate.TryAcquireRead());
            Assert.IsNull(secondGate.TryAcquireMaintenance());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void MaintenanceCanAcquireAfterReadersRelease()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var databasePath = Path.Combine(directory, "volume.sqlite");
            var firstGate = new IndexingVolumeFileGate(databasePath);
            var secondGate = new IndexingVolumeFileGate(databasePath);

            var read = firstGate.TryAcquireRead();
            Assert.IsNotNull(read);
            read.Dispose();

            using var maintenance = secondGate.TryAcquireMaintenance();
            Assert.IsNotNull(maintenance);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"fileop-index-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
