using FileOp.Core.Storage;

namespace FileOp.App;

public sealed partial class StorageOptimizationView
{
    public void SetKnownLocationCleanupReadinessLoading(string candidateName) =>
        KnownLocationReview.SetCleanupReadinessLoading(candidateName);

    public void ApplyKnownLocationCleanupReadiness(StorageCleanupReadinessPreview preview) =>
        KnownLocationReview.ApplyCleanupReadiness(preview);

    public void SetKnownLocationCleanupReadinessUnavailable(string message) =>
        KnownLocationReview.SetCleanupReadinessUnavailable(message);
}
