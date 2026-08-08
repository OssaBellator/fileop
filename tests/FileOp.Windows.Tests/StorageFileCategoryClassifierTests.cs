using FileOp.Core.Storage;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class StorageFileCategoryClassifierTests
{
    [TestMethod]
    [DataRow(null, StorageFileCategory.NoExtension)]
    [DataRow("", StorageFileCategory.NoExtension)]
    [DataRow(".PDF", StorageFileCategory.Documents)]
    [DataRow("jpg", StorageFileCategory.Images)]
    [DataRow(".mkv", StorageFileCategory.Video)]
    [DataRow("flac", StorageFileCategory.Audio)]
    [DataRow(".7Z", StorageFileCategory.Archives)]
    [DataRow("exe", StorageFileCategory.Applications)]
    [DataRow(".ts", StorageFileCategory.Code)]
    [DataRow("sqlite3", StorageFileCategory.Data)]
    [DataRow(".vhdx", StorageFileCategory.DiskImages)]
    [DataRow("woff2", StorageFileCategory.Fonts)]
    [DataRow("something-unmapped", StorageFileCategory.Other)]
    public void ClassifyUsesDeterministicNormalizedExtensions(
        string? extension,
        StorageFileCategory expected)
    {
        Assert.AreEqual(expected, StorageFileCategoryClassifier.Classify(extension));
    }

    [TestMethod]
    public void NormalizeExtensionTrimsWhitespaceDotAndCase()
    {
        Assert.AreEqual("jpg", StorageFileCategoryClassifier.NormalizeExtension("  .JPG  "));
    }
}
