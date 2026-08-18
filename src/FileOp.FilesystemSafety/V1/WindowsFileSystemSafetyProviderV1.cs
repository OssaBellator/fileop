using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FileOp.FilesystemSafety.V1;

/// <summary>
/// Windows implementation of the V1 read-only filesystem-safety provider.
/// It opens handles only for metadata/identity queries and contains no filesystem mutation API.
/// </summary>
public sealed class WindowsFileSystemSafetyProviderV1 : IFileSystemSafetyProviderV1
{
    private readonly WindowsCanonicalPathReaderV1 _paths = new();
    private readonly WindowsProtectedLocationPolicyV1 _protectedLocations;

    public WindowsFileSystemSafetyProviderV1()
        : this(protectedTrees: null)
    {
    }

    /// <summary>
    /// Allows tests/hosts to add protected trees while retaining the default Windows protected locations.
    /// </summary>
    public WindowsFileSystemSafetyProviderV1(IEnumerable<string>? protectedTrees)
    {
        _protectedLocations = new WindowsProtectedLocationPolicyV1(protectedTrees);
    }

    public ValueTask<FileSystemPathEvidenceV1> InspectIdentityAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<FileSystemPathEvidenceV1>(Task.Run(
            () => _paths.Read(path, allowMissingLeaf: false, cancellationToken),
            cancellationToken));
    }

    public async ValueTask<FileSystemOperationEvidenceV1> InspectOperationAsync(
        FileSystemOperationRequestV1 request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var sourceDirectory = await ReadAsync(
            request.SourceDirectoryPath,
            allowMissingLeaf: false,
            cancellationToken).ConfigureAwait(false);
        var sourceProtection = _protectedLocations.Evaluate(sourceDirectory.CanonicalPath);

        FileSystemPathEvidenceV1? destinationDirectory = null;
        FileSystemProtectedLocationEvidenceV1? destinationProtection = null;
        if (request.Kind != FileSystemOperationKindV1.PermanentDelete)
        {
            destinationDirectory = await ReadAsync(
                request.DestinationDirectoryPath!,
                allowMissingLeaf: false,
                cancellationToken).ConfigureAwait(false);
            destinationProtection = _protectedLocations.Evaluate(destinationDirectory.CanonicalPath);
        }

        var rootProblem = ValidateRoots(
            request,
            sourceDirectory,
            destinationDirectory,
            sourceProtection,
            destinationProtection);
        if (rootProblem is not null)
        {
            return BlockPlan(
                request,
                sourceDirectory,
                destinationDirectory,
                sourceProtection,
                destinationProtection,
                rootProblem);
        }

        var sourceNamespace = WindowsNamespaceCapabilityProbeV1.Query(sourceDirectory.CanonicalPath);
        if (sourceNamespace != NamespaceCapabilityV1.SupportedCaseInsensitive)
        {
            return BlockPlan(
                request,
                sourceDirectory,
                destinationDirectory,
                sourceProtection,
                destinationProtection,
                "The canonical source directory does not expose the case-insensitive namespace semantics required by the V1 exact-name model.");
        }

        if (destinationDirectory is not null)
        {
            var destinationNamespace = WindowsNamespaceCapabilityProbeV1.Query(destinationDirectory.CanonicalPath);
            if (destinationNamespace != NamespaceCapabilityV1.SupportedCaseInsensitive)
            {
                return BlockPlan(
                    request,
                    sourceDirectory,
                    destinationDirectory,
                    sourceProtection,
                    destinationProtection,
                    "The canonical destination directory does not expose the case-insensitive namespace semantics required by the V1 exact-name model.");
            }
        }

        var items = new List<FileSystemOperationItemEvidenceV1>(request.Entries.Count);
        for (var ordinal = 0; ordinal < request.Entries.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(await InspectItemAsync(
                ordinal,
                request,
                request.Entries[ordinal],
                sourceDirectory,
                destinationDirectory,
                cancellationToken).ConfigureAwait(false));
        }

        var status = items.Any(static item => item.Decision == FileSystemOperationItemDecisionV1.Blocked)
            ? FileSystemOperationEvidenceStatusV1.Blocked
            : items.Any(static item => item.Decision == FileSystemOperationItemDecisionV1.NeedsCallerDecision)
                ? FileSystemOperationEvidenceStatusV1.NeedsCallerDecision
                : FileSystemOperationEvidenceStatusV1.ReadyForIndependentPolicyReview;

        var ready = items.Count(static item => item.Decision == FileSystemOperationItemDecisionV1.ReadyForIndependentPolicyReview);
        var skipped = items.Count(static item => item.Decision == FileSystemOperationItemDecisionV1.Skip);
        var needsDecision = items.Count(static item => item.Decision == FileSystemOperationItemDecisionV1.NeedsCallerDecision);
        var blocked = items.Count(static item => item.Decision == FileSystemOperationItemDecisionV1.Blocked);

        return new FileSystemOperationEvidenceV1(
            request,
            sourceDirectory,
            destinationDirectory,
            sourceProtection,
            destinationProtection,
            Array.AsReadOnly(items.ToArray()),
            status,
            $"Operation evidence: {ready} ready for independent policy review, {skipped} skipped, {needsDecision} require caller collision decisions, {blocked} blocked. {FileSystemSafetyContractV1.EvidenceNotice}");
    }

    public FileSystemRecoveryAssessmentV1 AssessRecovery(FileSystemRecoveryAssessmentRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var states = request.Dimensions.Select(static dimension => dimension.State).ToArray();
        var status = states.Contains(FileSystemRecoveryEvidenceDimensionStateV1.Changed)
            ? FileSystemRecoveryAssessmentStatusV1.ObservedEvidenceChanged
            : states.Contains(FileSystemRecoveryEvidenceDimensionStateV1.Unavailable)
                ? FileSystemRecoveryAssessmentStatusV1.EvidenceUnavailable
                : states.Contains(FileSystemRecoveryEvidenceDimensionStateV1.Incomplete)
                    ? FileSystemRecoveryAssessmentStatusV1.EvidenceIncomplete
                    : FileSystemRecoveryAssessmentStatusV1.ObservedSubsetMatches;

        var summary = status switch
        {
            FileSystemRecoveryAssessmentStatusV1.ObservedEvidenceChanged =>
                "At least one recorded recovery-evidence dimension changed. No recovery mutation is authorized.",
            FileSystemRecoveryAssessmentStatusV1.EvidenceUnavailable =>
                "No observed dimension changed, but at least one required comparison is unavailable. No recovery mutation is authorized.",
            FileSystemRecoveryAssessmentStatusV1.EvidenceIncomplete =>
                "No observed dimension changed or is unavailable, but at least one history dimension is incomplete. No recovery mutation is authorized.",
            _ =>
                "All V1 observed recovery-evidence dimensions match. This is subset evidence only and does not authorize deletion, replacement, undo or any recovery mutation.",
        };

        return new FileSystemRecoveryAssessmentV1(
            request.OperationId,
            request.Dimensions,
            status,
            summary);
    }

    private ValueTask<FileSystemPathEvidenceV1> ReadAsync(
        string path,
        bool allowMissingLeaf,
        CancellationToken cancellationToken) =>
        new(Task.Run(
            () => _paths.Read(path, allowMissingLeaf, cancellationToken),
            cancellationToken));

    private async ValueTask<FileSystemOperationItemEvidenceV1> InspectItemAsync(
        int ordinal,
        FileSystemOperationRequestV1 operation,
        FileSystemOperationEntryRequestV1 entry,
        FileSystemPathEvidenceV1 sourceDirectory,
        FileSystemPathEvidenceV1? destinationDirectory,
        CancellationToken cancellationToken)
    {
        if (entry.ExpectedKind is not FileSystemObjectKindV1.File and not FileSystemObjectKindV1.Directory)
        {
            return Blocked(
                ordinal,
                entry,
                Error(entry.SourcePath, "ExpectedKindRequired", "The entry must declare whether a file or directory is expected."),
                destination: null,
                FileSystemOperationStrategyV1.None,
                "The expected object kind is not supported.");
        }

        if (!TryNormalize(entry.SourcePath, out var requestedSource))
        {
            return Blocked(
                ordinal,
                entry,
                Error(entry.SourcePath, "InvalidSourcePath", "The source path cannot be normalized."),
                destination: null,
                FileSystemOperationStrategyV1.None,
                "The captured source entry path is invalid.");
        }

        var requestedParent = Path.GetDirectoryName(requestedSource);
        if (string.IsNullOrWhiteSpace(requestedParent) ||
            !PathsEqual(requestedParent, operation.SourceDirectoryPath))
        {
            return Blocked(
                ordinal,
                entry,
                Missing(requestedSource),
                destination: null,
                FileSystemOperationStrategyV1.None,
                "The captured source entry is not a direct child of the captured source directory.");
        }

        var leafName = Path.GetFileName(requestedSource);
        if (string.IsNullOrWhiteSpace(leafName) || leafName.Contains(Path.VolumeSeparatorChar))
        {
            return Blocked(
                ordinal,
                entry,
                Missing(requestedSource),
                destination: null,
                FileSystemOperationStrategyV1.None,
                "The source leaf name is empty or uses an alternate data stream namespace.");
        }

        var source = await ReadAsync(requestedSource, allowMissingLeaf: false, cancellationToken).ConfigureAwait(false);
        var sourceProtection = _protectedLocations.Evaluate(source.CanonicalPath);
        bool? expectedIdentityMatches = entry.ExpectedIdentity is null || source.Identity is null
            ? null
            : entry.ExpectedIdentity.Value == source.Identity.Value;

        if (!IsUsableSource(source, entry.ExpectedKind, sourceDirectory, out var sourceProblem))
        {
            return Blocked(
                ordinal,
                entry,
                source,
                destination: null,
                FileSystemOperationStrategyV1.None,
                sourceProblem,
                sourceProtection,
                expectedIdentityMatches);
        }

        if (expectedIdentityMatches == false)
        {
            return Blocked(
                ordinal,
                entry,
                source,
                destination: null,
                FileSystemOperationStrategyV1.None,
                "The current stable filesystem identity differs from the caller-captured identity.",
                sourceProtection,
                expectedIdentityMatches);
        }

        if (entry.ExpectedKind == FileSystemObjectKindV1.Directory)
        {
            return Blocked(
                ordinal,
                entry,
                source,
                destination: null,
                FileSystemOperationStrategyV1.DirectoryOperationUnsupported,
                "V1 deliberately excludes recursive directory mutation strategy; directory entries remain evidence-only and blocked for execution mapping.",
                sourceProtection,
                expectedIdentityMatches);
        }

        if (operation.Kind == FileSystemOperationKindV1.PermanentDelete)
        {
            if (sourceProtection.Decision != FileSystemProtectedLocationDecisionV1.OutsideProtectedTrees)
            {
                return Blocked(
                    ordinal,
                    entry,
                    source,
                    destination: null,
                    FileSystemOperationStrategyV1.PermanentDeleteCandidate,
                    $"Permanent-delete review is blocked by protected-location evidence: {sourceProtection.Reason}",
                    sourceProtection,
                    expectedIdentityMatches);
            }

            return new FileSystemOperationItemEvidenceV1(
                ordinal,
                entry,
                source,
                Destination: null,
                sourceProtection,
                DestinationProtection: null,
                FileSystemCollisionStateV1.NotApplicable,
                FileSystemOperationStrategyV1.PermanentDeleteCandidate,
                FileSystemOperationItemDecisionV1.ReadyForIndependentPolicyReview,
                expectedIdentityMatches,
                "The current file is a direct non-reparse child with stable identity outside protected trees. Permanent-delete execution is not exposed by V1.");
        }

        if (destinationDirectory is null)
        {
            return Blocked(
                ordinal,
                entry,
                source,
                destination: null,
                FileSystemOperationStrategyV1.None,
                "Destination directory evidence is unavailable.",
                sourceProtection,
                expectedIdentityMatches);
        }

        var requestedDestination = Path.Combine(operation.DestinationDirectoryPath!, leafName);
        var destination = await ReadAsync(
            requestedDestination,
            allowMissingLeaf: true,
            cancellationToken).ConfigureAwait(false);
        var destinationProtection = _protectedLocations.Evaluate(destination.CanonicalPath);

        if (destination.State is FileSystemEvidenceStateV1.Inaccessible or FileSystemEvidenceStateV1.Error)
        {
            return Blocked(
                ordinal,
                entry,
                source,
                destination,
                FileSystemOperationStrategyV1.None,
                "The destination cannot be resolved safely.",
                sourceProtection,
                expectedIdentityMatches,
                destinationProtection,
                FileSystemCollisionStateV1.Unavailable);
        }

        if (destination.IsLeafReparsePoint)
        {
            return Blocked(
                ordinal,
                entry,
                source,
                destination,
                FileSystemOperationStrategyV1.None,
                "An existing destination reparse point is not a safe operation target.",
                sourceProtection,
                expectedIdentityMatches,
                destinationProtection,
                FileSystemCollisionStateV1.ExistingReparsePoint);
        }

        var destinationParent = Path.GetDirectoryName(NormalizeForComparison(destination.CanonicalPath));
        if (string.IsNullOrWhiteSpace(destinationParent) ||
            !PathsEqual(destinationParent, destinationDirectory.CanonicalPath))
        {
            return Blocked(
                ordinal,
                entry,
                source,
                destination,
                FileSystemOperationStrategyV1.None,
                "The destination resolves outside the canonical destination directory.",
                sourceProtection,
                expectedIdentityMatches,
                destinationProtection);
        }

        if (destinationProtection.Decision != FileSystemProtectedLocationDecisionV1.OutsideProtectedTrees)
        {
            return Blocked(
                ordinal,
                entry,
                source,
                destination,
                FileSystemOperationStrategyV1.None,
                $"The destination is blocked by protected-location evidence: {destinationProtection.Reason}",
                sourceProtection,
                expectedIdentityMatches,
                destinationProtection);
        }

        FileSystemOperationStrategyV1 strategy;
        if (operation.Kind == FileSystemOperationKindV1.MoveSameVolume)
        {
            if (sourceProtection.Decision != FileSystemProtectedLocationDecisionV1.OutsideProtectedTrees)
            {
                return Blocked(
                    ordinal,
                    entry,
                    source,
                    destination,
                    FileSystemOperationStrategyV1.SameVolumeMoveCandidate,
                    $"Same-volume move review is blocked because the source would be mutated inside a protected location: {sourceProtection.Reason}",
                    sourceProtection,
                    expectedIdentityMatches,
                    destinationProtection);
            }

            if (source.Identity is null || destinationDirectory.Identity is null ||
                source.Identity.Value.VolumeSerialNumber != destinationDirectory.Identity.Value.VolumeSerialNumber)
            {
                return Blocked(
                    ordinal,
                    entry,
                    source,
                    destination,
                    FileSystemOperationStrategyV1.CrossVolumeMoveUnsupported,
                    "The source and destination directory are not proven to be on the same volume; V1 maps only same-volume move strategy.",
                    sourceProtection,
                    expectedIdentityMatches,
                    destinationProtection);
            }

            strategy = FileSystemOperationStrategyV1.SameVolumeMoveCandidate;
        }
        else
        {
            strategy = FileSystemOperationStrategyV1.CopyCandidate;
        }

        var collision = destination.State == FileSystemEvidenceStateV1.Missing
            ? FileSystemCollisionStateV1.None
            : destination.ObjectKind == FileSystemObjectKindV1.Directory
                ? FileSystemCollisionStateV1.ExistingDirectory
                : FileSystemCollisionStateV1.ExistingFile;

        if (collision == FileSystemCollisionStateV1.None)
        {
            return new FileSystemOperationItemEvidenceV1(
                ordinal,
                entry,
                source,
                destination,
                sourceProtection,
                destinationProtection,
                collision,
                strategy,
                FileSystemOperationItemDecisionV1.ReadyForIndependentPolicyReview,
                expectedIdentityMatches,
                "No destination collision is currently visible. The strategy is evidence only and has no execution authority.");
        }

        return operation.CollisionPolicy switch
        {
            FileSystemCollisionPolicyV1.Ask => new FileSystemOperationItemEvidenceV1(
                ordinal,
                entry,
                source,
                destination,
                sourceProtection,
                destinationProtection,
                collision,
                strategy,
                FileSystemOperationItemDecisionV1.NeedsCallerDecision,
                expectedIdentityMatches,
                "The destination already exists; the caller must make an explicit collision decision outside this provider."),
            FileSystemCollisionPolicyV1.Skip => new FileSystemOperationItemEvidenceV1(
                ordinal,
                entry,
                source,
                destination,
                sourceProtection,
                destinationProtection,
                collision,
                strategy,
                FileSystemOperationItemDecisionV1.Skip,
                expectedIdentityMatches,
                "The destination already exists and the requested evidence policy classifies this item as skipped."),
            _ => Blocked(
                ordinal,
                entry,
                source,
                destination,
                strategy,
                "The destination already exists and the requested evidence policy stops on collision.",
                sourceProtection,
                expectedIdentityMatches,
                destinationProtection,
                collision),
        };
    }

    private static string? ValidateRoots(
        FileSystemOperationRequestV1 request,
        FileSystemPathEvidenceV1 sourceDirectory,
        FileSystemPathEvidenceV1? destinationDirectory,
        FileSystemProtectedLocationEvidenceV1 sourceProtection,
        FileSystemProtectedLocationEvidenceV1? destinationProtection)
    {
        if (!IsUsableDirectory(sourceDirectory))
        {
            return "The source directory is missing, inaccessible, redirected through a leaf reparse point, changed type, or lacks stable identity evidence.";
        }

        if (request.Kind is FileSystemOperationKindV1.MoveSameVolume or FileSystemOperationKindV1.PermanentDelete &&
            sourceProtection.Decision != FileSystemProtectedLocationDecisionV1.OutsideProtectedTrees)
        {
            return $"The source directory is blocked for mutation-adjacent review by protected-location evidence: {sourceProtection.Reason}";
        }

        if (request.Kind != FileSystemOperationKindV1.PermanentDelete)
        {
            if (destinationDirectory is null || !IsUsableDirectory(destinationDirectory))
            {
                return "The destination directory is missing, inaccessible, redirected through a leaf reparse point, changed type, or lacks stable identity evidence.";
            }
            if (destinationProtection is null ||
                destinationProtection.Decision != FileSystemProtectedLocationDecisionV1.OutsideProtectedTrees)
            {
                return $"The destination directory is blocked for mutation-adjacent review by protected-location evidence: {destinationProtection?.Reason ?? "classification unavailable"}";
            }
            if (PathsEqual(sourceDirectory.CanonicalPath, destinationDirectory.CanonicalPath))
            {
                return "The canonical source and destination directories are the same.";
            }
        }

        return null;
    }

    private static bool IsUsableDirectory(FileSystemPathEvidenceV1 evidence) =>
        evidence.State == FileSystemEvidenceStateV1.Available &&
        evidence.ObjectKind == FileSystemObjectKindV1.Directory &&
        !evidence.IsLeafReparsePoint &&
        evidence.Identity is not null;

    private static bool IsUsableSource(
        FileSystemPathEvidenceV1 source,
        FileSystemObjectKindV1 expectedKind,
        FileSystemPathEvidenceV1 sourceDirectory,
        out string problem)
    {
        if (source.State != FileSystemEvidenceStateV1.Available)
        {
            problem = "The source entry is missing or cannot be inspected safely.";
            return false;
        }
        if (source.ObjectKind != expectedKind)
        {
            problem = "The source entry type differs from the caller-captured expected type.";
            return false;
        }
        if (source.Identity is null)
        {
            problem = "The source entry lacks stable filesystem identity evidence.";
            return false;
        }
        if (source.IsLeafReparsePoint)
        {
            problem = "A source entry that is itself a reparse point is outside the V1 operation-safety boundary.";
            return false;
        }

        var canonicalParent = Path.GetDirectoryName(NormalizeForComparison(source.CanonicalPath));
        if (string.IsNullOrWhiteSpace(canonicalParent) ||
            !PathsEqual(canonicalParent, sourceDirectory.CanonicalPath))
        {
            problem = "The source resolves outside the canonical captured source directory.";
            return false;
        }

        problem = string.Empty;
        return true;
    }

    private static FileSystemOperationEvidenceV1 BlockPlan(
        FileSystemOperationRequestV1 request,
        FileSystemPathEvidenceV1 sourceDirectory,
        FileSystemPathEvidenceV1? destinationDirectory,
        FileSystemProtectedLocationEvidenceV1 sourceProtection,
        FileSystemProtectedLocationEvidenceV1? destinationProtection,
        string summary) =>
        new(
            request,
            sourceDirectory,
            destinationDirectory,
            sourceProtection,
            destinationProtection,
            Array.Empty<FileSystemOperationItemEvidenceV1>(),
            FileSystemOperationEvidenceStatusV1.Blocked,
            $"{summary} {FileSystemSafetyContractV1.EvidenceNotice}");

    private static FileSystemOperationItemEvidenceV1 Blocked(
        int ordinal,
        FileSystemOperationEntryRequestV1 request,
        FileSystemPathEvidenceV1 source,
        FileSystemPathEvidenceV1? destination,
        FileSystemOperationStrategyV1 strategy,
        string message,
        FileSystemProtectedLocationEvidenceV1? sourceProtection = null,
        bool? expectedIdentityMatches = null,
        FileSystemProtectedLocationEvidenceV1? destinationProtection = null,
        FileSystemCollisionStateV1 collision = FileSystemCollisionStateV1.NotApplicable) =>
        new(
            ordinal,
            request,
            source,
            destination,
            sourceProtection ?? Unclassified("Source protected-location classification was not reached."),
            destinationProtection,
            collision,
            strategy,
            FileSystemOperationItemDecisionV1.Blocked,
            expectedIdentityMatches,
            message);

    private static FileSystemProtectedLocationEvidenceV1 Unclassified(string reason) =>
        new(FileSystemProtectedLocationDecisionV1.Unclassified, reason);

    private static FileSystemPathEvidenceV1 Missing(string path) =>
        new(
            path,
            path,
            FileSystemEvidenceStateV1.Missing,
            FileSystemObjectKindV1.Unknown,
            IsLeafReparsePoint: false);

    private static FileSystemPathEvidenceV1 Error(string path, string code, string message) =>
        new(
            path,
            path,
            FileSystemEvidenceStateV1.Error,
            FileSystemObjectKindV1.Unknown,
            IsLeafReparsePoint: false,
            Identity: null,
            ErrorCode: code,
            ErrorMessage: message);

    private static bool TryNormalize(string path, out string normalized)
    {
        try
        {
            normalized = Path.GetFullPath(path);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            normalized = path;
            return false;
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            NormalizeForComparison(left),
            NormalizeForComparison(right),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeForComparison(string path)
    {
        var normalized = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalized.Length == 2 && normalized[1] == Path.VolumeSeparatorChar)
        {
            normalized += Path.DirectorySeparatorChar;
        }
        return normalized;
    }
}

