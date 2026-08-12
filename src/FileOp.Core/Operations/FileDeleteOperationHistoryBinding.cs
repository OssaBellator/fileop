using System;

namespace FileOp.Core.Operations;

/// <summary>
/// Snapshot evidence that one exact user-authorization receipt, read-only stability observation,
/// and durable delete-history entry described the same pending file at validation time.
/// This is not a mutation capability, does not prove a stability lease was acquired or remains alive,
/// and does not replace the durable Pending -> MutationStarted barrier required before mutation.
/// </summary>
public sealed class FileDeleteOperationHistoryBindingEvidence
{
    internal FileDeleteOperationHistoryBindingEvidence(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        FileDeleteOperationStabilityLeaseEvidence stabilityEvidence,
        FileDeleteOperationActionHistory historySnapshot,
        int ordinal)
    {
        Authorization = authorization;
        StabilityEvidence = stabilityEvidence;
        HistorySnapshot = historySnapshot;
        Ordinal = ordinal;
    }

    public FileDeleteOperationUserAuthorizationReceipt Authorization { get; }

    public FileDeleteOperationStabilityLeaseEvidence StabilityEvidence { get; }

    public FileDeleteOperationActionHistory HistorySnapshot { get; }

    public int Ordinal { get; }

    public FileDeleteOperationUserAuthorizationItem AuthorizedItem => Authorization.Items[Ordinal];

    public FileDeleteOperationActionEntry HistoryEntry => HistorySnapshot.Entries[Ordinal];

    public bool DeleteMutationAuthorized => false;

    public bool MutationBarrierSatisfied => false;

    public bool StabilityLeaseAcquisitionProven => false;

    public bool StabilityLeaseLivenessProven => false;

    public bool IsBoundTo(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        FileDeleteOperationStabilityLeaseEvidence stabilityEvidence,
        FileDeleteOperationActionHistory historySnapshot,
        int ordinal) =>
        ReferenceEquals(Authorization, authorization) &&
        ReferenceEquals(StabilityEvidence, stabilityEvidence) &&
        ReferenceEquals(HistorySnapshot, historySnapshot) &&
        Ordinal == ordinal;
}

public static class FileDeleteOperationHistoryBinding
{
    public static FileDeleteOperationHistoryBindingEvidence Validate(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        FileDeleteOperationStabilityLeaseEvidence stabilityEvidence,
        FileDeleteOperationActionHistory historySnapshot,
        int ordinal)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(stabilityEvidence);
        ArgumentNullException.ThrowIfNull(historySnapshot);

        if (!authorization.UserAuthorizedAttempt || authorization.DeleteMutationAuthorized ||
            stabilityEvidence.DeleteMutationAuthorized || historySnapshot.DeleteMutationAuthorized)
        {
            throw new ArgumentException(
                "Delete history binding requires non-mutating authorization, stability, and history evidence.");
        }
        if (ordinal < 0 || ordinal >= authorization.Items.Count || ordinal >= historySnapshot.Entries.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }
        if (!stabilityEvidence.IsBoundTo(authorization, ordinal))
        {
            throw new ArgumentException(
                "Delete history binding requires stability evidence from the exact authorization receipt and ordinal.",
                nameof(stabilityEvidence));
        }
        if (historySnapshot.OperationId != authorization.PlanId ||
            historySnapshot.AuthorizationId != authorization.AuthorizationId)
        {
            throw new ArgumentException(
                "Delete history operation/authorization provenance does not match the exact user authorization.",
                nameof(historySnapshot));
        }
        if (historySnapshot.IsTerminal)
        {
            throw new ArgumentException(
                "Terminal delete history cannot be prepared for a new mutation barrier.",
                nameof(historySnapshot));
        }
        if (authorization.Items.Count != authorization.Plan.Intent.Entries.Count ||
            historySnapshot.Entries.Count != authorization.Items.Count)
        {
            throw new ArgumentException(
                "Delete history binding requires complete ordered authorization and history evidence.");
        }
        if (!string.Equals(
                historySnapshot.SourcePaneId,
                authorization.Plan.Intent.SourcePane,
                StringComparison.Ordinal) ||
            historySnapshot.SourceTabId != authorization.Plan.Intent.SourceTabId)
        {
            throw new ArgumentException(
                "Delete history source-pane/tab provenance does not match the authorized plan.",
                nameof(historySnapshot));
        }
        if (historySnapshot.QueuedAtUtc != authorization.Plan.QueuedAtUtc.ToUniversalTime() ||
            historySnapshot.ValidatedAtUtc != authorization.ValidatedAtUtc.ToUniversalTime() ||
            historySnapshot.AuthorizedAtUtc != authorization.AuthorizedAtUtc.ToUniversalTime())
        {
            throw new ArgumentException(
                "Delete history timing provenance does not match the exact authorization evidence.",
                nameof(historySnapshot));
        }
        if (!PathEquals(
                historySnapshot.CanonicalSourceDirectoryPath,
                authorization.CanonicalSourceDirectoryPath) ||
            !PathEquals(
                stabilityEvidence.CanonicalSourceDirectoryPath,
                authorization.CanonicalSourceDirectoryPath) ||
            historySnapshot.SourceDirectoryIdentity != authorization.SourceDirectoryIdentity ||
            stabilityEvidence.SourceDirectoryIdentity != authorization.SourceDirectoryIdentity)
        {
            throw new ArgumentException(
                "Delete history/stability root evidence does not match the authorized canonical root identity.");
        }

        var authorizedItem = authorization.Items[ordinal];
        var plannedEntry = authorization.Plan.Intent.Entries[ordinal];
        var historyEntry = historySnapshot.Entries[ordinal];
        if (authorizedItem.Entry != plannedEntry || historyEntry.Entry != authorizedItem.Entry ||
            historyEntry.Ordinal != ordinal)
        {
            throw new ArgumentException(
                "Delete history binding requires the exact authorized file entry in captured plan order.");
        }
        if (historyEntry.State != FileDeleteOperationActionEntryState.Pending)
        {
            throw new ArgumentException(
                "Delete history binding requires an entry that is still Pending before the durable mutation barrier.",
                nameof(historySnapshot));
        }
        if (!PathEquals(historyEntry.CanonicalSourcePath, authorizedItem.CanonicalPath) ||
            !PathEquals(stabilityEvidence.CanonicalSourcePath, authorizedItem.CanonicalPath) ||
            historyEntry.SourceIdentity != authorizedItem.Identity ||
            stabilityEvidence.SourceIdentity != authorizedItem.Identity)
        {
            throw new ArgumentException(
                "Delete history/stability file evidence does not match the exact authorized canonical file identity.");
        }

        return new FileDeleteOperationHistoryBindingEvidence(
            authorization,
            stabilityEvidence,
            historySnapshot,
            ordinal);
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
