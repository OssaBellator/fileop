using System;
using System.Linq;
using FileOp.Core.Models;

namespace FileOp.Core.Operations;

/// <summary>
/// Describes which reviewed mutation contract a validated file Move would require.
/// Classification is evidence only; it grants no filesystem mutation authority.
/// </summary>
public enum FileMoveExecutionStrategy
{
    Blocked,
    SkipOnly,
    SameVolumeRenameRequired,
    CrossVolumeCopyDeleteRequired,
}

public sealed record FileMoveExecutionStrategyClassification(
    FileOperationExecutionValidationResult Validation,
    FileMoveExecutionStrategy Strategy,
    string Summary)
{
    public bool IsMutationFree => Strategy == FileMoveExecutionStrategy.SkipOnly;
}

public static class FileMoveExecutionStrategyClassifier
{
    public static FileMoveExecutionStrategyClassification Classify(
        FileOperationExecutionValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentNullException.ThrowIfNull(validation.Plan);
        ArgumentNullException.ThrowIfNull(validation.Plan.Intent);
        ArgumentNullException.ThrowIfNull(validation.Plan.Intent.Entries);

        var plan = validation.Plan;
        if (plan.Kind != FileOperationKind.Move)
        {
            return Blocked(validation, "Only Move plans can be classified by the Move strategy boundary.");
        }

        if (!validation.CanBeginMutation)
        {
            return Blocked(
                validation,
                "Execution-grade validation is not Ready, so no Move mutation strategy can be selected.");
        }

        if (plan.Intent.Entries.Count == 0 ||
            validation.Items.Count != plan.Intent.Entries.Count)
        {
            return Blocked(
                validation,
                "Move strategy classification requires a non-empty validation result matching the immutable plan entry count.");
        }

        if (plan.Intent.Entries.Any(static entry => entry.IsDirectory))
        {
            return Blocked(
                validation,
                "Directory Move remains outside the first file-only Move strategy boundary.");
        }

        if (!TryGetDirectoryIdentity(validation.SourceDirectory, out var sourceDirectoryIdentity) ||
            !TryGetDirectoryIdentity(validation.DestinationDirectory, out var destinationDirectoryIdentity))
        {
            return Blocked(
                validation,
                "Move strategy classification requires stable canonical identities for both source and destination directories.");
        }

        if (sourceDirectoryIdentity == destinationDirectoryIdentity)
        {
            return Blocked(
                validation,
                "Source and destination directories cannot resolve to the same stable identity.");
        }

        var readyCount = 0;
        for (var ordinal = 0; ordinal < validation.Items.Count; ordinal++)
        {
            var item = validation.Items[ordinal];
            var expectedEntry = plan.Intent.Entries[ordinal];
            if (item.Entry != expectedEntry || item.Entry.IsDirectory)
            {
                return Blocked(
                    validation,
                    "Execution validation changed Move entry ordering/type relative to the immutable plan.");
            }

            if (item.Decision is not FileOperationExecutionValidationDecision.Ready and
                not FileOperationExecutionValidationDecision.Skip)
            {
                return Blocked(
                    validation,
                    "Move strategy classification accepts only Ready or Skip execution decisions.");
            }

            if (item.Source.State != FileOperationCanonicalPathState.File ||
                item.Source.Identity is not FileIdentity sourceIdentity ||
                sourceIdentity.VolumeSerialNumber != sourceDirectoryIdentity.VolumeSerialNumber)
            {
                return Blocked(
                    validation,
                    "A validated Move source file is not identity-bound to the canonical source volume.");
            }

            if (item.Decision == FileOperationExecutionValidationDecision.Skip)
            {
                if (plan.CollisionPolicy != FileOperationCollisionPolicy.Skip ||
                    item.Destination.State is not FileOperationCanonicalPathState.File and
                        not FileOperationCanonicalPathState.Directory ||
                    item.Destination.Identity is not FileIdentity skippedDestinationIdentity ||
                    skippedDestinationIdentity.VolumeSerialNumber !=
                        destinationDirectoryIdentity.VolumeSerialNumber)
                {
                    return Blocked(
                        validation,
                        "A Skip Move decision must remain bound to an existing destination identity on the canonical destination volume and an explicit Skip collision policy.");
                }

                continue;
            }

            if (item.Destination.State != FileOperationCanonicalPathState.Missing ||
                item.Destination.Identity.HasValue)
            {
                return Blocked(
                    validation,
                    "A Ready file Move must target a missing canonical destination leaf with no existing destination identity.");
            }

            readyCount++;
        }

        if (readyCount == 0)
        {
            return new FileMoveExecutionStrategyClassification(
                validation,
                FileMoveExecutionStrategy.SkipOnly,
                "Every validated Move entry is Skip. No filesystem mutation strategy is required; durable history semantics still need to remain explicit before UI execution is enabled.");
        }

        if (sourceDirectoryIdentity.VolumeSerialNumber ==
            destinationDirectoryIdentity.VolumeSerialNumber)
        {
            return new FileMoveExecutionStrategyClassification(
                validation,
                FileMoveExecutionStrategy.SameVolumeRenameRequired,
                "Validated file Move entries remain on one volume and require a separately reviewed identity-bound rename/move primitive plus durable Move history before execution can be enabled.");
        }

        return new FileMoveExecutionStrategyClassification(
            validation,
            FileMoveExecutionStrategy.CrossVolumeCopyDeleteRequired,
            "Validated file Move entries cross volumes and require a separately reviewed Copy-plus-source-delete transaction. Existing Copy support alone is not Move authorization.");
    }

    private static bool TryGetDirectoryIdentity(
        FileOperationCanonicalPath path,
        out FileIdentity identity)
    {
        if (path.State == FileOperationCanonicalPathState.Directory &&
            path.Identity is FileIdentity stableIdentity)
        {
            identity = stableIdentity;
            return true;
        }

        identity = default;
        return false;
    }

    private static FileMoveExecutionStrategyClassification Blocked(
        FileOperationExecutionValidationResult validation,
        string summary) =>
        new(
            validation,
            FileMoveExecutionStrategy.Blocked,
            summary + " No filesystem mutation authority was created.");
}