internal sealed class WindowsCanonicalPathReaderV1
{
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;

    public FileSystemPathEvidenceV1 Read(
        string path,
        bool allowMissingLeaf,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string requestedPath;
        try
        {
            requestedPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Error(path, path, "InvalidPath", "The path cannot be normalized.");
        }

        bool leafIsReparsePoint;
        try
        {
            leafIsReparsePoint = (File.GetAttributes(requestedPath) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return allowMissingLeaf ? ReadMissingLeaf(requestedPath, cancellationToken) : Missing(requestedPath);
        }
        catch (DirectoryNotFoundException)
        {
            return allowMissingLeaf ? ReadMissingLeaf(requestedPath, cancellationToken) : Missing(requestedPath);
        }
        catch (UnauthorizedAccessException)
        {
            return Inaccessible(requestedPath, "AccessDenied", "Windows denied path attribute inspection.");
        }
        catch (System.Security.SecurityException)
        {
            return Inaccessible(requestedPath, "AccessDenied", "Windows denied path attribute inspection.");
        }
        catch (IOException)
        {
            return Error(requestedPath, requestedPath, "IoError", "Windows could not inspect path attributes.");
        }

        using var handle = CreateFileW(
            requestedPath,
            dwDesiredAccess: 0,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return FromWin32Failure(requestedPath, Marshal.GetLastWin32Error());
        }

        if (!GetFileInformationByHandle(handle, out var information))
        {
            return FromWin32Failure(requestedPath, Marshal.GetLastWin32Error());
        }

        var finalPath = TryGetFinalPath(handle, out var finalPathError);
        if (finalPath is null)
        {
            return FromWin32Failure(requestedPath, finalPathError);
        }

        var objectKind = (information.FileAttributes & (uint)FileAttributes.Directory) != 0
            ? FileSystemObjectKindV1.Directory
            : FileSystemObjectKindV1.File;
        var identity = new FileSystemIdentityV1(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);

        return new FileSystemPathEvidenceV1(
            requestedPath,
            NormalizeFinalPath(finalPath),
            FileSystemEvidenceStateV1.Available,
            objectKind,
            leafIsReparsePoint,
            identity);
    }

    private FileSystemPathEvidenceV1 ReadMissingLeaf(
        string requestedPath,
        CancellationToken cancellationToken)
    {
        var leafName = Path.GetFileName(requestedPath);
        var parentPath = Path.GetDirectoryName(requestedPath);
        if (string.IsNullOrWhiteSpace(leafName) || string.IsNullOrWhiteSpace(parentPath))
        {
            return Error(
                requestedPath,
                requestedPath,
                "InvalidMissingLeaf",
                "A missing target must have an existing parent directory and one leaf name.");
        }

        var parent = Read(parentPath, allowMissingLeaf: false, cancellationToken);
        if (parent.State == FileSystemEvidenceStateV1.Inaccessible)
        {
            return Inaccessible(
                requestedPath,
                parent.ErrorCode ?? "AccessDenied",
                "The parent directory for the missing target is inaccessible.");
        }
        if (parent.State != FileSystemEvidenceStateV1.Available ||
            parent.ObjectKind != FileSystemObjectKindV1.Directory ||
            parent.IsLeafReparsePoint)
        {
            return Error(
                requestedPath,
                requestedPath,
                "MissingParent",
                "The parent directory for the missing target could not be resolved safely.");
        }

        return new FileSystemPathEvidenceV1(
            requestedPath,
            Path.Combine(parent.CanonicalPath, leafName),
            FileSystemEvidenceStateV1.Missing,
            FileSystemObjectKindV1.Unknown,
            IsLeafReparsePoint: false);
    }

    private static string? TryGetFinalPath(SafeFileHandle handle, out int error)
    {
        var capacity = 512;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
            {
                error = Marshal.GetLastWin32Error();
                return null;
            }
            if (length < buffer.Capacity)
            {
                error = 0;
                return buffer.ToString();
            }
            capacity = checked((int)length + 1);
        }

        error = 122;
        return null;
    }

