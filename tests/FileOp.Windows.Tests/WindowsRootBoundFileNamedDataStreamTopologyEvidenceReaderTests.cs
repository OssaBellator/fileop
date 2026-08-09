using System;
using System.IO;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsRootBoundFileNamedDataStreamTopologyEvidenceReaderTests
{
    [TestMethod]
    public async Task ReaderReportsNoNamedStreamsForOrdinaryNtfsFile()
    {
        using var fixture = new ReaderFixture();
        var request = await fixture.CreateRequestAsync();

        var result = await new WindowsRootBoundFileNamedDataStreamTopologyEvidenceReader().ReadAsync(request);

        Assert.AreEqual(FileContentFingerprintReadStatus.Success, result.Status);
        Assert.IsNotNull(result.Topology);
        Assert.AreEqual(0, result.Topology.NamedStreamCount);
        Assert.AreEqual(request.DestinationIdentity, result.CurrentDestination.Identity);
    }

    [TestMethod]
    public async Task NamedStreamNameOrSizeChangesTopologyDigestButSameSizeContentDoesNot()
    {
        using var fixture = new ReaderFixture();
        var request = await fixture.CreateRequestAsync();
        var streamPath = fixture.Path + ":alpha";
        try
        {
            await File.WriteAllTextAsync(streamPath, "AAAA");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Assert.Inconclusive("Named data streams are not available in this Windows test environment: " + exception.Message);
        }

        var reader = new WindowsRootBoundFileNamedDataStreamTopologyEvidenceReader();
        var first = await reader.ReadAsync(request);
        Assert.AreEqual(FileContentFingerprintReadStatus.Success, first.Status);
        Assert.IsNotNull(first.Topology);
        Assert.AreEqual(1, first.Topology.NamedStreamCount);

        await File.WriteAllTextAsync(streamPath, "BBBB");
        var sameSizeRewrite = await reader.ReadAsync(request);
        Assert.AreEqual(FileContentFingerprintReadStatus.Success, sameSizeRewrite.Status);
        Assert.AreEqual(first.Topology, sameSizeRewrite.Topology,
            "This slice deliberately hashes named-stream names and logical sizes, not named-stream contents.");

        await File.WriteAllTextAsync(streamPath, "BBBBB");
        var resized = await reader.ReadAsync(request);
        Assert.AreEqual(FileContentFingerprintReadStatus.Success, resized.Status);
        Assert.AreNotEqual(first.Topology, resized.Topology);

        await File.WriteAllTextAsync(fixture.Path + ":beta", "x");
        var addedName = await reader.ReadAsync(request);
        Assert.AreEqual(FileContentFingerprintReadStatus.Success, addedName.Status);
        Assert.AreEqual(2, addedName.Topology!.NamedStreamCount);
        Assert.AreNotEqual(resized.Topology, addedName.Topology);
    }

    [TestMethod]
    public async Task ExistingWriterCanCoexistWithAttributesOnlyTopologyRead()
    {
        using var fixture = new ReaderFixture();
        var request = await fixture.CreateRequestAsync();
        await using var writer = new FileStream(
            fixture.Path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.None);

        var result = await new WindowsRootBoundFileNamedDataStreamTopologyEvidenceReader().ReadAsync(request);

        Assert.AreEqual(FileContentFingerprintReadStatus.Success, result.Status);
        Assert.IsNotNull(result.Topology);
    }

    [TestMethod]
    public async Task WrongLeafIdentityFailsClosed()
    {
        using var fixture = new ReaderFixture();
        var request = await fixture.CreateRequestAsync();
        request = request with
        {
            DestinationIdentity = new FileIdentity(
                request.DestinationIdentity.VolumeSerialNumber,
                request.DestinationIdentity.FileReferenceNumber + 1),
        };

        var result = await new WindowsRootBoundFileNamedDataStreamTopologyEvidenceReader().ReadAsync(request);

        Assert.AreEqual(FileContentFingerprintReadStatus.DifferentObject, result.Status);
        Assert.IsNull(result.Topology);
    }

    [TestMethod]
    public async Task ReplacedRootWithSameFileMovedBackFailsClosed()
    {
        using var fixture = new ReaderFixture();
        var request = await fixture.CreateRequestAsync();
        var originalRoot = fixture.Root + ".original";
        Directory.Move(fixture.Root, originalRoot);
        Directory.CreateDirectory(fixture.Root);
        File.Move(System.IO.Path.Combine(originalRoot, "payload.bin"), fixture.Path);
        fixture.AddCleanupRoot(originalRoot);

        var result = await new WindowsRootBoundFileNamedDataStreamTopologyEvidenceReader().ReadAsync(request);

        Assert.AreEqual(FileContentFingerprintReadStatus.DestinationRootChanged, result.Status);
        Assert.IsNull(result.Topology);
    }

    private sealed class ReaderFixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "FileOp.NamedStreams.Reader.Tests",
            Guid.NewGuid().ToString("N"));
        private string? _additionalCleanupRoot;

        public ReaderFixture()
        {
            Directory.CreateDirectory(_root);
            Root = _root;
            Path = System.IO.Path.Combine(Root, "payload.bin");
            File.WriteAllText(Path, "content");
        }

        public string Root { get; }
        public string Path { get; }
        public void AddCleanupRoot(string path) => _additionalCleanupRoot = path;

        public async Task<FileContentFingerprintReadRequest> CreateRequestAsync()
        {
            var resolver = new WindowsFileOperationCanonicalPathResolver();
            var root = await resolver.ResolveAsync(Root);
            var leaf = await resolver.ResolveAsync(Path);
            Assert.AreEqual(FileOperationCanonicalPathState.Directory, root.State);
            Assert.AreEqual(FileOperationCanonicalPathState.File, leaf.State);
            Assert.IsNotNull(root.Identity);
            Assert.IsNotNull(leaf.Identity);
            return new FileContentFingerprintReadRequest(
                root.CanonicalPath,
                root.Identity.Value,
                leaf.CanonicalPath,
                leaf.Identity.Value);
        }

        public void Dispose()
        {
            foreach (var root in new[] { Root, _additionalCleanupRoot })
            {
                if (string.IsNullOrEmpty(root))
                {
                    continue;
                }

                try { Directory.Delete(root, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
