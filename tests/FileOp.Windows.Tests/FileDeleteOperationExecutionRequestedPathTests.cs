using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileDeleteOperationExecutionRequestedPathTests
{
    [TestMethod]
    public async Task RequestedPathMustRemainDirectChildWithCapturedLeafAndNoAds()
    {
        var rootPath = @"C:\Users\Alice\Temp";
        var resolver = new RootOnlyResolver(
            rootPath,
            new FileOperationCanonicalPath(
                rootPath,
                rootPath,
                FileOperationCanonicalPathState.Directory,
                IsLeafReparsePoint: false,
                new FileIdentity(1, 10)));
        var validator = new WindowsFileDeleteOperationExecutionValidator(
            resolver,
            new WindowsFileDeleteProtectedLocationPolicy(Array.Empty<string>()));

        var nested = await validator.ValidateAsync(Plan(
            rootPath,
            new FileOperationEntry(@"C:\Users\Alice\Temp\Nested\a.tmp", "a.tmp", false)));
        var renamed = await validator.ValidateAsync(Plan(
            rootPath,
            new FileOperationEntry(@"C:\Users\Alice\Temp\a.tmp", "different.tmp", false)));
        var ads = await validator.ValidateAsync(Plan(
            rootPath,
            new FileOperationEntry(@"C:\Users\Alice\Temp\a.tmp:stream", "a.tmp:stream", false)));

        Assert.AreEqual(FileDeleteOperationExecutionValidationStatus.Blocked, nested.Status);
        Assert.AreEqual(FileDeleteOperationExecutionValidationStatus.Blocked, renamed.Status);
        Assert.AreEqual(FileDeleteOperationExecutionValidationStatus.Blocked, ads.Status);
        Assert.IsFalse(nested.DeleteMutationAuthorized);
        Assert.IsFalse(renamed.DeleteMutationAuthorized);
        Assert.IsFalse(ads.DeleteMutationAuthorized);
        Assert.AreEqual(3, resolver.Calls);
    }

    private static FileDeleteOperationPlan Plan(
        string rootPath,
        FileOperationEntry entry) =>
        new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new FileDeleteOperationIntent(
                "left",
                Guid.NewGuid(),
                rootPath,
                [entry]));

    private sealed class RootOnlyResolver(
        string rootPath,
        FileOperationCanonicalPath root)
        : IFileOperationCanonicalPathResolver
    {
        public int Calls { get; private set; }

        public ValueTask<FileOperationCanonicalPath> ResolveAsync(
            string path,
            bool allowMissingLeaf = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Assert.IsFalse(allowMissingLeaf);
            if (!string.Equals(path, rootPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new AssertFailedException($"Requested-path failure should block before file resolution: {path}");
            }
            return ValueTask.FromResult(root);
        }
    }
}
