using FileOp.Core.Performance;

namespace FileOp.Windows.Performance;

internal static class WindowsDiskIoTimingProvenanceValidator
{
    public static void Validate(
        IReadOnlyList<DiskIoEventObservation> observations,
        IReadOnlyList<DiskIoResponseTimingObservation> responseTimings)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(responseTimings);
        if (observations.Count != responseTimings.Count)
        {
            throw new InvalidDataException(
                $"DiskIo response-timing evidence count {responseTimings.Count:N0} does not match the {observations.Count:N0} accepted normalized completions.");
        }

        for (var index = 0; index < observations.Count; index++)
        {
            var observation = observations[index];
            var timing = responseTimings[index];
            if (timing.Timestamp != observation.Timestamp ||
                timing.PhysicalDiskNumber != observation.PhysicalDiskNumber ||
                timing.Operation != observation.Operation ||
                timing.Owner != observation.Owner)
            {
                throw new InvalidDataException(
                    $"DiskIo response-timing evidence at accepted completion index {index:N0} does not match its timestamp/disk/operation/process provenance.");
            }
        }
    }
}
