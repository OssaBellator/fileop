using System.Buffers.Binary;
using FileOp.Core.Performance;
using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsDiskIoTraceMetadataTests
{
    [TestMethod]
    public void HeaderFlagsResolveExactPointerWidth()
    {
        var x86 = WindowsDiskIoTraceMetadata.FromEventHeaderFlags(
            has32BitHeaderFlag: true,
            has64BitHeaderFlag: false,
            performanceCounterFrequency: 10_000_000);
        var x64 = WindowsDiskIoTraceMetadata.FromEventHeaderFlags(
            has32BitHeaderFlag: false,
            has64BitHeaderFlag: true,
            performanceCounterFrequency: 10_000_000);

        Assert.AreEqual(4, x86.PointerSize);
        Assert.AreEqual(8, x64.PointerSize);
    }

    [TestMethod]
    public void AmbiguousOrMissingPointerFlagsFailClosed()
    {
        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsDiskIoTraceMetadata.FromEventHeaderFlags(
                has32BitHeaderFlag: false,
                has64BitHeaderFlag: false,
                performanceCounterFrequency: 10_000_000));
        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsDiskIoTraceMetadata.FromEventHeaderFlags(
                has32BitHeaderFlag: true,
                has64BitHeaderFlag: true,
                performanceCounterFrequency: 10_000_000));
    }

    [TestMethod]
    public void InvalidPointerSizeOrFrequencyFailsClosed()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new WindowsDiskIoTraceMetadata(0, 10_000_000));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new WindowsDiskIoTraceMetadata(16, 10_000_000));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new WindowsDiskIoTraceMetadata(8, 0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new WindowsDiskIoTraceMetadata(8, -1));
    }

    [TestMethod]
    public void TenMegahertzFrequencyMapsDirectlyToTimeSpanTicks()
    {
        var metadata = new WindowsDiskIoTraceMetadata(8, TimeSpan.TicksPerSecond);

        Assert.AreEqual(TimeSpan.Zero, metadata.ConvertHighResolutionResponseTime(0));
        Assert.AreEqual(TimeSpan.FromTicks(1), metadata.ConvertHighResolutionResponseTime(1));
        Assert.AreEqual(TimeSpan.FromTicks(12_345_678), metadata.ConvertHighResolutionResponseTime(12_345_678));
    }

    [TestMethod]
    public void FractionalTimeSpanTicksUseDeterministicNearestRounding()
    {
        var thirds = new WindowsDiskIoTraceMetadata(8, 3);
        var halfTick = new WindowsDiskIoTraceMetadata(8, 20_000_000);

        Assert.AreEqual(
            TimeSpan.FromTicks(3_333_333),
            thirds.ConvertHighResolutionResponseTime(1));
        Assert.AreEqual(
            TimeSpan.FromTicks(6_666_667),
            thirds.ConvertHighResolutionResponseTime(2));
        Assert.AreEqual(
            TimeSpan.FromTicks(1),
            halfTick.ConvertHighResolutionResponseTime(1));
    }

    [TestMethod]
    public void ResponseConversionFailsClosedOnTimeSpanOverflow()
    {
        var metadata = new WindowsDiskIoTraceMetadata(8, 1);

        Assert.ThrowsException<InvalidDataException>(() =>
            metadata.ConvertHighResolutionResponseTime(ulong.MaxValue));
    }

    [TestMethod]
    public void MetadataPointerWidthFeedsDiskIoDecoderWithoutArchitectureGuess()
    {
        var metadata = WindowsDiskIoTraceMetadata.FromEventHeaderFlags(
            has32BitHeaderFlag: true,
            has64BitHeaderFlag: false,
            performanceCounterFrequency: 10_000_000);
        var payload = CreateReadPayload32(
            diskNumber: 4,
            transferSize: 8_192,
            responseTicks: 25_000,
            issuingThreadId: 555);

        Assert.IsTrue(WindowsDiskIoEventDecoder.TryDecodeCompletion(
            WindowsDiskIoEventDecoder.DiskIoProviderId,
            WindowsDiskIoEventDecoder.ReadEventType,
            metadata.PointerSize,
            payload,
            out var completion));
        Assert.IsNotNull(completion);
        Assert.AreEqual(4u, completion.PhysicalDiskNumber);
        Assert.AreEqual(8_192L, completion.TransferBytes);
        Assert.AreEqual(555u, completion.IssuingThreadId);
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(2.5),
            metadata.ConvertHighResolutionResponseTime(completion.HighResolutionResponseTicks));
    }

    private static byte[] CreateReadPayload32(
        uint diskNumber,
        uint transferSize,
        ulong responseTicks,
        uint issuingThreadId)
    {
        const int pointerSize = 4;
        const int highResolutionOffset = 24 + (2 * pointerSize);
        const int issuingThreadOffset = highResolutionOffset + sizeof(ulong);
        var payload = new byte[issuingThreadOffset + sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), diskNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), transferSize);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(16, 8), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(highResolutionOffset, 8), responseTicks);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(issuingThreadOffset, 4), issuingThreadId);
        return payload;
    }
}
