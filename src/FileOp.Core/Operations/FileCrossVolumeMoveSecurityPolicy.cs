namespace FileOp.Core.Operations;

/// <summary>
/// Security-descriptor behavior intentionally promised by FileOp for a cross-volume
/// regular-file Move. Windows does not preserve a file's original security descriptor
/// when MoveFile crosses volumes; the newly created destination receives destination-side
/// default/inherited security instead. FileOp follows that platform contract rather than
/// requiring privileged SACL reads or pretending Copy preserves the source descriptor.
/// </summary>
public enum FileCrossVolumeMoveSecurityDisposition
{
    DestinationDefaultInherited = 0,
}

/// <summary>
/// Central product contract for cross-volume Move security semantics.
/// Security-descriptor equivalence is intentionally not part of destructive fidelity:
/// the source descriptor is not a preserved semantic under the selected Windows-native
/// cross-volume Move contract. Other unsupported filesystem semantics remain fail-closed.
/// </summary>
public static class FileCrossVolumeMoveSecurityPolicy
{
    public const FileCrossVolumeMoveSecurityDisposition CurrentDisposition =
        FileCrossVolumeMoveSecurityDisposition.DestinationDefaultInherited;

    public const bool PreservesSourceSecurityDescriptor = false;

    public const string Summary =
        "Cross-volume Move follows Windows destination-default security semantics: the new destination inherits/defaults security from its destination context rather than preserving the source security descriptor.";
}