    private static string NormalizeFinalPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }
        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) &&
            path.Length >= 6 && path[5] == Path.VolumeSeparatorChar)
        {
            return path[4..];
        }
        return path;
    }

    private static FileSystemPathEvidenceV1 FromWin32Failure(string path, int error)
    {
        if (error is ErrorFileNotFound or ErrorPathNotFound)
        {
            return Missing(path);
        }
        if (error == ErrorAccessDenied)
        {
            return Inaccessible(path, "AccessDenied", $"Windows handle inspection failed with Win32 error {error}.");
        }
        return Error(path, path, $"Win32:{error}", $"Windows handle inspection failed with Win32 error {error}.");
    }

    private static FileSystemPathEvidenceV1 Missing(string path) =>
        new(
            path,
            path,
            FileSystemEvidenceStateV1.Missing,
            FileSystemObjectKindV1.Unknown,
            IsLeafReparsePoint: false);

    private static FileSystemPathEvidenceV1 Inaccessible(string path, string code, string message) =>
        new(
            path,
            path,
            FileSystemEvidenceStateV1.Inaccessible,
            FileSystemObjectKindV1.Unknown,
            IsLeafReparsePoint: false,
            Identity: null,
            ErrorCode: code,
            ErrorMessage: message);

    private static FileSystemPathEvidenceV1 Error(
        string requestedPath,
        string canonicalPath,
        string code,
        string message) =>
        new(
            requestedPath,
            canonicalPath,
            FileSystemEvidenceStateV1.Error,
            FileSystemObjectKindV1.Unknown,
            IsLeafReparsePoint: false,
            Identity: null,
            ErrorCode: code,
            ErrorMessage: message);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

