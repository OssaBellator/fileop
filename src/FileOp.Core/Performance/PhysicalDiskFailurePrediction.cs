namespace FileOp.Core.Performance;

public enum PhysicalDiskFailurePredictionStatus
{
    Available,
    Unsupported,
    Unavailable,
}

public sealed record PhysicalDiskFailurePredictionEvidence
{
    public PhysicalDiskFailurePredictionEvidence(
        int physicalDiskNumber,
        uint windowsPredictFailureValue)
    {
        if (physicalDiskNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalDiskNumber));
        }

        PhysicalDiskNumber = physicalDiskNumber;
        WindowsPredictFailureValue = windowsPredictFailureValue;
    }

    public int PhysicalDiskNumber { get; }

    // Windows defines zero as no current prediction and any nonzero value as a
    // current failure prediction. FileOp preserves the raw ULONG and derives
    // only that documented boolean interpretation.
    public uint WindowsPredictFailureValue { get; }
    public bool FailurePredicted => WindowsPredictFailureValue != 0;
}

public sealed record PhysicalDiskFailurePredictionResult
{
    public PhysicalDiskFailurePredictionResult(
        int physicalDiskNumber,
        PhysicalDiskFailurePredictionStatus status,
        PhysicalDiskFailurePredictionEvidence? evidence,
        TimeSpan elapsed,
        string detail)
    {
        if (physicalDiskNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalDiskNumber));
        }
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        if ((status == PhysicalDiskFailurePredictionStatus.Available) !=
            (evidence is not null))
        {
            throw new ArgumentException(
                "Available physical-disk failure-prediction results require evidence; unavailable results cannot carry it.",
                nameof(evidence));
        }
        if (evidence is not null && evidence.PhysicalDiskNumber != physicalDiskNumber)
        {
            throw new ArgumentException(
                "Physical-disk result number must match its failure-prediction evidence.",
                nameof(evidence));
        }
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);

        PhysicalDiskNumber = physicalDiskNumber;
        Status = status;
        Evidence = evidence;
        Elapsed = elapsed;
        Detail = detail;
    }

    public int PhysicalDiskNumber { get; }
    public PhysicalDiskFailurePredictionStatus Status { get; }
    public PhysicalDiskFailurePredictionEvidence? Evidence { get; }
    public TimeSpan Elapsed { get; }
    public string Detail { get; }

    public static PhysicalDiskFailurePredictionResult Available(
        PhysicalDiskFailurePredictionEvidence evidence,
        TimeSpan elapsed,
        string detail)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return new PhysicalDiskFailurePredictionResult(
            evidence.PhysicalDiskNumber,
            PhysicalDiskFailurePredictionStatus.Available,
            evidence,
            elapsed,
            detail);
    }

    public static PhysicalDiskFailurePredictionResult Unavailable(
        int physicalDiskNumber,
        PhysicalDiskFailurePredictionStatus status,
        TimeSpan elapsed,
        string detail)
    {
        if (status == PhysicalDiskFailurePredictionStatus.Available)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new PhysicalDiskFailurePredictionResult(
            physicalDiskNumber,
            status,
            null,
            elapsed,
            detail);
    }
}

public interface IPhysicalDiskFailurePredictionProvider
{
    PhysicalDiskFailurePredictionResult Query(int physicalDiskNumber);
}
