using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileDeleteOperationExecutionValidationTests
{
    [TestMethod]
    public void ProtectedLocationPolicyBlocksSystemTreesAndVolumeRoots()
    {
        var policy = new WindowsFileDeleteProtectedLocationPolicy(
        [
            @"C:\Windows",
            @"C:\Program Files",
            @"C:\ProgramData",
        ]);

        Assert.IsTrue(policy.Evaluate(@"C:\").IsBlocked);
        Assert.IsTrue(policy.Evaluate(@"C:\Windows\Temp\a.tmp").IsBlocked);
        Assert.IsTrue(policy.Evaluate(@"C:\Program Files\App\a.dll").IsBlocked);
        Assert.IsTrue(policy.Evaluate(@"C:\ProgramData\Vendor\cache.bin").IsBlocked);
        Assert.IsTrue(policy.Evaluate(@"C:\$Recycle.Bin\S-1-5-21\a.bin").IsBlocked);
        Assert.IsTrue(policy.Evaluate(@"C:\System Volume Information\a.bin").IsBlocked);
        Assert.IsFalse(policy.Evaluate(@"C:\Users\Alice\AppData\Local\Temp\a.tmp").IsBlocked);
    }

    [TestMethod]
    public void ResultRequiresCanonicalIdentityEvidenceForReadyItems()
    {
        var plan = Plan(FileEntry());
        var root = CanonicalDirectory(@"C:\Users\Alice\Temp", new FileIdentity(1, 10));
        var file = CanonicalFile(@"C:\Users\Alice\Temp\a.tmp", new FileIdentity(1, 11));
        var ready = new FileDeleteOperationExecutionValidationItem(
            plan.Intent.Entries[0],
            file,
            FileDeleteOperationExecutionValidationDecision.ReadyForAuthorizationReview,
            "ready for review");
        var result = new FileDeleteOperationExecutionValidationResult(
            plan,
            root,
            new[] { ready },
            FileDeleteOperationExecutionValidationStatus.ReadyForAuthorizationReview,
            DateTimeOffset.Now,
            "validated");

        Assert.IsTrue(result.CanRequestAuthorizationReview);
        Assert.IsFalse(result.DeleteMutationAuthorized);
        Assert.AreEqual(TimeSpan.Zero, result.ValidatedAtUtc.Offset);

        var noIdentity = CanonicalFile(@"C:\Users\Alice\Temp\a.tmp", identity: null);
        var invalidReady = ready with { Source = noIdentity };
        Assert.ThrowsException<ArgumentException>(() =>
            new FileDeleteOperationExecutionValidationResult(
                plan,
                root,
                new[] { invalidReady },
                FileDeleteOperationExecutionValidationStatus.ReadyForAuthorizationReview,
                DateTimeOffset.UtcNow,
                "invalid"));
    }

    [TestMethod]
    public async Task MatchingCanonicalFileIsReadyForAuthorizationReviewButNeverAuthorized()
    {
        var entry = FileEntry();
        var resolver = new FakeResolver(new Dictionary<string, FileOperationCanonicalPath>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\Users\Alice\Temp"] = CanonicalDirectory(@"C:\Users\Alice\Temp", new FileIdentity(1, 10)),
            [entry.Path] = CanonicalFile(entry.Path, new FileIdentity(1, 11)),
        });
        var validator = new WindowsFileDeleteOperationExecutionValidator(
            resolver,
            new WindowsFileDeleteProtectedLocationPolicy([@"C:\Windows", @"C:\ProgramData"]));

        var result = await validator.ValidateAsync(Plan(entry));

        Assert.AreEqual(
            FileDeleteOperationExecutionValidationStatus.ReadyForAuthorizationReview,
            result.Status);
        Assert.AreEqual(1, result.ReadyForAuthorizationReviewCount);
        Assert.AreEqual(0, result.BlockedCount);
        Assert.IsTrue(result.CanRequestAuthorizationReview);
        Assert.IsFalse(result.DeleteMutationAuthorized);
        Assert.IsNotNull(result.Items[0].Source.Identity);
        Assert.AreEqual(new FileIdentity(1, 11), result.Items[0].Source.Identity.Value);
        StringAssert.Contains(result.Summary, "No delete authorization was granted");
    }

    [TestMethod]
    public async Task CanonicalParentEscapeFailsClosed()
    {
        var entry = FileEntry();
        var resolver = new FakeResolver(new Dictionary<string, FileOperationCanonicalPath>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\Users\Alice\Temp"] = CanonicalDirectory(@"C:\Users\Alice\Temp", new FileIdentity(1, 10)),
            [entry.Path] = new FileOperationCanonicalPath(
                entry.Path,
                @"C:\Other\a.tmp",
                FileOperationCanonicalPathState.File,
                IsLeafReparsePoint: false,
                new FileIdentity(1, 11)),
        });
        var validator = new WindowsFileDeleteOperationExecutionValidator(
            resolver,
            new WindowsFileDeleteProtectedLocationPolicy(Array.Empty<string>()));

        var result = await validator.ValidateAsync(Plan(entry));

        Assert.AreEqual(FileDeleteOperationExecutionValidationStatus.Blocked, result.Status);
        Assert.AreEqual(1, result.BlockedCount);
        StringAssert.Contains(result.Items[0].Message, "outside the canonical captured source directory");
        Assert.IsFalse(result.DeleteMutationAuthorized);
    }

    [TestMethod]
    public async Task ProtectedCanonicalRootBlocksBeforeEntryResolution()
    {
        var entry = new FileOperationEntry(@"C:\Windows\Temp\a.tmp", "a.tmp", false);
        var resolver = new FakeResolver(new Dictionary<string, FileOperationCanonicalPath>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\Windows\Temp"] = CanonicalDirectory(@"C:\Windows\Temp", new FileIdentity(1, 20)),
        });
        var validator = new WindowsFileDeleteOperationExecutionValidator(
            resolver,
            new WindowsFileDeleteProtectedLocationPolicy([@"C:\Windows"]));

        var result = await validator.ValidateAsync(
            new FileDeleteOperationPlan(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                new FileDeleteOperationIntent("left", Guid.NewGuid(), @"C:\Windows\Temp", [entry])));

        Assert.AreEqual(FileDeleteOperationExecutionValidationStatus.Blocked, result.Status);
        Assert.AreEqual(1, resolver.Calls);
        Assert.AreEqual(0, result.Items.Count);
        StringAssert.Contains(result.Summary, "protected");
    }

    [TestMethod]
    public async Task MissingChangedReparseOrIdentitylessFileFailsClosed()
    {
        foreach (var source in new[]
        {
            new FileOperationCanonicalPath(@"C:\Users\Alice\Temp\a.tmp", @"C:\Users\Alice\Temp\a.tmp", FileOperationCanonicalPathState.Missing, false),
            CanonicalDirectory(@"C:\Users\Alice\Temp\a.tmp", new FileIdentity(1, 11)),
            new FileOperationCanonicalPath(@"C:\Users\Alice\Temp\a.tmp", @"C:\Users\Alice\Temp\a.tmp", FileOperationCanonicalPathState.File, true, new FileIdentity(1, 11)),
            CanonicalFile(@"C:\Users\Alice\Temp\a.tmp", identity: null),
            new FileOperationCanonicalPath(@"C:\Users\Alice\Temp\a.tmp", @"C:\Users\Alice\Temp\a.tmp", FileOperationCanonicalPathState.Inaccessible, false),
        })
        {
            var entry = FileEntry();
            var resolver = new FakeResolver(new Dictionary<string, FileOperationCanonicalPath>(StringComparer.OrdinalIgnoreCase)
            {
                [@"C:\Users\Alice\Temp"] = CanonicalDirectory(@"C:\Users\Alice\Temp", new FileIdentity(1, 10)),
                [entry.Path] = source,
            });
            var result = await new WindowsFileDeleteOperationExecutionValidator(
                    resolver,
                    new WindowsFileDeleteProtectedLocationPolicy(Array.Empty<string>()))
                .ValidateAsync(Plan(entry));

            Assert.AreEqual(FileDeleteOperationExecutionValidationStatus.Blocked, result.Status);
            Assert.IsFalse(result.DeleteMutationAuthorized);
        }
    }

    [TestMethod]
    public async Task DirectoryEntryBlocksBeforeEntryResolution()
    {
        var entry = new FileOperationEntry(@"C:\Users\Alice\Temp\Folder", "Folder", true);
        var resolver = new FakeResolver(new Dictionary<string, FileOperationCanonicalPath>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\Users\Alice\Temp"] = CanonicalDirectory(@"C:\Users\Alice\Temp", new FileIdentity(1, 10)),
        });
        var result = await new WindowsFileDeleteOperationExecutionValidator(
                resolver,
                new WindowsFileDeleteProtectedLocationPolicy(Array.Empty<string>()))
            .ValidateAsync(new FileDeleteOperationPlan(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                new FileDeleteOperationIntent("left", Guid.NewGuid(), @"C:\Users\Alice\Temp", [entry])));

        Assert.AreEqual(FileDeleteOperationExecutionValidationStatus.Blocked, result.Status);
        Assert.AreEqual(1, resolver.Calls);
        Assert.AreEqual(1, result.BlockedCount);
    }

    [TestMethod]
    public async Task SourceRootNeedsCanonicalDirectoryIdentityAndCannotBeReparse()
    {
        foreach (var root in new[]
        {
            new FileOperationCanonicalPath(@"C:\Users\Alice\Temp", @"C:\Users\Alice\Temp", FileOperationCanonicalPathState.Missing, false),
            CanonicalDirectory(@"C:\Users\Alice\Temp", identity: null),
            new FileOperationCanonicalPath(@"C:\Users\Alice\Temp", @"C:\Users\Alice\Temp", FileOperationCanonicalPathState.Directory, true, new FileIdentity(1, 10)),
        })
        {
            var resolver = new FakeResolver(new Dictionary<string, FileOperationCanonicalPath>(StringComparer.OrdinalIgnoreCase)
            {
                [@"C:\Users\Alice\Temp"] = root,
            });
            var result = await new WindowsFileDeleteOperationExecutionValidator(
                    resolver,
                    new WindowsFileDeleteProtectedLocationPolicy(Array.Empty<string>()))
                .ValidateAsync(Plan(FileEntry()));

            Assert.AreEqual(FileDeleteOperationExecutionValidationStatus.Blocked, result.Status);
            Assert.AreEqual(1, resolver.Calls);
            Assert.IsFalse(result.DeleteMutationAuthorized);
        }
    }

    [TestMethod]
    public async Task CallerCancellationPropagatesBeforeResolution()
    {
        var resolver = new FakeResolver(new Dictionary<string, FileOperationCanonicalPath>());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await new WindowsFileDeleteOperationExecutionValidator(
                    resolver,
                    new WindowsFileDeleteProtectedLocationPolicy(Array.Empty<string>()))
                .ValidateAsync(Plan(FileEntry()), cancellation.Token));
        Assert.AreEqual(0, resolver.Calls);
    }

    private static FileOperationEntry FileEntry() =>
        new(@"C:\Users\Alice\Temp\a.tmp", "a.tmp", false);

    private static FileDeleteOperationPlan Plan(FileOperationEntry entry) =>
        new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new FileDeleteOperationIntent(
                "left",
                Guid.NewGuid(),
                @"C:\Users\Alice\Temp",
                [entry]));

    private static FileOperationCanonicalPath CanonicalDirectory(
        string path,
        FileIdentity? identity) =>
        new(
            path,
            path,
            FileOperationCanonicalPathState.Directory,
            IsLeafReparsePoint: false,
            identity);

    private static FileOperationCanonicalPath CanonicalFile(
        string path,
        FileIdentity? identity) =>
        new(
            path,
            path,
            FileOperationCanonicalPathState.File,
            IsLeafReparsePoint: false,
            identity);

    private sealed class FakeResolver(
        IReadOnlyDictionary<string, FileOperationCanonicalPath> resolutions)
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
            if (!resolutions.TryGetValue(path, out var result))
            {
                throw new AssertFailedException($"Unexpected canonical resolution path: {path}");
            }
            return ValueTask.FromResult(result);
        }
    }
}
