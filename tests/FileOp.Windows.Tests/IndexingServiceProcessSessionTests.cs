using FileOp.Windows.IndexingService;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class IndexingServiceProcessSessionTests
{
    [TestMethod]
    public async Task StartAsyncWrapsNativeProcessLaunchFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"fileop-indexer-launch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var executablePath = Path.Combine(directory, "invalid-indexer.exe");

        try
        {
            await File.WriteAllTextAsync(executablePath, "not a Windows executable");

            var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                IndexingServiceProcessSession.StartAsync(
                    executablePath,
                    elevated: false,
                    connectTimeout: TimeSpan.FromMilliseconds(100)));

            StringAssert.Contains(exception.Message, "could not start the FileOp indexing helper");
            Assert.IsNotNull(exception.InnerException);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
