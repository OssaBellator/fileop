using System.Runtime.InteropServices;

namespace FileOp.Windows.Performance;

internal readonly record struct WindowsEtwEventDescriptorSnapshot(
    ushort Id,
    byte Version,
    byte Channel,
    byte Level,
    byte Opcode,
    ushort Task,
    ulong Keyword);

internal sealed class WindowsEtwEventRecordSnapshot
{
    internal const int EventHeaderSizeBytes = 80;
    internal const int EventRecordFixedSize32 = 104;
    internal const int EventRecordFixedSize64 = 112;
    internal const int BufferContextOffset = 80;
    internal const int ExtendedDataCountOffset = 84;
    internal const int UserDataLengthOffset = 86;
    internal const int ExtendedDataPointerOffset = 88;
    internal const int UserDataPointerOffset32 = 92;
    internal const int UserDataPointerOffset64 = 96;
    internal const int UserContextPointerOffset32 = 96;
    internal const int UserContextPointerOffset64 = 104;
    internal const int DefaultMaximumUserDataBytes = ushort.MaxValue;

    private readonly byte[] _userData;

    private WindowsEtwEventRecordSnapshot(
        ushort eventRecordSize,
        ushort headerType,
        ushort flags,
        ushort eventProperty,
        uint threadId,
        uint processId,
        long timestamp,
        Guid providerId,
        WindowsEtwEventDescriptorSnapshot descriptor,
        ulong processorTime,
        Guid activityId,
        ushort processorIndex,
        ushort loggerId,
        ushort extendedDataCount,
        byte[] userData)
    {
        EventRecordSize = eventRecordSize;
        HeaderType = headerType;
        Flags = flags;
        EventProperty = eventProperty;
        ThreadId = threadId;
        ProcessId = processId;
        Timestamp = timestamp;
        ProviderId = providerId;
        Descriptor = descriptor;
        ProcessorTime = processorTime;
        ActivityId = activityId;
        ProcessorIndex = processorIndex;
        LoggerId = loggerId;
        ExtendedDataCount = extendedDataCount;
        _userData = userData;
    }

    public ushort EventRecordSize { get; }

    public ushort HeaderType { get; }

    public ushort Flags { get; }

    public ushort EventProperty { get; }

    public uint ThreadId { get; }

    public uint ProcessId { get; }

    public long Timestamp { get; }

    public Guid ProviderId { get; }

    public WindowsEtwEventDescriptorSnapshot Descriptor { get; }

    public ulong ProcessorTime { get; }

    public Guid ActivityId { get; }

    public ushort ProcessorIndex { get; }

    public ushort LoggerId { get; }

    public ushort ExtendedDataCount { get; }

    public int UserDataLength => _userData.Length;

    public ReadOnlySpan<byte> UserData => _userData;

    public static WindowsEtwEventRecordSnapshot CopyFrom(
        IntPtr eventRecord,
        int maximumUserDataBytes = DefaultMaximumUserDataBytes)
    {
        if (eventRecord == IntPtr.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(eventRecord));
        }
        if (maximumUserDataBytes is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumUserDataBytes),
                maximumUserDataBytes,
                $"ETW user-data copy limit must be between 0 and {ushort.MaxValue} bytes.");
        }

        var eventRecordSize = ReadUInt16(eventRecord, 0);
        if (eventRecordSize < EventHeaderSizeBytes)
        {
            throw new InvalidDataException(
                $"ETW EVENT_HEADER.Size was {eventRecordSize} bytes; expected at least {EventHeaderSizeBytes}.");
        }

        var userDataLength = ReadUInt16(eventRecord, UserDataLengthOffset);
        if (userDataLength > maximumUserDataBytes)
        {
            throw new InvalidDataException(
                $"ETW user data was {userDataLength} bytes, exceeding FileOp's {maximumUserDataBytes}-byte copy limit.");
        }
        if (eventRecordSize < EventHeaderSizeBytes + userDataLength)
        {
            throw new InvalidDataException(
                $"ETW event size {eventRecordSize} cannot contain its {userDataLength}-byte user payload.");
        }

        var userDataPointerOffset = IntPtr.Size == 8
            ? UserDataPointerOffset64
            : UserDataPointerOffset32;
        var userDataPointer = Marshal.ReadIntPtr(eventRecord, userDataPointerOffset);
        byte[] copiedUserData;
        if (userDataLength == 0)
        {
            copiedUserData = Array.Empty<byte>();
        }
        else
        {
            if (userDataPointer == IntPtr.Zero)
            {
                throw new InvalidDataException(
                    "ETW EVENT_RECORD declared user data but supplied a null UserData pointer.");
            }

            copiedUserData = new byte[userDataLength];
            Marshal.Copy(userDataPointer, copiedUserData, 0, copiedUserData.Length);
        }

        var descriptor = new WindowsEtwEventDescriptorSnapshot(
            Id: ReadUInt16(eventRecord, 40),
            Version: Marshal.ReadByte(eventRecord, 42),
            Channel: Marshal.ReadByte(eventRecord, 43),
            Level: Marshal.ReadByte(eventRecord, 44),
            Opcode: Marshal.ReadByte(eventRecord, 45),
            Task: ReadUInt16(eventRecord, 46),
            Keyword: ReadUInt64(eventRecord, 48));

        return new WindowsEtwEventRecordSnapshot(
            eventRecordSize: eventRecordSize,
            headerType: ReadUInt16(eventRecord, 2),
            flags: ReadUInt16(eventRecord, 4),
            eventProperty: ReadUInt16(eventRecord, 6),
            threadId: ReadUInt32(eventRecord, 8),
            processId: ReadUInt32(eventRecord, 12),
            timestamp: Marshal.ReadInt64(eventRecord, 16),
            providerId: ReadGuid(eventRecord, 24),
            descriptor: descriptor,
            processorTime: ReadUInt64(eventRecord, 56),
            activityId: ReadGuid(eventRecord, 64),
            processorIndex: ReadUInt16(eventRecord, BufferContextOffset),
            loggerId: ReadUInt16(eventRecord, BufferContextOffset + sizeof(ushort)),
            extendedDataCount: ReadUInt16(eventRecord, ExtendedDataCountOffset),
            userData: copiedUserData);
    }

    private static ushort ReadUInt16(IntPtr pointer, int offset) =>
        unchecked((ushort)Marshal.ReadInt16(pointer, offset));

    private static uint ReadUInt32(IntPtr pointer, int offset) =>
        unchecked((uint)Marshal.ReadInt32(pointer, offset));

    private static ulong ReadUInt64(IntPtr pointer, int offset) =>
        unchecked((ulong)Marshal.ReadInt64(pointer, offset));

    private static Guid ReadGuid(IntPtr pointer, int offset)
    {
        var bytes = new byte[16];
        Marshal.Copy(IntPtr.Add(pointer, offset), bytes, 0, bytes.Length);
        return new Guid(bytes);
    }
}
