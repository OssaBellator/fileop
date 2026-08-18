using System.Reflection;
using FileOp.FilesystemSafety.V1;

namespace FileOp.FilesystemSafety.Tests;

[TestClass]
public sealed class FileSystemSafetyProviderV1Tests
{
    [TestMethod]
    public void Contract_IsVersionedBoundedAndNonExecutable()
    {
        Assert.AreEqual("filesystem.safety.v1", FileSystemSafetyContractV1.ContractId);
        Assert.AreEqual(1, FileSystemSafetyContractV1.MajorVersion);
        Assert.AreEqual(256, FileSystemSafetyContractV1.MaxEntriesPerOperation);
        Assert.IsTrue(FileSystemFutureExecutionCapabilitiesV1.All.Count == 3);
        Assert.IsTrue(FileSystemFutureExecutionCapabilitiesV1.All.All(static capability => !capability.Executable));

        var publicDeclaredMethods = typeof(WindowsFileSystemSafetyProviderV1)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.IsFalse(publicDeclaredMethods.Any(static method => method.Name.Contains("Execute", StringComparison.OrdinalIgnoreCase)));

        var references = typeof(WindowsFileSystemSafetyProviderV1).Assembly
            .GetReferencedAssemblies()
            .Select(static assembly => assembly.Name)
            .Where(static name => name is not null)
            .ToArray();
        CollectionAssert.DoesNotContain(references, "FileOp.Core");
        CollectionAssert.DoesNotContain(references, "FileOp.Windows");
        CollectionAssert.DoesNotContain(references, "FileOp.App");
        CollectionAssert.DoesNotContain(references, "FileOp.Indexer");
    }

