using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class VolumeFragmentationTimeoutTests
{
    [TestMethod]
    public void RemainingTimeoutSubtractsLookupElapsedTime()
    {
        var remaining = WindowsVolumeFragmentationApi.GetRemainingTimeout(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(3));

        Assert.AreEqual(TimeSpan.FromSeconds(7), remaining);
    }

    [TestMethod]
    public void RemainingTimeoutFailsWhenLookupConsumesBudget()
    {
        Assert.Throws<TimeoutException>(() =>
            WindowsVolumeFragmentationApi.GetRemainingTimeout(
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(10)));
        Assert.Throws<TimeoutException>(() =>
            WindowsVolumeFragmentationApi.GetRemainingTimeout(
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(11)));
    }
}
