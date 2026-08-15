using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class DirectoryCopyFreshManifestGateTests
{
    private const uint Volume = 51;

    [TestMethod]
    public async Task ExactFreshManifestAllowsOnlyDurableHistoryBoundary()
    {
        var reviewed = CreateManifest(rootReference: 1, fileReference: 2);
        var fresh = CreateManifest(rootReference: 1, fileReference: 2);
        var gate = new DirectoryCopyFreshManifestGate(
            new FakeAcquirer(Ready(fresh)));

        var result = await gate.PrepareAsync(reviewed);

        Assert.AreEqual(
            DirectoryCopyFreshManifestGateStatus.ReadyForDurableHistory,
            result.Status);
        Assert.IsTrue(result.CanBeginDurableHistory);
        Assert.IsNotNull(result.Revalidation);
        Assert.IsTrue(result.Revalidation.EvidenceStillMatches);
        Assert.IsFalse(result.GrantsMutationAuthority);
        Assert.IsFalse(result.GrantsCopyAuthority);
        Assert.IsFalse(result.GrantsCreateAuthority);
        Assert.IsFalse(result.GrantsDeleteAuthority);
    }

    [TestMethod]
    public async Task ChangedObjectIdentityFailsBeforeDurableHistory()
    {
        var reviewed = CreateManifest(rootReference: 1, fileReference: 2);
        var fresh = CreateManifest(rootReference: 1, fileReference: 3);
        var gate = new DirectoryCopyFreshManifestGate(
            new FakeAcquirer(Ready(fresh)));

        var result = await gate.PrepareAsync(reviewed);

        Assert.AreEqual(
            DirectoryCopyFreshManifestGateStatus.ReviewedTreeChanged,
            result.Status);
        Assert.IsFalse(result.CanBeginDurableHistory);
        Assert.IsNotNull(result.Revalidation);
        Assert.IsFalse(result.Revalidation.EvidenceStillMatches);
        Assert.AreEqual(
            DirectoryOperationTreeManifestChangeKind.EntryIdentityChanged,
            result.Revalidation.Changes[0].Kind);
    }

    [TestMethod]
    public async Task UnsupportedFreshFidelityFailsClosedWithoutRevalidation()
    {
        var reviewed = CreateManifest(rootReference: 1, fileReference: 2);
        var fidelity = new DirectoryOperationFidelityEvidence(
            reviewed.CanonicalRootPath,
            DirectoryOperationFidelityFeature.AlternateDataStreams,
            EnumerationComplete: true,
            MetadataInspectionComplete: true);
        var acquisition = DirectoryOperationTreeManifestAcquisitionResult.Unsupported(
            fidelity,
            "alternate streams present");
        var gate = new DirectoryCopyFreshManifestGate(new FakeAcquirer(acquisition));

        var result = await gate.PrepareAsync(reviewed);

        Assert.AreEqual(
            DirectoryCopyFreshManifestGateStatus.AcquisitionUnsupported,
            result.Status);
        Assert.IsFalse(result.CanBeginDurableHistory);
        Assert.IsNull(result.Revalidation);
        Assert.IsFalse(result.GrantsMutationAuthority);
    }

    [TestMethod]
    public async Task UnavailableMetadataInspectionFailsClosedWithoutRevalidation()
    {
        var reviewed = CreateManifest(rootReference: 1, fileReference: 2);
        var fidelity = new DirectoryOperationFidelityEvidence(
            reviewed.CanonicalRootPath,
            DirectoryOperationFidelityFeature.None,
            EnumerationComplete: true,
            MetadataInspectionComplete: false,
            new[] { "EA inspection unavailable" });
        var acquisition = DirectoryOperationTreeManifestAcquisitionResult.Unavailable(
            fidelity,
            "metadata inspection incomplete");
        var gate = new DirectoryCopyFreshManifestGate(new FakeAcquirer(acquisition));

        var result = await gate.PrepareAsync(reviewed);

        Assert.AreEqual(
            DirectoryCopyFreshManifestGateStatus.AcquisitionUnavailable,
            result.Status);
        Assert.IsFalse(result.CanBeginDurableHistory);
        Assert.IsNull(result.Revalidation);
    }

    [TestMethod]
    public void ReadyAcquisitionRejectsIncompleteFidelityEvidence()
    {
        var manifest = CreateManifest(rootReference: 1, fileReference: 2);
        var incomplete = new DirectoryOperationFidelityEvidence(
            manifest.CanonicalRootPath,
            DirectoryOperationFidelityFeature.None,
            EnumerationComplete: true,
            MetadataInspectionComplete: false);

        Assert.ThrowsException<ArgumentException>(() =>
            DirectoryOperationTreeManifestAcquisitionResult.Ready(
                manifest,
                incomplete,
                "should fail"));
    }

    [TestMethod]
    public void UnsupportedAcquisitionCannotPublishManifest()
    {
        var manifest = CreateManifest(rootReference: 1, fileReference: 2);
        var constructor = typeof(DirectoryOperationTreeManifestAcquisitionResult).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[]
            {
                typeof(DirectoryOperationTreeManifestAcquisitionStatus),
                typeof(DirectoryOperationTreeManifest),
                typeof(DirectoryOperationFidelityEvidence),
                typeof(string),
            },
            modifiers: null)
            ?? throw new AssertFailedException("Acquisition invariant constructor was not found.");

        var exception = Assert.ThrowsException<TargetInvocationException>(() =>
            constructor.Invoke(new object?[]
            {
                DirectoryOperationTreeManifestAcquisitionStatus.Unsupported,
                manifest,
                PlainFidelity(manifest.CanonicalRootPath),
                "unsupported",
            }));

        Assert.IsInstanceOfType<ArgumentException>(exception.InnerException);
    }

    [TestMethod]
    public void ReadyGateCannotBeForgedFromUnrelatedMatchingRevalidation()
    {
        var reviewed = CreateManifest(rootReference: 1, fileReference: 2);
        var fresh = CreateManifest(rootReference: 1, fileReference: 2);
        var unrelatedInitial = CreateManifest(rootReference: 1, fileReference: 2);
        var acquisition = Ready(fresh);
        var unrelatedRevalidation = DirectoryOperationTreeManifestRevalidator.Compare(
            unrelatedInitial,
            fresh);

        Assert.ThrowsException<ArgumentException>(() =>
            new DirectoryCopyFreshManifestGateResult(
                DirectoryCopyFreshManifestGateStatus.ReadyForDurableHistory,
                reviewed,
                acquisition,
                unrelatedRevalidation,
                "forged"));
    }

    [TestMethod]
    public async Task CancellationIsObservedBeforeAcquisition()
    {
        var reviewed = CreateManifest(rootReference: 1, fileReference: 2);
        var acquirer = new FakeAcquirer(Ready(reviewed));
        var gate = new DirectoryCopyFreshManifestGate(acquirer);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
            await gate.PrepareAsync(reviewed, cancellation.Token));
        Assert.AreEqual(0, acquirer.CallCount);
    }

    private static DirectoryOperationTreeManifestAcquisitionResult Ready(
        DirectoryOperationTreeManifest manifest) =>
        DirectoryOperationTreeManifestAcquisitionResult.Ready(
            manifest,
            PlainFidelity(manifest.CanonicalRootPath),
            "fresh complete plain-tree evidence");

    private static DirectoryOperationTreeManifest CreateManifest(
        ulong rootReference,
        ulong fileReference)
    {
        var root = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "FileOp.DirectoryCopyFreshManifestGate.Tests",
            "root"));
        var rootIdentity = new FileIdentity(Volume, rootReference);
        var child = Path.Combine(root, "file.txt");
        return DirectoryOperationTreeManifest.Create(
            PlainFidelity(root),
            new FileOperationCanonicalPath(
                root,
                root,
                FileOperationCanonicalPathState.Directory,
                IsLeafReparsePoint: false,
                Identity: rootIdentity),
            new[]
            {
                new DirectoryOperationTreeEntryEvidence(
                    "file.txt",
                    new FileOperationCanonicalPath(
                        child,
                        child,
                        FileOperationCanonicalPathState.File,
                        IsLeafReparsePoint: false,
                        Identity: new FileIdentity(Volume, fileReference))),
            });
    }

    private static DirectoryOperationFidelityEvidence PlainFidelity(string root) =>
        new(
            root,
            DirectoryOperationFidelityFeature.None,
            EnumerationComplete: true,
            MetadataInspectionComplete: true);

    private sealed class FakeAcquirer : IDirectoryOperationTreeManifestAcquirer
    {
        private readonly DirectoryOperationTreeManifestAcquisitionResult _result;

        public FakeAcquirer(DirectoryOperationTreeManifestAcquisitionResult result)
        {
            _result = result;
        }

        public int CallCount { get; private set; }

        public ValueTask<DirectoryOperationTreeManifestAcquisitionResult> AcquireFreshAsync(
            DirectoryOperationTreeManifest reviewedManifest,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(_result);
        }
    }
}
