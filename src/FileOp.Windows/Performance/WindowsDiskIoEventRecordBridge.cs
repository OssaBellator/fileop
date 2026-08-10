namespace FileOp.Windows.Performance;

internal sealed record WindowsDiskIoDecodedEvent(
    long EventTimestamp,
    ushort ProcessorIndex,
    ushort LoggerId,
    WindowsDiskIoCompletion Completion,
    TimeSpan ResponseTime);

internal static class WindowsDiskIoEventRecordBridge
{
    internal const ushort EventHeaderFlag32Bit = 0x0020;
    internal const ushort EventHeaderFlag64Bit = 0x0040;
    internal const ushort EventHeaderFlagClassic = 0x0100;

    public static bool TryDecodeCompletion(
        WindowsEtwEventRecordSnapshot record,
        long performanceCounterFrequency,
        out WindowsDiskIoDecodedEvent? decoded)
    {
        ArgumentNullException.ThrowIfNull(record);
        decoded = null;

        var eventType = record.Descriptor.Opcode;
        if (record.ProviderId != WindowsDiskIoEventDecoder.DiskIoProviderId ||
            eventType is not (
                WindowsDiskIoEventDecoder.ReadEventType or
                WindowsDiskIoEventDecoder.WriteEventType or
                WindowsDiskIoEventDecoder.FlushEventType))
        {
            return false;
        }

        if ((record.Flags & EventHeaderFlagClassic) == 0)
        {
            throw new InvalidDataException(
                "DiskIo ETW completion record is missing EVENT_HEADER_FLAG_CLASSIC_HEADER; FileOp will not interpret a non-classic event with the classic DiskIo payload layout.");
        }

        var metadata = WindowsDiskIoTraceMetadata.FromEventHeaderFlags(
            has32BitHeaderFlag: (record.Flags & EventHeaderFlag32Bit) != 0,
            has64BitHeaderFlag: (record.Flags & EventHeaderFlag64Bit) != 0,
            performanceCounterFrequency);

        if (!WindowsDiskIoEventDecoder.TryDecodeCompletion(
            record.ProviderId,
            eventType,
            metadata.PointerSize,
            record.UserData,
            out var completion) || completion is null)
        {
            throw new InvalidDataException(
                "A record classified as a DiskIo completion could not be decoded by FileOp's DiskIo payload decoder.");
        }

        decoded = new WindowsDiskIoDecodedEvent(
            EventTimestamp: record.Timestamp,
            ProcessorIndex: record.ProcessorIndex,
            LoggerId: record.LoggerId,
            Completion: completion,
            ResponseTime: metadata.ConvertHighResolutionResponseTime(
                completion.HighResolutionResponseTicks));
        return true;
    }
}
