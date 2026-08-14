using System;

namespace FileOp.Core.Operations;

public enum FileDeleteDisposition
{
    PermanentReviewed,
    RecycleBin,
}

public enum FileDeleteDispositionSupport
{
    SupportedByReviewedPermanentBoundary,
    RequiresSeparateIdentitySafeRecycleContract,
}

public sealed record FileDeleteDispositionPolicyResult(
    FileDeleteDisposition Disposition,
    FileDeleteDispositionSupport Support,
    string Summary)
{
    public bool CanUseExistingPermanentDeleteAuthorization =>
        Support == FileDeleteDispositionSupport.SupportedByReviewedPermanentBoundary;
}

/// <summary>
/// Prevents a future Recycle Bin UI from reusing the permanent-delete authorization
/// receipt or treating a path-only shell operation as equivalent to identity-bound delete.
/// </summary>
public static class FileDeleteDispositionPolicy
{
    public static FileDeleteDispositionPolicyResult Classify(FileDeleteDisposition disposition) =>
        disposition switch
        {
            FileDeleteDisposition.PermanentReviewed => new(
                disposition,
                FileDeleteDispositionSupport.SupportedByReviewedPermanentBoundary,
                "Permanent regular-file deletion may use the existing reviewed recovery, confirmation, authorization, durable-history and same-handle mutation boundary."),
            FileDeleteDisposition.RecycleBin => new(
                disposition,
                FileDeleteDispositionSupport.RequiresSeparateIdentitySafeRecycleContract,
                "Recycle Bin remains unavailable until FileOp can bind the shell/recycle operation to the exact reviewed filesystem object and define restore/recovery semantics. A path-only shell delete is not equivalent to the permanent-delete capability."),
            _ => throw new ArgumentOutOfRangeException(nameof(disposition)),
        };
}
