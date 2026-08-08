using FileOp.Core.Storage;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class StorageHistoryDeltaTests
{
    [TestMethod]
    public void UnchangedUnknownPhysicalCategoryIsNotReportedAsGrowth()
    {
        var category = new StorageHistoryCategorySnapshot(
            StorageFileCategory.Images,
            100,
            null,
            1,
            0,
            1);
        var older = new StorageHistorySnapshot(
            1,
            @"C:\Data",
            new DateTimeOffset(2026, 8, 8, 1, 0, 0, TimeSpan.Zero),
            100,
            null,
            1,
            0,
            1,
            [category]);
        var newer = older with
        {
            Id = 2,
            RootPath = @"c:\data\",
            CapturedAt = older.CapturedAt.AddHours(1),
        };

        var delta = StorageHistoryDelta.Between(older, newer);

        Assert.AreEqual(0L, delta.LogicalBytesDelta);
        Assert.IsNull(delta.AllocatedBytesDelta);
        Assert.AreEqual(0, delta.Categories.Count);
    }

    [TestMethod]
    public void DifferentRootsCannotBeCompared()
    {
        var first = new StorageHistorySnapshot(
            1,
            @"C:\Data",
            DateTimeOffset.UnixEpoch,
            0,
            0,
            0,
            0,
            0,
            []);
        var second = first with
        {
            Id = 2,
            RootPath = @"D:\Data",
            CapturedAt = first.CapturedAt.AddHours(1),
        };

        Assert.ThrowsExactly<ArgumentException>(() => StorageHistoryDelta.Between(first, second));
    }
}
