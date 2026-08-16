using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsMoveOperationExecutionValidatorTests
{
    [TestMethod]
    public async Task CaseSensitiveSourceBlocksReadyMoveBeforeMutationHistory()
    {
        var ready = CreateReadyValidation(FileOperationKind.Move);
        var probe = new StubNamespaceProbe(path =>
            path.EndsWith(@"\Source", StringComparison.OrdinalIgnoreCase)
                ? FileOperationNamespaceCapabilityState.UnsupportedCaseSensitiveDirectory
                : FileOperationNamespaceCapabilityState.SupportedCaseInsensitive);
        var volumeProbe = new StubVolumeRelationshipProbe(FileOperationVolumeRelationshipState.SameVolume);
        var validator = CreateValidator(ready, probe, volumeProbe);

        var result = await validator.ValidateAsync(ready.Plan);

        Assert.AreEqual(1, volumeProbe.QueryCalls);
        Assert.AreEqual(1, probe.QueryCalls);
        Assert.AreEqual(ready.SourceDirectory.CanonicalPath, probe.QueriedPaths[0]);
        Assert.AreSame(ready.Plan, result.Plan);
        Assert.AreEqual(ready.Items.Count, result.Items.Count);
        Assert.AreSame(ready.Items[0], result.Items[0]);
        Assert.AreEqual(FileOperationExecutionValidationStatus.Blocked, result.Status);
        Assert.IsFalse(result.CanBeginMutation);
        StringAssert.Contains(result.Summary, "before durable mutation history");
        StringAssert.Contains(result.Summary, "UnsupportedCaseSensitiveDirectory");
        StringAssert.Contains(result.Summary, "No MutationStarted record");
    }

    [TestMethod]
    public async Task CaseSensitiveDestinationBlocksReadyMoveAfterCheckingBothRoots()
    {
        var ready = CreateReadyValidation(FileOperationKind.Move);
        var probe = new StubNamespaceProbe(path =>
            path.EndsWith(@"\Destination", StringComparison.OrdinalIgnoreCase)
                ? FileOperationNamespaceCapabilityState.UnsupportedCaseSensitiveDirectory
                : FileOperationNamespaceCapabilityState.SupportedCaseInsensitive);
        var validator = CreateValidator(ready, probe);

        var result = await validator.ValidateAsync(ready.Plan);

        Assert.AreEqual(2, probe.QueryCalls);
        Assert.AreEqual(ready.SourceDirectory.CanonicalPath, probe.QueriedPaths[0]);
        Assert.AreEqual(ready.DestinationDirectory.CanonicalPath, probe.QueriedPaths[1]);
        Assert.AreEqual(FileOperationExecutionValidationStatus.Blocked, result.Status);
        StringAssert.Contains(result.Summary, "UnsupportedCaseSensitiveDirectory");
        StringAssert.Contains(result.Summary, ready.DestinationDirectory.CanonicalPath);
    }

    [TestMethod]
    public async Task UnavailableNamespaceCapabilityBlocksReadyMove()
    {
        var ready = CreateReadyValidation(FileOperationKind.Move);
        var probe = new StubNamespaceProbe(path =>
            path.EndsWith(@"\Destination", StringComparison.OrdinalIgnoreCase)
                ? FileOperationNamespaceCapabilityState.Unavailable
                : FileOperationNamespaceCapabilityState.SupportedCaseInsensitive);
        var validator = CreateValidator(ready, probe);

        var result = await validator.ValidateAsync(ready.Plan);

        Assert.AreEqual(FileOperationExecutionValidationStatus.Blocked, result.Status);
        Assert.AreEqual(2, probe.QueryCalls);
        Assert.AreEqual(ready.SourceDirectory.CanonicalPath, probe.QueriedPaths[0]);
        Assert.AreEqual(ready.DestinationDirectory.CanonicalPath, probe.QueriedPaths[1]);
        StringAssert.Contains(result.Summary, "Unavailable");
    }

    [TestMethod]
    public async Task SupportedSameVolumeNamespacesReturnOriginalReadyMoveValidation()
    {
        var ready = CreateReadyValidation(FileOperationKind.Move);
        var probe = new StubNamespaceProbe(_ =>
            FileOperationNamespaceCapabilityState.SupportedCaseInsensitive);
        var volumeProbe = new StubVolumeRelationshipProbe(FileOperationVolumeRelationshipState.SameVolume);
        var validator = CreateValidator(ready, probe, volumeProbe);

        var result = await validator.ValidateAsync(ready.Plan);

        Assert.AreSame(ready, result);
        Assert.AreEqual(1, volumeProbe.QueryCalls);
        Assert.AreEqual(ready.SourceDirectory.CanonicalPath, volumeProbe.SourcePaths[0]);
        Assert.AreEqual(ready.DestinationDirectory.CanonicalPath, volumeProbe.DestinationPaths[0]);
        Assert.AreEqual(ready.SourceDirectory.Identity!.Value, volumeProbe.SourceIdentities[0]);
        Assert.AreEqual(ready.DestinationDirectory.Identity!.Value, volumeProbe.DestinationIdentities[0]);
        Assert.AreEqual(2, probe.QueryCalls);
        Assert.AreEqual(ready.SourceDirectory.CanonicalPath, probe.QueriedPaths[0]);
        Assert.AreEqual(ready.DestinationDirectory.CanonicalPath, probe.QueriedPaths[1]);
        Assert.IsTrue(result.CanBeginMutation);
    }

    [TestMethod]
    public async Task MutationReadyMoveWithoutRootIdentityFailsClosedBeforeNamespaceProbe()
    {
        var ready = CreateReadyValidation(
            FileOperationKind.Move,
            includeDestinationRootIdentity: false);
        var probe = new StubNamespaceProbe(_ =>
            FileOperationNamespaceCapabilityState.SupportedCaseInsensitive);
        var volumeProbe = new StubVolumeRelationshipProbe(FileOperationVolumeRelationshipState.SameVolume);
        var validator = CreateValidator(ready, probe, volumeProbe);

        var result = await validator.ValidateAsync(ready.Plan);

        Assert.AreEqual(FileOperationExecutionValidationStatus.Blocked, result.Status);
        Assert.IsFalse(result.CanBeginMutation);
        Assert.AreEqual(0, volumeProbe.QueryCalls);
        Assert.AreEqual(0, probe.QueryCalls);
        Assert.AreEqual(0, probe.QueriedPaths.Count);
        StringAssert.Contains(result.Summary, "stable source and destination root filesystem identities");
        StringAssert.Contains(result.Summary, "No durable mutation history or filesystem mutation");
    }

    [TestMethod]
    public async Task CrossVolumeMoveIsProductBlockedBeforeNamespaceProbeOrMutationHistory()
    {
        var ready = CreateReadyValidation(
            FileOperationKind.Move,
            destinationVolumeSerialNumber: 22);
        var probe = new StubNamespaceProbe(_ =>
            FileOperationNamespaceCapabilityState.SupportedCaseInsensitive);
        var volumeProbe = new StubVolumeRelationshipProbe(FileOperationVolumeRelationshipState.SameVolume);
        var validator = CreateValidator(ready, probe, volumeProbe);

        var result = await validator.ValidateAsync(ready.Plan);

        AssertCrossVolumeProductBlock(result);
        Assert.AreEqual(0, volumeProbe.QueryCalls);
        Assert.AreEqual(0, probe.QueryCalls);
    }

    [TestMethod]
    public async Task EqualVolumeSerialCollisionWithDifferentGuidIsBlockedAsCrossVolume()
    {
        var ready = CreateReadyValidation(FileOperationKind.Move);
        var probe = new StubNamespaceProbe(_ =>
            FileOperationNamespaceCapabilityState.SupportedCaseInsensitive);
        var volumeProbe = new StubVolumeRelationshipProbe(FileOperationVolumeRelationshipState.DifferentVolume);
        var validator = CreateValidator(ready, probe, volumeProbe);

        var result = await validator.ValidateAsync(ready.Plan);

        AssertCrossVolumeProductBlock(result);
        Assert.AreEqual(1, volumeProbe.QueryCalls);
        Assert.AreEqual(ready.SourceDirectory.Identity!.Value, volumeProbe.SourceIdentities[0]);
        Assert.AreEqual(ready.DestinationDirectory.Identity!.Value, volumeProbe.DestinationIdentities[0]);
        Assert.AreEqual(0, probe.QueryCalls);
    }

    [TestMethod]
    public async Task EqualVolumeSerialWithoutStrongerGuidProofFailsClosedBeforeNamespaceProbe()
    {
        var ready = CreateReadyValidation(FileOperationKind.Move);
        var probe = new StubNamespaceProbe(_ =>
            FileOperationNamespaceCapabilityState.SupportedCaseInsensitive);
        var volumeProbe = new StubVolumeRelationshipProbe(FileOperationVolumeRelationshipState.Unavailable);
        var validator = CreateValidator(ready, probe, volumeProbe);

        var result = await validator.ValidateAsync(ready.Plan);

        Assert.AreEqual(FileOperationExecutionValidationStatus.Blocked, result.Status);
        Assert.IsFalse(result.CanBeginMutation);
        Assert.AreEqual(1, volumeProbe.QueryCalls);
        Assert.AreEqual(0, probe.QueryCalls);
        StringAssert.Contains(result.Summary, "equal volume-serial evidence");
        StringAssert.Contains(result.Summary, "stub volume relationship: Unavailable");
        StringAssert.Contains(result.Summary, "No durable mutation history or filesystem mutation");
    }

    [TestMethod]
    public async Task NonMoveValidationDoesNotInvokeMoveNamespaceCapability()
    {
        var ready = CreateReadyValidation(FileOperationKind.Copy);
        var probe = new StubNamespaceProbe(_ =>
            FileOperationNamespaceCapabilityState.UnsupportedCaseSensitiveDirectory);
        var volumeProbe = new StubVolumeRelationshipProbe(FileOperationVolumeRelationshipState.DifferentVolume);
        var validator = CreateValidator(ready, probe, volumeProbe);

        var result = await validator.ValidateAsync(ready.Plan);

        Assert.AreSame(ready, result);
        Assert.AreEqual(0, volumeProbe.QueryCalls);
        Assert.AreEqual(0, probe.QueryCalls);
        Assert.AreEqual(0, probe.QueriedPaths.Count);
    }

    private static WindowsMoveOperationExecutionValidator CreateValidator(
        FileOperationExecutionValidationResult ready,
        StubNamespaceProbe namespaceProbe,
        StubVolumeRelationshipProbe? volumeProbe = null) =>
        new(
            new StaticExecutionValidator(ready),
            namespaceProbe,
            volumeProbe ?? new StubVolumeRelationshipProbe(FileOperationVolumeRelationshipState.SameVolume));

    private static void AssertCrossVolumeProductBlock(FileOperationExecutionValidationResult result)
    {
        Assert.AreEqual(FileOperationExecutionValidationStatus.Blocked, result.Status);
        Assert.IsFalse(result.CanBeginMutation);
        StringAssert.Contains(result.Summary, "Cross-volume Move is currently disabled");
        StringAssert.Contains(result.Summary, "final mutation-stability");
        Assert.IsFalse(result.Summary.Contains("security-fidelity", StringComparison.Ordinal));
        StringAssert.Contains(result.Summary, "Choose a destination on the same volume");
        StringAssert.Contains(result.Summary, "No durable history, destination Copy, or source-delete mutation");
        Assert.IsFalse(result.Summary.Contains("#186", StringComparison.Ordinal));
        Assert.IsFalse(result.Summary.Contains("#187", StringComparison.Ordinal));
    }

    private static FileOperationExecutionValidationResult CreateReadyValidation(
        FileOperationKind kind,
        ulong destinationVolumeSerialNumber = 11,
        bool includeDestinationRootIdentity = true)
    {
        var sourceDirectory = Path.GetFullPath(@"C:\Source");
        var destinationDirectory = Path.GetFullPath(
            destinationVolumeSerialNumber == 11 ? @"C:\Destination" : @"D:\Destination");
        var canonicalSourceDirectory = Path.GetFullPath(@"C:\Real\Source");
        var canonicalDestinationDirectory = Path.GetFullPath(
            destinationVolumeSerialNumber == 11 ? @"C:\Real\Destination" : @"D:\Real\Destination");
        var entry = new FileOperationEntry(
            Path.Combine(sourceDirectory, "a.txt"),
            "a.txt",
            IsDirectory: false);
        var plan = new FileOperationPlan(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero),
            kind,
            FileOperationCollisionPolicy.Stop,
            new FileOperationIntent(
                "Left",
                Guid.NewGuid(),
                sourceDirectory,
                new[] { entry },
                "Right",
                Guid.NewGuid(),
                destinationDirectory));
        var item = new FileOperationExecutionValidationItem(
            entry,
            new FileOperationCanonicalPath(
                entry.Path,
                Path.Combine(canonicalSourceDirectory, entry.Name),
                FileOperationCanonicalPathState.File,
                IsLeafReparsePoint: false,
                Identity: new FileIdentity(11, 101)),
            new FileOperationCanonicalPath(
                Path.Combine(destinationDirectory, entry.Name),
                Path.Combine(canonicalDestinationDirectory, entry.Name),
                FileOperationCanonicalPathState.Missing,
                IsLeafReparsePoint: false),
            FileOperationExecutionValidationDecision.Ready,
            "ready");

        return new FileOperationExecutionValidationResult(
            plan,
            new FileOperationCanonicalPath(
                sourceDirectory,
                canonicalSourceDirectory,
                FileOperationCanonicalPathState.Directory,
                IsLeafReparsePoint: false,
                Identity: new FileIdentity(11, 10)),
            new FileOperationCanonicalPath(
                destinationDirectory,
                canonicalDestinationDirectory,
                FileOperationCanonicalPathState.Directory,
                IsLeafReparsePoint: false,
                Identity: includeDestinationRootIdentity
                    ? new FileIdentity(destinationVolumeSerialNumber, 20)
                    : null),
            new[] { item },
            FileOperationExecutionValidationStatus.Ready,
            new DateTimeOffset(2026, 8, 14, 0, 1, 0, TimeSpan.Zero),
            "ready");
    }

    private sealed class StaticExecutionValidator : IFileOperationExecutionValidator
    {
        private readonly FileOperationExecutionValidationResult _validation;

        public StaticExecutionValidator(FileOperationExecutionValidationResult validation) =>
            _validation = validation;

        public ValueTask<FileOperationExecutionValidationResult> ValidateAsync(
            FileOperationPlan plan,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreSame(_validation.Plan, plan);
            return ValueTask.FromResult(_validation);
        }
    }

    private sealed class StubNamespaceProbe : IFileOperationNamespaceCapabilityProbe
    {
        private readonly Func<string, FileOperationNamespaceCapabilityState> _stateForPath;

        public StubNamespaceProbe(Func<string, FileOperationNamespaceCapabilityState> stateForPath) =>
            _stateForPath = stateForPath;

        public int QueryCalls { get; private set; }

        public System.Collections.Generic.List<string> QueriedPaths { get; } = new();

        public FileOperationNamespaceCapability QueryDirectory(string canonicalDirectoryPath)
        {
            QueryCalls++;
            QueriedPaths.Add(canonicalDirectoryPath);
            var state = _stateForPath(canonicalDirectoryPath);
            return new FileOperationNamespaceCapability(
                canonicalDirectoryPath,
                state,
                $"stub capability: {state}");
        }
    }

    private sealed class StubVolumeRelationshipProbe : IFileOperationVolumeRelationshipProbe
    {
        private readonly FileOperationVolumeRelationshipState _state;

        public StubVolumeRelationshipProbe(FileOperationVolumeRelationshipState state) =>
            _state = state;

        public int QueryCalls { get; private set; }

        public System.Collections.Generic.List<string> SourcePaths { get; } = new();

        public System.Collections.Generic.List<string> DestinationPaths { get; } = new();

        public System.Collections.Generic.List<FileIdentity> SourceIdentities { get; } = new();

        public System.Collections.Generic.List<FileIdentity> DestinationIdentities { get; } = new();

        public FileOperationVolumeRelationship Query(
            string canonicalSourceDirectoryPath,
            FileIdentity expectedSourceIdentity,
            string canonicalDestinationDirectoryPath,
            FileIdentity expectedDestinationIdentity)
        {
            QueryCalls++;
            SourcePaths.Add(canonicalSourceDirectoryPath);
            DestinationPaths.Add(canonicalDestinationDirectoryPath);
            SourceIdentities.Add(expectedSourceIdentity);
            DestinationIdentities.Add(expectedDestinationIdentity);
            return new FileOperationVolumeRelationship(
                _state,
                _state == FileOperationVolumeRelationshipState.Unavailable
                    ? null
                    : @"\\?\Volume{11111111-1111-1111-1111-111111111111}\",
                _state switch
                {
                    FileOperationVolumeRelationshipState.SameVolume =>
                        @"\\?\Volume{11111111-1111-1111-1111-111111111111}\",
                    FileOperationVolumeRelationshipState.DifferentVolume =>
                        @"\\?\Volume{22222222-2222-2222-2222-222222222222}\",
                    _ => null,
                },
                $"stub volume relationship: {_state}");
        }
    }
}
