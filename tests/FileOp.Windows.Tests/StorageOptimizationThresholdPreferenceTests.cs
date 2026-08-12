using System.Text;
using FileOp.Core.Storage;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class StorageOptimizationThresholdPreferenceTests
{
    [TestMethod]
    public void SupportedPreferenceResolvesAgainstCurrentPolicy()
    {
        var analysis = Analysis(new StorageOptimizationPolicy(
            LargeFileMinimumBytes: 100,
            SameSizeMinimumBytes: 50,
            StaleAgeDays: 10));
        var preference = new StorageOptimizationThresholdPreference(4, 2, 6);

        var thresholds = StorageOptimizationThresholdPreferencePolicy.Resolve(analysis, preference);

        Assert.AreEqual(400L, thresholds.LargeFileMinimumBytes);
        Assert.AreEqual(100L, thresholds.SameSizeMinimumBytes);
        Assert.AreEqual(60, thresholds.StaleAgeDays);
    }

    [TestMethod]
    public void SamePreferenceReResolvesAgainstFreshPolicy()
    {
        var preference = new StorageOptimizationThresholdPreference(2, 4, 2);
        var first = StorageOptimizationThresholdPreferencePolicy.Resolve(
            Analysis(new StorageOptimizationPolicy(
                LargeFileMinimumBytes: 100,
                SameSizeMinimumBytes: 25,
                StaleAgeDays: 30)),
            preference);
        var second = StorageOptimizationThresholdPreferencePolicy.Resolve(
            Analysis(new StorageOptimizationPolicy(
                LargeFileMinimumBytes: 300,
                SameSizeMinimumBytes: 40,
                StaleAgeDays: 90)),
            preference);

        Assert.AreEqual(200L, first.LargeFileMinimumBytes);
        Assert.AreEqual(100L, first.SameSizeMinimumBytes);
        Assert.AreEqual(60, first.StaleAgeDays);
        Assert.AreEqual(600L, second.LargeFileMinimumBytes);
        Assert.AreEqual(160L, second.SameSizeMinimumBytes);
        Assert.AreEqual(180, second.StaleAgeDays);
    }

    [TestMethod]
    public void UnsupportedPreferenceFallsBackToCurrentBaseline()
    {
        var analysis = Analysis(new StorageOptimizationPolicy(
            LargeFileMinimumBytes: 123,
            SameSizeMinimumBytes: 45,
            StaleAgeDays: 67));

        var thresholds = StorageOptimizationThresholdPreferencePolicy.Resolve(
            analysis,
            new StorageOptimizationThresholdPreference(3, 2, 2));

        Assert.AreEqual(StorageOptimizationDisplayThresholds.FromAnalysis(analysis), thresholds);
    }

    [TestMethod]
    public void SaturatedThresholdsDoNotImplyAUniqueMultiplier()
    {
        var analysis = Analysis(new StorageOptimizationPolicy(
            LargeFileMinimumBytes: long.MaxValue / 2 + 1,
            SameSizeMinimumBytes: long.MaxValue / 4 + 1,
            StaleAgeDays: int.MaxValue / 2 + 1));
        var lowerPreference = new StorageOptimizationThresholdPreference(2, 4, 2);
        var higherPreference = new StorageOptimizationThresholdPreference(8, 8, 6);

        var lowerThresholds = StorageOptimizationThresholdPreferencePolicy.Resolve(
            analysis,
            lowerPreference);
        var higherThresholds = StorageOptimizationThresholdPreferencePolicy.Resolve(
            analysis,
            higherPreference);

        Assert.AreEqual(higherThresholds, lowerThresholds);
        Assert.AreNotEqual(higherPreference, lowerPreference);
        Assert.AreEqual(long.MaxValue, higherThresholds.LargeFileMinimumBytes);
        Assert.AreEqual(long.MaxValue, higherThresholds.SameSizeMinimumBytes);
        Assert.AreEqual(int.MaxValue, higherThresholds.StaleAgeDays);
    }

    [TestMethod]
    public void PreferenceResolutionSaturatesWithoutOverflow()
    {
        var analysis = Analysis(new StorageOptimizationPolicy(
            LargeFileMinimumBytes: long.MaxValue / 2 + 1,
            SameSizeMinimumBytes: long.MaxValue / 4 + 1,
            StaleAgeDays: int.MaxValue / 2 + 1));

        var thresholds = StorageOptimizationThresholdPreferencePolicy.Resolve(
            analysis,
            new StorageOptimizationThresholdPreference(8, 8, 6));

        Assert.AreEqual(long.MaxValue, thresholds.LargeFileMinimumBytes);
        Assert.AreEqual(long.MaxValue, thresholds.SameSizeMinimumBytes);
        Assert.AreEqual(int.MaxValue, thresholds.StaleAgeDays);
        Assert.IsTrue(thresholds.LargeFileMinimumBytes >= analysis.Policy.LargeFileMinimumBytes);
        Assert.IsTrue(thresholds.SameSizeMinimumBytes >= analysis.Policy.SameSizeMinimumBytes);
        Assert.IsTrue(thresholds.StaleAgeDays >= analysis.Policy.StaleAgeDays);
    }

    [TestMethod]
    public async Task PreferenceStoreRoundTripsAndLatestSaveWins()
    {
        using var fixture = new PreferenceFixture();
        var store = new JsonFileStorageOptimizationThresholdPreferenceStore(fixture.PreferencePath);
        var first = new StorageOptimizationThresholdPreference(2, 4, 6);
        var second = new StorageOptimizationThresholdPreference(8, 2, 4);

        Assert.IsNull(await store.LoadAsync());
        await store.SaveAsync(first);
        Assert.AreEqual(first, await store.LoadAsync());
        await store.SaveAsync(second);
        Assert.AreEqual(second, await store.LoadAsync());

        var text = await File.ReadAllTextAsync(fixture.PreferencePath);
        StringAssert.Contains(text, "\"SchemaVersion\":1");
        StringAssert.Contains(text, "\"LargeFileMultiplier\":8");
        Assert.IsFalse(text.Contains("LargeFileMinimumBytes", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("StaleAgeDays", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CorruptUnknownUnsupportedAndOversizedPreferencesFallBackToNoPreference()
    {
        using var fixture = new PreferenceFixture();
        var store = new JsonFileStorageOptimizationThresholdPreferenceStore(fixture.PreferencePath);
        var invalidDocuments = new[]
        {
            "not json",
            "{}",
            "{\"SchemaVersion\":2,\"LargeFileMultiplier\":2,\"SameSizeMultiplier\":2,\"StaleAgeMultiplier\":2}",
            "{\"SchemaVersion\":1,\"LargeFileMultiplier\":3,\"SameSizeMultiplier\":2,\"StaleAgeMultiplier\":2}",
            "{\"SchemaVersion\":1,\"LargeFileMultiplier\":2,\"SameSizeMultiplier\":2,\"StaleAgeMultiplier\":2,\"Extra\":1}",
            "{\"SchemaVersion\":1,\"SchemaVersion\":1,\"LargeFileMultiplier\":2,\"SameSizeMultiplier\":2,\"StaleAgeMultiplier\":2}",
        };

        foreach (var document in invalidDocuments)
        {
            await File.WriteAllTextAsync(fixture.PreferencePath, document);
            Assert.IsNull(await store.LoadAsync(), document);
        }

        await File.WriteAllBytesAsync(
            fixture.PreferencePath,
            Encoding.UTF8.GetBytes(new string('x', 4_097)));
        Assert.IsNull(await store.LoadAsync());
    }

    [TestMethod]
    public async Task UnsupportedPreferenceCannotBePersisted()
    {
        using var fixture = new PreferenceFixture();
        var store = new JsonFileStorageOptimizationThresholdPreferenceStore(fixture.PreferencePath);

        await AssertThrowsAsync<ArgumentException>(async () =>
            await store.SaveAsync(new StorageOptimizationThresholdPreference(3, 2, 2)));

        Assert.IsFalse(File.Exists(fixture.PreferencePath));
    }

    [TestMethod]
    public async Task PreferenceStoreCancellationPropagatesWithoutCreatingFile()
    {
        using var fixture = new PreferenceFixture();
        var store = new JsonFileStorageOptimizationThresholdPreferenceStore(fixture.PreferencePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await AssertThrowsAsync<OperationCanceledException>(async () =>
            await store.SaveAsync(
                new StorageOptimizationThresholdPreference(2, 2, 2),
                cancellation.Token));

        Assert.IsFalse(File.Exists(fixture.PreferencePath));
    }

    private static StorageOptimizationAnalysis Analysis(StorageOptimizationPolicy policy) =>
        new(
            @"C:\",
            DateTimeOffset.UnixEpoch,
            policy,
            [],
            [],
            []);

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        Assert.Fail($"Expected {typeof(TException).Name} to be thrown.");
    }

    private sealed class PreferenceFixture : IDisposable
    {
        public PreferenceFixture()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "FileOp.StorageThresholdPreference.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            PreferencePath = Path.Combine(DirectoryPath, "thresholds.json");
        }

        public string DirectoryPath { get; }

        public string PreferencePath { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
