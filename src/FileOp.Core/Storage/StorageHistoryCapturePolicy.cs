namespace FileOp.Core.Storage;

public static class StorageHistoryCapturePolicy
{
    public static DateTimeOffset GetHourlyBucket(DateTimeOffset timestamp)
    {
        var utc = timestamp.ToUniversalTime();
        return new DateTimeOffset(
            utc.Year,
            utc.Month,
            utc.Day,
            utc.Hour,
            0,
            0,
            TimeSpan.Zero);
    }
}