#pragma warning disable CA1838 // Caller-sized StringBuilder mirrors FileOp's reviewed GetFinalPathNameByHandleW path-resolution primitive.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile,
        StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);
#pragma warning restore CA1838

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }
}

internal sealed class WindowsProtectedLocationPolicyV1
{
    private static readonly string[] RootManagedNames =
    [
        "$Recycle.Bin",
        "System Volume Information",
        "Recovery",
        "Boot",
        "EFI",
    ];

    private readonly string[] _protectedTrees;

    public WindowsProtectedLocationPolicyV1(IEnumerable<string>? additionalProtectedTrees)
    {
        var defaults = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        };

        _protectedTrees = defaults
            .Concat(additionalProtectedTrees ?? Array.Empty<string>())
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeForComparison)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public FileSystemProtectedLocationEvidenceV1 Evaluate(string canonicalPath)
    {
        if (string.IsNullOrWhiteSpace(canonicalPath))
        {
            return Unclassified("The canonical path is empty.");
        }
        if (canonicalPath.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ||
            canonicalPath.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) ||
            canonicalPath.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase))
        {
            return Unclassified("Residual extended/device namespace paths are not classified by V1 protected-location policy.");
        }

        string normalized;
        string root;
        try
        {
            normalized = NormalizeForComparison(canonicalPath);
            root = Path.GetPathRoot(Path.GetFullPath(canonicalPath)) ?? string.Empty;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Unclassified("The canonical path cannot be normalized for protected-location classification.");
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            return Unclassified("The canonical path has no resolvable filesystem root.");
        }

        var normalizedRoot = NormalizeForComparison(root);
        if (PathsEqual(normalized, normalizedRoot))
        {
            return Protected("Filesystem volume/share roots are protected from mutation-adjacent review.");
        }

        foreach (var protectedTree in _protectedTrees)
        {
            if (IsSameOrDescendantPath(normalized, protectedTree))
            {
                return Protected($"The path is inside protected Windows tree '{protectedTree}'.");
            }
        }

        foreach (var name in RootManagedNames)
        {
            var managedRoot = NormalizeForComparison(Path.Combine(root, name));
            if (IsSameOrDescendantPath(normalized, managedRoot))
            {
                return Protected($"The path is inside protected root-managed tree '{managedRoot}'.");
            }
        }

        return new FileSystemProtectedLocationEvidenceV1(
            FileSystemProtectedLocationDecisionV1.OutsideProtectedTrees,
            "The canonical path is outside the V1 protected-location trees.");
    }

    private static FileSystemProtectedLocationEvidenceV1 Protected(string reason) =>
        new(FileSystemProtectedLocationDecisionV1.Protected, reason);

    private static FileSystemProtectedLocationEvidenceV1 Unclassified(string reason) =>
        new(FileSystemProtectedLocationDecisionV1.Unclassified, reason);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            NormalizeForComparison(left),
            NormalizeForComparison(right),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrDescendantPath(string candidate, string root)
    {
        var normalizedCandidate = NormalizeForComparison(candidate);
        var normalizedRoot = NormalizeForComparison(root);
        if (string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeForComparison(string path)
    {
        var normalized = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalized.Length == 2 && normalized[1] == Path.VolumeSeparatorChar)
        {
            normalized += Path.DirectorySeparatorChar;
        }
        return normalized;
    }
}