    [TestMethod]
    public void OperationRequest_RejectsUnboundedEntrySets()
    {
        var entries = Enumerable.Range(0, FileSystemSafetyContractV1.MaxEntriesPerOperation + 1)
            .Select(index => new FileSystemOperationEntryRequestV1(
                $@"C:\source\file-{index}.txt",
                FileSystemObjectKindV1.File))
            .ToArray();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new FileSystemOperationRequestV1(
            FileSystemOperationKindV1.Copy,
            @"C:\source",
            @"C:\destination",
            entries));
    }

    [TestMethod]
    public void RecoveryAssessment_IsDeterministicAndFailClosed()
    {
        var provider = new WindowsFileSystemSafetyProviderV1();
        var operationId = Guid.NewGuid();

        var allMatch = CreateRecoveryRequest(
            operationId,
            _ => FileSystemRecoveryEvidenceDimensionStateV1.Matches);
        var matching = provider.AssessRecovery(allMatch);
        Assert.AreEqual(FileSystemRecoveryAssessmentStatusV1.ObservedSubsetMatches, matching.Status);
        Assert.IsFalse(matching.MutationAuthorized);

        var incomplete = CreateRecoveryRequest(
            operationId,
            dimension => dimension == FileSystemRecoveryEvidenceDimensionV1.BasicMetadata
                ? FileSystemRecoveryEvidenceDimensionStateV1.Incomplete
                : FileSystemRecoveryEvidenceDimensionStateV1.Matches);
        Assert.AreEqual(
            FileSystemRecoveryAssessmentStatusV1.EvidenceIncomplete,
            provider.AssessRecovery(incomplete).Status);

        var unavailable = CreateRecoveryRequest(
            operationId,
            dimension => dimension == FileSystemRecoveryEvidenceDimensionV1.NamedDataStreams
                ? FileSystemRecoveryEvidenceDimensionStateV1.Unavailable
                : FileSystemRecoveryEvidenceDimensionStateV1.Matches);
        Assert.AreEqual(
            FileSystemRecoveryAssessmentStatusV1.EvidenceUnavailable,
            provider.AssessRecovery(unavailable).Status);

        var changedWins = CreateRecoveryRequest(
            operationId,
            dimension => dimension switch
            {
                FileSystemRecoveryEvidenceDimensionV1.MainStream => FileSystemRecoveryEvidenceDimensionStateV1.Changed,
                FileSystemRecoveryEvidenceDimensionV1.NamedDataStreams => FileSystemRecoveryEvidenceDimensionStateV1.Unavailable,
                FileSystemRecoveryEvidenceDimensionV1.BasicMetadata => FileSystemRecoveryEvidenceDimensionStateV1.Incomplete,
                _ => FileSystemRecoveryEvidenceDimensionStateV1.Matches,
            });
        Assert.AreEqual(
            FileSystemRecoveryAssessmentStatusV1.ObservedEvidenceChanged,
            provider.AssessRecovery(changedWins).Status);
    }

    [TestMethod]
    public async Task IdentityInspection_ReturnsCanonicalStableIdentityEvidence()
    {
        using var temp = TempTree.Create();
        var file = Path.Combine(temp.SourceDirectory, "identity.txt");
        await File.WriteAllTextAsync(file, "identity-evidence");

        var provider = new WindowsFileSystemSafetyProviderV1();
        var first = await provider.InspectIdentityAsync(file);
        var second = await provider.InspectIdentityAsync(file);

        Assert.AreEqual(FileSystemEvidenceStateV1.Available, first.State);
        Assert.AreEqual(FileSystemObjectKindV1.File, first.ObjectKind);
        Assert.IsNotNull(first.Identity);
        Assert.AreEqual(first.Identity, second.Identity);
        Assert.IsFalse(first.IsLeafReparsePoint);
        Assert.AreEqual(Path.GetFullPath(file), first.RequestedPath);
        Assert.IsFalse(first.MutationAuthorized);
    }

    [TestMethod]
    public async Task CopyCollisionAsk_ProducesDecisionEvidenceWithoutAuthority()
    {
        using var temp = TempTree.Create();
        var source = Path.Combine(temp.SourceDirectory, "collision.txt");
        var destination = Path.Combine(temp.DestinationDirectory, "collision.txt");
        await File.WriteAllTextAsync(source, "source");
        await File.WriteAllTextAsync(destination, "destination");

        var provider = new WindowsFileSystemSafetyProviderV1();
        var sourceIdentity = (await provider.InspectIdentityAsync(source)).Identity;
        Assert.IsNotNull(sourceIdentity);

        var request = new FileSystemOperationRequestV1(
            FileSystemOperationKindV1.Copy,
            temp.SourceDirectory,
            temp.DestinationDirectory,
            [new FileSystemOperationEntryRequestV1(source, FileSystemObjectKindV1.File, sourceIdentity)],
            FileSystemCollisionPolicyV1.Ask);

        var evidence = await provider.InspectOperationAsync(request);

        Assert.AreEqual(FileSystemOperationEvidenceStatusV1.NeedsCallerDecision, evidence.Status, evidence.Summary);
        Assert.AreEqual(1, evidence.Items.Count);
        Assert.AreEqual(FileSystemCollisionStateV1.ExistingFile, evidence.Items[0].Collision);
        Assert.AreEqual(FileSystemOperationStrategyV1.CopyCandidate, evidence.Items[0].Strategy);
        Assert.AreEqual(FileSystemOperationItemDecisionV1.NeedsCallerDecision, evidence.Items[0].Decision);
        Assert.IsTrue(evidence.Items[0].ExpectedIdentityMatches);
        Assert.IsFalse(evidence.MutationAuthorized);
        Assert.IsFalse(evidence.Items[0].MutationAuthorized);
    }

    [TestMethod]
    public async Task ExpectedIdentityMismatch_BlocksFreshOperationEvidence()
    {
        using var temp = TempTree.Create();
        var source = Path.Combine(temp.SourceDirectory, "mismatch.txt");
        await File.WriteAllTextAsync(source, "source");

        var provider = new WindowsFileSystemSafetyProviderV1();
        var actual = (await provider.InspectIdentityAsync(source)).Identity;
        Assert.IsNotNull(actual);
        var wrong = new FileSystemIdentityV1(
            actual.Value.VolumeSerialNumber,
            actual.Value.FileReferenceNumber ^ 1UL);

        var request = new FileSystemOperationRequestV1(
            FileSystemOperationKindV1.Copy,
            temp.SourceDirectory,
            temp.DestinationDirectory,
            [new FileSystemOperationEntryRequestV1(source, FileSystemObjectKindV1.File, wrong)]);

        var evidence = await provider.InspectOperationAsync(request);

        Assert.AreEqual(FileSystemOperationEvidenceStatusV1.Blocked, evidence.Status, evidence.Summary);
        Assert.IsTrue(evidence.Items.Count > 0, evidence.Summary);
        Assert.AreEqual(FileSystemOperationItemDecisionV1.Blocked, evidence.Items.Single().Decision);
        Assert.AreEqual(false, evidence.Items.Single().ExpectedIdentityMatches);
    }

    [TestMethod]
    public async Task SameVolumeMove_MapsStrategyButCannotExecute()
    {
        using var temp = TempTree.Create();
        var source = Path.Combine(temp.SourceDirectory, "move.txt");
        await File.WriteAllTextAsync(source, "source");

        var provider = new WindowsFileSystemSafetyProviderV1();
        var identity = (await provider.InspectIdentityAsync(source)).Identity;
        Assert.IsNotNull(identity);

        var request = new FileSystemOperationRequestV1(
            FileSystemOperationKindV1.MoveSameVolume,
            temp.SourceDirectory,
            temp.DestinationDirectory,
            [new FileSystemOperationEntryRequestV1(source, FileSystemObjectKindV1.File, identity)]);

        var evidence = await provider.InspectOperationAsync(request);

        Assert.AreEqual(FileSystemOperationEvidenceStatusV1.ReadyForIndependentPolicyReview, evidence.Status, evidence.Summary);
        Assert.AreEqual(FileSystemOperationStrategyV1.SameVolumeMoveCandidate, evidence.Items.Single().Strategy);
        Assert.AreEqual(FileSystemCollisionStateV1.None, evidence.Items.Single().Collision);
        Assert.IsFalse(evidence.MutationAuthorized);
        Assert.IsTrue(File.Exists(source));
        Assert.IsFalse(File.Exists(Path.Combine(temp.DestinationDirectory, "move.txt")));
    }

    [TestMethod]
    public async Task PermanentDelete_ProtectedWindowsRootBlocksBeforeItemInspection()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.IsFalse(string.IsNullOrWhiteSpace(windows));

        var provider = new WindowsFileSystemSafetyProviderV1();
        var request = new FileSystemOperationRequestV1(
            FileSystemOperationKindV1.PermanentDelete,
            windows,
            destinationDirectoryPath: null,
            [new FileSystemOperationEntryRequestV1(
                Path.Combine(windows, "fileop-provider-does-not-touch-this-file.txt"),
                FileSystemObjectKindV1.File)]);

        var evidence = await provider.InspectOperationAsync(request);

        Assert.AreEqual(FileSystemOperationEvidenceStatusV1.Blocked, evidence.Status);
        Assert.AreEqual(0, evidence.Items.Count);
        Assert.IsTrue(evidence.SourceDirectoryProtection.IsProtected);
        Assert.IsFalse(evidence.MutationAuthorized);
    }

    private static FileSystemRecoveryAssessmentRequestV1 CreateRecoveryRequest(
        Guid operationId,
        Func<FileSystemRecoveryEvidenceDimensionV1, FileSystemRecoveryEvidenceDimensionStateV1> state) =>
        new(
            operationId,
            Enum.GetValues<FileSystemRecoveryEvidenceDimensionV1>()
                .Select(dimension => new FileSystemRecoveryDimensionEvidenceV1(dimension, state(dimension))));

    private sealed class TempTree : IDisposable
    {
        private TempTree(string root)
        {
            Root = root;
            SourceDirectory = Path.Combine(root, "source");
            DestinationDirectory = Path.Combine(root, "destination");
            Directory.CreateDirectory(SourceDirectory);
            Directory.CreateDirectory(DestinationDirectory);
        }

        public string Root { get; }

        public string SourceDirectory { get; }

        public string DestinationDirectory { get; }

        public static TempTree Create()
        {
            var root = Path.Combine(
                AppContext.BaseDirectory,
                "fixture",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TempTree(root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // Test cleanup only; provider correctness assertions have already completed.
            }
            catch (UnauthorizedAccessException)
            {
                // Test cleanup only; provider correctness assertions have already completed.
            }
        }
    }
}
