using System.Buffers.Binary;
using FileOp.Core.Performance;
using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class NvmeHealthEvidenceTests
{
    [TestMethod]
    public void QueryBufferMatchesDocumentedProtocolSpecificHealthRequest()
    {
        var buffer = WindowsNvmeHealthQueryCodec.BuildHealthQueryBuffer();

        Assert.AreEqual(560, buffer.Length);
        Assert.AreEqual(50u, ReadUInt32(buffer, 0));
        Assert.AreEqual(0u, ReadUInt32(buffer, 4));
        Assert.AreEqual(3u, ReadUInt32(buffer, 8));
        Assert.AreEqual(2u, ReadUInt32(buffer, 12));
        Assert.AreEqual(2u, ReadUInt32(buffer, 16));
        Assert.AreEqual(0u, ReadUInt32(buffer, 20));
        Assert.AreEqual(40u, ReadUInt32(buffer, 24));
        Assert.AreEqual(512u, ReadUInt32(buffer, 28));
        Assert.AreEqual(0u, ReadUInt32(buffer, 32));
        Assert.AreEqual(0u, ReadUInt32(buffer, 36));
        Assert.AreEqual(0u, ReadUInt32(buffer, 40));
        Assert.AreEqual(0u, ReadUInt32(buffer, 44));
    }

    [TestMethod]
    public void HealthLogParsesStandardizedWarningsAndLifetimeCounters()
    {
        var log = BuildHealthLog(
            criticalWarning: 0x1D,
            temperatureKelvin: 300,
            spare: 90,
            spareThreshold: 10,
            percentageUsed: 42,
            powerCycles: 5,
            powerOnHours: 1234,
            unsafeShutdowns: 7,
            mediaErrors: 9,
            errorEntries: 11,
            warningTemperatureMinutes: 13,
            criticalTemperatureMinutes: 17);

        var evidence = WindowsNvmeHealthQueryCodec.ParseHealthLog(4, log);

        Assert.AreEqual(4, evidence.PhysicalDiskNumber);
        Assert.AreEqual((byte)0x1D, evidence.CriticalWarnings.RawValue);
        Assert.IsTrue(evidence.CriticalWarnings.AvailableSpareBelowThreshold);
        Assert.IsFalse(evidence.CriticalWarnings.TemperatureThreshold);
        Assert.IsTrue(evidence.CriticalWarnings.ReliabilityDegraded);
        Assert.IsTrue(evidence.CriticalWarnings.MediaReadOnly);
        Assert.IsTrue(evidence.CriticalWarnings.VolatileMemoryBackupFailed);
        Assert.AreEqual((ushort)300, evidence.CompositeTemperatureKelvin);
        Assert.AreEqual((byte)90, evidence.AvailableSparePercent);
        Assert.AreEqual((byte)10, evidence.AvailableSpareThresholdPercent);
        Assert.AreEqual((byte)42, evidence.PercentageUsedEstimate);
        Assert.AreEqual((UInt128)5, evidence.PowerCycles);
        Assert.AreEqual((UInt128)1234, evidence.PowerOnHours);
        Assert.AreEqual((UInt128)7, evidence.UnsafeShutdowns);
        Assert.AreEqual((UInt128)9, evidence.MediaErrors);
        Assert.AreEqual((UInt128)11, evidence.ErrorInfoLogEntryCount);
        Assert.AreEqual(13u, evidence.WarningCompositeTemperatureMinutes);
        Assert.AreEqual(17u, evidence.CriticalCompositeTemperatureMinutes);
    }

    [TestMethod]
    public void PercentageUsed255AndReservedWarningBitsRemainRawEvidence()
    {
        var log = BuildHealthLog(criticalWarning: 0xE0, percentageUsed: 255);

        var evidence = WindowsNvmeHealthQueryCodec.ParseHealthLog(0, log);

        Assert.AreEqual((byte)255, evidence.PercentageUsedEstimate);
        Assert.IsFalse(evidence.CriticalWarnings.HasDefinedCriticalWarning);
        Assert.AreEqual((byte)0xE0, evidence.CriticalWarnings.ReservedOrFutureBits);
    }

    [TestMethod]
    public void NormalizedSpareValuesOutsideOneHundredFailClosed()
    {
        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsNvmeHealthQueryCodec.ParseHealthLog(
                0,
                BuildHealthLog(spare: 101)));
        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsNvmeHealthQueryCodec.ParseHealthLog(
                0,
                BuildHealthLog(spareThreshold: 101)));
    }

    [TestMethod]
    public void HealthResponseValidatesProtocolDescriptorAndPayloadRange()
    {
        var response = BuildProtocolResponse(BuildHealthLog(powerOnHours: 99));
        var evidence = WindowsNvmeHealthQueryCodec.ParseHealthResponse(
            2,
            response,
            response.Length);
        Assert.AreEqual((UInt128)99, evidence.PowerOnHours);

        var wrongVersion = (byte[])response.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(wrongVersion.AsSpan(0, 4), 49);
        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsNvmeHealthQueryCodec.ParseHealthResponse(
                2,
                wrongVersion,
                wrongVersion.Length));

        var wrongSize = (byte[])response.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(wrongSize.AsSpan(4, 4), 49);
        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsNvmeHealthQueryCodec.ParseHealthResponse(
                2,
                wrongSize,
                wrongSize.Length));

        var wrongProtocol = (byte[])response.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(wrongProtocol.AsSpan(8, 4), 2);
        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsNvmeHealthQueryCodec.ParseHealthResponse(
                2,
                wrongProtocol,
                wrongProtocol.Length));

        var shortOffset = (byte[])response.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(shortOffset.AsSpan(24, 4), 39);
        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsNvmeHealthQueryCodec.ParseHealthResponse(
                2,
                shortOffset,
                shortOffset.Length));

        var excessiveOffset = (byte[])response.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(excessiveOffset.AsSpan(24, 4), uint.MaxValue);
        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsNvmeHealthQueryCodec.ParseHealthResponse(
                2,
                excessiveOffset,
                excessiveOffset.Length));

        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsNvmeHealthQueryCodec.ParseHealthResponse(
                2,
                response,
                response.Length - 1));
    }

    [TestMethod]
    public void UInt128CountersPreserveHighBits()
    {
        var large = ((UInt128)0x0123456789ABCDEFUL << 64) |
            0xFEDCBA9876543210UL;
        var log = BuildHealthLog(mediaErrors: large);

        var evidence = WindowsNvmeHealthQueryCodec.ParseHealthLog(0, log);

        Assert.AreEqual(large, evidence.MediaErrors);
    }

    [TestMethod]
    public void ResultContractsRejectMismatchedOrMissingEvidence()
    {
        var evidence = WindowsNvmeHealthQueryCodec.ParseHealthLog(
            1,
            BuildHealthLog());
        var result = NvmeHealthEvidenceResult.Available(evidence, "captured");
        Assert.AreEqual(1, result.PhysicalDiskNumber);
        Assert.AreSame(evidence, result.Evidence);

        Assert.ThrowsException<ArgumentException>(() =>
            new NvmeHealthEvidenceResult(
                2,
                NvmeHealthEvidenceStatus.Available,
                evidence,
                "mismatched"));
        Assert.ThrowsException<ArgumentException>(() =>
            new NvmeHealthEvidenceResult(
                1,
                NvmeHealthEvidenceStatus.Available,
                null,
                "missing"));
    }

    private static uint ReadUInt32(byte[] value, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(offset, 4));

    private static byte[] BuildProtocolResponse(byte[] healthLog)
    {
        var buffer = new byte[WindowsNvmeHealthQueryCodec.QueryBufferBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), 48);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), 48);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8, 4), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12, 4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16, 4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(20, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(24, 4), 40);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(28, 4), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(32, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(36, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(40, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(44, 4), 0);
        healthLog.AsSpan().CopyTo(buffer.AsSpan(48, 512));
        return buffer;
    }

    private static byte[] BuildHealthLog(
        byte criticalWarning = 0,
        ushort temperatureKelvin = 295,
        byte spare = 100,
        byte spareThreshold = 10,
        byte percentageUsed = 0,
        UInt128 powerCycles = default,
        UInt128 powerOnHours = default,
        UInt128 unsafeShutdowns = default,
        UInt128 mediaErrors = default,
        UInt128 errorEntries = default,
        uint warningTemperatureMinutes = 0,
        uint criticalTemperatureMinutes = 0)
    {
        var log = new byte[512];
        log[0] = criticalWarning;
        BinaryPrimitives.WriteUInt16LittleEndian(log.AsSpan(1, 2), temperatureKelvin);
        log[3] = spare;
        log[4] = spareThreshold;
        log[5] = percentageUsed;
        WriteUInt128(log.AsSpan(112, 16), powerCycles);
        WriteUInt128(log.AsSpan(128, 16), powerOnHours);
        WriteUInt128(log.AsSpan(144, 16), unsafeShutdowns);
        WriteUInt128(log.AsSpan(160, 16), mediaErrors);
        WriteUInt128(log.AsSpan(176, 16), errorEntries);
        BinaryPrimitives.WriteUInt32LittleEndian(
            log.AsSpan(192, 4),
            warningTemperatureMinutes);
        BinaryPrimitives.WriteUInt32LittleEndian(
            log.AsSpan(196, 4),
            criticalTemperatureMinutes);
        return log;
    }

    private static void WriteUInt128(Span<byte> destination, UInt128 value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[0..8],
            (ulong)value);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[8..16],
            (ulong)(value >> 64));
    }
}