internal enum NamespaceCapabilityV1
{
    SupportedCaseInsensitive,
    UnsupportedCaseSensitiveDirectory,
    Unavailable,
}

internal sealed class WindowsNamespaceCapabilityProbeV1
{
    private const uint FileReadAttributes = 0x0080;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int FileCaseSensitiveInformation = 71;
    private const uint FileCsFlagCaseSensitiveDir = 0x00000001;

    public static NamespaceCapabilityV1 Query(string canonicalDirectoryPath)
    {
        using var handle = CreateFileW(
            Path.GetFullPath(canonicalDirectoryPath),
            FileReadAttributes,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return NamespaceCapabilityV1.Unavailable;
        }

        var status = NtQueryInformationFile(
            handle,
            out _,
            out var information,
            checked((uint)Marshal.SizeOf<FileCaseSensitiveInformationData>()),
            FileCaseSensitiveInformation);
        if (status < 0)
        {
            return NamespaceCapabilityV1.Unavailable;
        }

        return (information.Flags & FileCsFlagCaseSensitiveDir) != 0
            ? NamespaceCapabilityV1.UnsupportedCaseSensitiveDirectory
            : NamespaceCapabilityV1.SupportedCaseInsensitive;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationFile(
        SafeFileHandle fileHandle,
        out IoStatusBlock ioStatusBlock,
        out FileCaseSensitiveInformationData fileInformation,
        uint length,
        int fileInformationClass);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileCaseSensitiveInformationData
    {
        public uint Flags;
    }
}
