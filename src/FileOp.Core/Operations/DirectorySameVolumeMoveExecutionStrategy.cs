using System;
using System.IO;
using FileOp.Core.Models;

namespace FileOp.Core.Operations;

public enum DirectorySameVolumeMoveExecutionStrategy
{
    Blocked,
    SkipOnly,
    SameVolumeDirectoryRenameRequired,
}

/// <summary>
/// Evidence-only classification for same-volume directory Move.
/// This type grants no filesystem mutation authority.
/// </summary>
public sealed record DirectorySameVolumeMoveExecutionStrategyClassification(
    FileOperationExecutionValidationResult Validation,
    DirectorySameVolumeMoveExecutionStrategy Strategy,
    string Summary)
{
    public bool IsMutationFree => Strategy == DirectorySameVolumeMoveExecutionStrategy.SkipOnly;

    public bool GrantsMutationAuthority => false;
}

public static class DirectorySameVolumeMoveExecutionStrategyClassifier
{
    public static DirectorySameVolumeMoveExecutionStrategyClassification Classify(
        FileOperationExecutionValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentNullException.ThrowIfNull(validation.Plan);
        ArgumentNullException.ThrowIfNull(validation.Plan.Intent);
        ArgumentNullException.ThrowIfNull(validation.Plan.Intent.Entries);

        var plan = validation.Plan;
        if (plan.Kind != FileOperationKind.Move)
        {
            return Blocked(validation, "Only Move plans can enter the directory Move strategy boundary.");
        }

        if (!validation.CanBeginMutation)
        {
            return Blocked(
                validation,
                "Execution-grade validation is not Ready, so no directory Move mutation strategy can be selected.");
        }

        if (plan.Intent.Entries.Count == 0 ||
            validation.Items.Count != plan.Intent.Entries.Count)
        {
            return Blocked(
                validation,
                "Directory Move strategy classification requires a non-empty validation result matching the immutable plan entry count.");
        }

        if (!TryGetDirectoryIdentity(validation.SourceDirectory, out var sourceRootIdentity) ||
            !TryGetDirectoryIdentity(validation.DestinationDirectory, out var destinationRootIdentity))
        {
            return Blocked(
                validation,
                "Directory Move requires stable canonical identities for both source and destination roots.");
        }

        if (sourceRootIdentity == destinationRootIdentity)
        {
            return Blocked(validation, "Source and destination roots resolve to the same stable directory identity.");
        }

        if (sourceRootIdentity.VolumeSerialNumber != destinationRootIdentity.VolumeSerialNumber)
        {
            return Blocked(
                validation,
                "Directory Move currently supports same-volume rename only. Cross-volume directory Move requires a separate recursive transaction.");
        }

        if (!IsLocalDrivePath(validation.SourceDirectory.CanonicalPath) ||
            !IsLocalDrivePath(validation.DestinationDirectory.CanonicalPath))
        {
            return Blocked(
                validation,
                "The first directory rename transaction is restricted to local drive-letter paths; UNC/network directory Move remains unsupported.");
        }

        var readyCount = 0;
        for (var ordinal = 0; ordinal < validation.Items.Count; ordinal++)
        {
            var item = validation.Items[ordinal];
            var expectedEntry = plan.Intent.Entries[ordinal];
            if (item.Entry != expectedEntry || !item.Entry.IsDirectory)
            {
                return Blocked(
                    validation,
                    "Execution validation changed directory Move entry ordering/type relative to the immutable plan.");
            }

            if (item.Decision is not FileOperationExecutionValidationDecision.Ready and
                not FileOperationExecutionValidationDecision.Skip)
            {
                return Blocked(
                    validation,
                    "Directory Move accepts only Ready or explicit Skip execution decisions.");
            }

            if (item.Source.State != FileOperationCanonicalPathState.Directory ||
                item.Source.IsLeafReparsePoint ||
                item.Source.Identity is not FileIdentity sourceIdentity ||
                sourceIdentity.VolumeSerialNumber != sourceRootIdentity.VolumeSerialNumber)
            {
                return Blocked(
                    validation,
                    "A directory Move source is not a non-reparse canonical directory identity-bound to the source volume.");
            }

            if (item.Decision == FileOperationExecutionValidationDecision.Skip)
            {
                if (plan.CollisionPolicy != FileOperationCollisionPolicy.Skip ||
                    item.Destination.State is not FileOperationCanonicalPathState.File and
                        not FileOperationCanonicalPathState.Directory ||
                    item.Destination.IsLeafReparsePoint ||
                    item.Destination.Identity is not FileIdentity skippedDestinationIdentity ||
                    skippedDestinationIdentity.VolumeSerialNumber != destinationRootIdentity.VolumeSerialNumber)
                {
                    return Blocked(
                        validation,
                        "A skipped directory Move must remain bound to an existing non-reparse destination identity under an explicit Skip collision policy.");
                }

                continue;
            }

            if (item.Destination.State != FileOperationCanonicalPathState.Missing ||
                item.Destination.Identity.HasValue ||
                item.Destination.IsLeafReparsePoint)
            {
                return Blocked(
                    validation,
                    "A Ready directory Move requires a missing destination leaf with no existing destination identity.");
            }

            readyCount++;
        }

        if (readyCount == 0)
        {
            return new DirectorySameVolumeMoveExecutionStrategyClassification(
                validation,
                DirectorySameVolumeMoveExecutionStrategy.SkipOnly,
                "Every validated directory Move entry is Skip, so no namespace mutation is required.");
        }

        return new DirectorySameVolumeMoveExecutionStrategyClassification(
            validation,
            DirectorySameVolumeMoveExecutionStrategy.SameVolumeDirectoryRenameRequired,
            "Validated directory Move entries remain on one local volume and require the separate identity-bound directory rename transaction.");
    }

    private static bool TryGetDirectoryIdentity(
        FileOperationCanonicalPath path,
        out FileIdentity identity)
    {
        if (path.State == FileOperationCanonicalPathState.Directory &&
            !path.IsLeafReparsePoint &&
            path.Identity is FileIdentity stableIdentity)
        {
            identity = stableIdentity;
            return true;
        }

        identity = default;
        return false;
    }

    private static bool IsLocalDrivePath(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is { Length: >= 3 } &&
                root[1] == Path.VolumeSeparatorChar &&
                (root[2] == Path.DirectorySeparatorChar ||
                 root[2] == Path.AltDirectorySeparatorChar);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static DirectorySameVolumeMoveExecutionStrategyClassification Blocked(
        FileOperationExecutionValidationResult validation,
        string summary) =>
        new(
            validation,
            DirectorySameVolumeMoveExecutionStrategy.Blocked,
            summary + " No directory mutation authority was created.");
}
