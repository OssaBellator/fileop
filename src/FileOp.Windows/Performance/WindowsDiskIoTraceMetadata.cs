namespace FileOp.Windows.Performance;

public sealed record WindowsDiskIoTraceMetadata
{
    public WindowsDiskIoTraceMetadata(
        int pointerSize,
        long performanceCounterFrequency)
    {
        if (pointerSize is not (4 or 8))
        {
            throw new ArgumentOutOfRangeException(
                nameof(pointerSize),
                pointerSize,
                "DiskIo ETW pointer size must be 4 or 8 bytes.");
        }

        if (performanceCounterFrequency <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(performanceCounterFrequency),
                performanceCounterFrequency,
                "DiskIo trace performance-counter frequency must be positive.");
        }

        PointerSize = pointerSize;
        PerformanceCounterFrequency = performanceCounterFrequency;
    }

    public int PointerSize { get; }

    public long PerformanceCounterFrequency { get; }

    public static WindowsDiskIoTraceMetadata FromEventHeaderFlags(
        bool has32BitHeaderFlag,
        bool has64BitHeaderFlag,
        long performanceCounterFrequency)
    {
        if (has32BitHeaderFlag == has64BitHeaderFlag)
        {
            throw new InvalidDataException(
                has32BitHeaderFlag
                    ? "DiskIo ETW event header declares both 32-bit and 64-bit pointer-width flags."
                    : "DiskIo ETW event header declares neither 32-bit nor 64-bit pointer-width flag; FileOp does not guess from its own process architecture.");
        }

        return new WindowsDiskIoTraceMetadata(
            has32BitHeaderFlag ? 4 : 8,
            performanceCounterFrequency);
    }

    public TimeSpan ConvertHighResolutionResponseTime(ulong responseTicks)
    {
        if (responseTicks == 0)
        {
            return TimeSpan.Zero;
        }

        var timeSpanTicks = decimal.Round(
            responseTicks * (decimal)TimeSpan.TicksPerSecond / PerformanceCounterFrequency,
            decimals: 0,
            MidpointRounding.AwayFromZero);
        if (timeSpanTicks > TimeSpan.MaxValue.Ticks)
        {
            throw new InvalidDataException(
                $"DiskIo high-resolution response value {responseTicks} exceeds the representable TimeSpan range at frequency {PerformanceCounterFrequency} Hz.");
        }

        return TimeSpan.FromTicks((long)timeSpanTicks);
    }
}
