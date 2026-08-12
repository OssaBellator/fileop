using System;

namespace FileOp.Core.Operations;

/// <summary>
/// Point-in-time evidence that one durable Pending delete-history entry still agrees with the
/// exact in-memory user-authorization receipt and the exact read-only stability evidence.
/// This object is evidence only: it is not a reusable authorization capability and does not
/// replace the durable Pending -> MutationStarted concurrency barrier.
/// </summary>
public sealed class FileDeleteOperationExecutionEvidenceBinding
{
    internal FileDeleteOperationExecutionEvidenceBinding(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        FileDeleteOperationStabilityLeaseEvidence stabilityEvidence,
        FileDeleteOperationActionHistory history,
        FileDeleteOperationActionEntry historyEntry,
        int ordinal)
    {
        Authorization = authorization;
        StabilityEvidence = stabilityEvidence;
        History = history;
        HistoryEntry = historyEntry;
        Ordinal = ordinal;
    }

    public FileDeleteOperationUserAuthorizationReceipt Authorization { get; }

    public FileDeleteOperationStabilityLeaseEvidence StabilityEvidence { get; }

    public FileDeleteOperationActionHistory History { get; }

    public FileDeleteOperationActionEntry HistoryEntry { get; }

    public int Ordinal { get; }

    public Guid AuthorizationId => Authorization.AuthorizationId;

    public Guid PlanId => Authorization.PlanId;

    public bool DeleteMutationAuthorized => false;

    public bool IsBoundTo(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        FileDeleteOperationStabilityLeaseEvidence stabilityEvidence,
        FileDeleteOperationActionHistory history,
        int ordinal) =>
        ReferenceEquals(Authorization, authorization) &&
        ReferenceEquals(StabilityEvidence, stabilityEvidence) &&
        ReferenceEquals(History, history) &&
        Ordinal == ordinal;
}

public sealed class FileDeleteOperationExecutionEvidenceBinder
{
    public FileDeleteOperationExecutionEvidenceBinding Bind(
        FileDeleteOperationUserAuthorizationReceipt authorization,
        FileDeleteOperationStabilityLeaseEvidence stabilityEvidence,
        FileDeleteOperationActionHistory history,
        int ordinal)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(stabilityEvidence);
        ArgumentNullException.ThrowIfNull(history);

        if (!authorization.UserAuthorizedAttempt ||
            authorization.DeleteMutationAuthorized ||
            stabilityEvidence.DeleteMutationAuthorized ||
            history.DeleteMutationAuthorized)
        {
            throw new InvalidOperationException(
                "Delete execution evidence binding requires non-mutating consent, stability, and history evidence.");
        }

        if (ordinal < 0 ||
            ordinal >= authorization.Items.Count ||
            ordinal >= authorization.Plan.Intent.Entries.Count ||
            ordinal >= history.Entries.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        if (authorization.Items.Count != authorization.Plan.Intent.Entries.Count ||
            history.Entries.Count != authorization.Items.Count)
        {
            throw new InvalidOperationException(
                "Delete execution evidence binding requires the durable history and authorization to preserve the same complete ordered entry set.");
        }

        if (!stabilityEvidence.IsBoundTo(authorization, ordinal) ||
            !ReferenceEquals(stabilityEvidence.Authorization, authorization))
        {
            throw new InvalidOperationException(
                "Delete stability evidence is not bound to the exact user-authorization receipt and ordinal.");
        }

        if (history.OperationId != authorization.PlanId ||
            history.AuthorizationId != authorization.AuthorizationId)
        {
            throw new InvalidOperationException(
                "Delete action history does not belong to the exact authorized plan and consent attempt.");
        }

        if (history.IsTerminal)
        {
            throw new InvalidOperationException(
                "Terminal delete action history cannot be rebound for a new mutation attempt.");
        }

        var authorizedItem = authorization.Items[ordinal];
        var plannedEntry = authorization.Plan.Intent.Entries[ordinal];
        var historyEntry = history.Entries[ordinal];

        if (authorizedItem.Entry != plannedEntry ||
            historyEntry.Ordinal != ordinal ||
            historyEntry.Entry != plannedEntry ||
            historyEntry.State != FileDeleteOperationActionEntryState.Pending)
        {
            throw new InvalidOperationException(
                "Delete execution evidence binding requires the exact ordered authorized entry to remain Pending in durable history.");
        }

        if (!PathsEqual(history.CanonicalSourceDirectoryPath, authorization.CanonicalSourceDirectoryPath) ||
            history.SourceDirectoryIdentity != authorization.SourceDirectoryIdentity ||
            !PathsEqual(stabilityEvidence.CanonicalSourceDirectoryPath, authorization.CanonicalSourceDirectoryPath) ||
            stabilityEvidence.SourceDirectoryIdentity != authorization.SourceDirectoryIdentity)
        {
            throw new InvalidOperationException(
                "Delete execution evidence binding requires exact authorized source-directory path and identity evidence.");
        }

        if (!PathsEqual(historyEntry.CanonicalSourcePath, authorizedItem.CanonicalPath) ||
            historyEntry.SourceIdentity != authorizedItem.Identity ||
            !PathsEqual(stabilityEvidence.CanonicalSourcePath, authorizedItem.CanonicalPath) ||
            stabilityEvidence.SourceIdentity != authorizedItem.Identity ||
            stabilityEvidence.Entry != plannedEntry)
        {
            throw new InvalidOperationException(
                "Delete execution evidence binding requires exact authorized source-file path and identity evidence.");
        }

        return new FileDeleteOperationExecutionEvidenceBinding(
            authorization,
            stabilityEvidence,
            history,
            historyEntry,
            ordinal);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
