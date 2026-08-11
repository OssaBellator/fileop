using System.Buffers.Binary;
using FileOp.Core.Performance;
using FileOp.Windows.Performance;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class PhysicalDiskDeviceContextTests
{
    [TestMethod]
    public void DeviceDescriptorParsesReportedFieldsWithoutMediaInference()
    {
        var buffer = BuildDescriptor(
            rawBusType: 17,
            vendor: "ACME",
            product: "FastDisk",
            revision: "1.2",
            serial: "SN123",
            removable: false,
            commandQueueing: true);

        var descriptor = WindowsPhysicalDiskPropertyParser.ParseDeviceDescriptor(3, buffer);

        Assert.AreEqual(3, descriptor.PhysicalDiskNumber);
        Assert.AreEqual(17u, descriptor.RawBusType);
        Assert.AreEqual("NVMe", descriptor.BusTypeLabel);
        Assert.AreEqual("ACME", descriptor.VendorId);
        Assert.AreEqual("FastDisk", descriptor.ProductId);
        Assert.AreEqual("1.2", descriptor.ProductRevision);
        Assert.AreEqual("SN123", descriptor.SerialNumber);
        Assert.AreEqual("ACME FastDisk", descriptor.DisplayName);
        Assert.IsFalse(descriptor.RemovableMedia);
        Assert.IsTrue(descriptor.CommandQueueing);
    }

    [TestMethod]
    public void UnknownReservedBusValueRemainsExplicitNumericEvidence()
    {
        var buffer = BuildDescriptor(rawBusType: 42);

        var descriptor = WindowsPhysicalDiskPropertyParser.ParseDeviceDescriptor(0, buffer);

        Assert.AreEqual(42u, descriptor.RawBusType);
        Assert.AreEqual("Unrecognized bus type 42", descriptor.BusTypeLabel);
    }

    [TestMethod]
    public void BusValueAboveReservedMaximumFailsClosed()
    {
        var buffer = BuildDescriptor(rawBusType: 0x80);

        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsPhysicalDiskPropertyParser.ParseDeviceDescriptor(0, buffer));
    }

    [TestMethod]
    public void DescriptorRejectsInvalidSizeAndStringOffsets()
    {
        var undersized = BuildDescriptor();
        BinaryPrimitives.WriteUInt32LittleEndian(undersized.AsSpan(4, 4), 35);
        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsPhysicalDiskPropertyParser.ParseDeviceDescriptor(0, undersized));

        var insideHeader = BuildDescriptor(vendor: "ACME");
        BinaryPrimitives.WriteUInt32LittleEndian(insideHeader.AsSpan(12, 4), 8);
        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsPhysicalDiskPropertyParser.ParseDeviceDescriptor(0, insideHeader));

        var outside = BuildDescriptor(vendor: "ACME");
        BinaryPrimitives.WriteUInt32LittleEndian(
            outside.AsSpan(12, 4),
            checked((uint)outside.Length));
        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsPhysicalDiskPropertyParser.ParseDeviceDescriptor(0, outside));
    }

    [TestMethod]
    public void DescriptorRejectsUnterminatedAndNonPrintableAscii()
    {
        var unterminated = BuildDescriptor(vendor: "ACME");
        var vendorOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            unterminated.AsSpan(12, 4)));
        unterminated[vendorOffset + 4] = (byte)'X';
        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsPhysicalDiskPropertyParser.ParseDeviceDescriptor(0, unterminated));

        var nonPrintable = BuildDescriptor(vendor: "ACME");
        vendorOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            nonPrintable.AsSpan(12, 4)));
        nonPrintable[vendorOffset + 1] = 0x01;
        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsPhysicalDiskPropertyParser.ParseDeviceDescriptor(0, nonPrintable));
    }

    [TestMethod]
    public void BooleanDescriptorsPreserveReportedValuesAndRejectMalformedBuffers()
    {
        Assert.IsTrue(WindowsPhysicalDiskPropertyParser.ParseBooleanDescriptor(
            BuildBooleanDescriptor(true),
            "seek-penalty"));
        Assert.IsFalse(WindowsPhysicalDiskPropertyParser.ParseBooleanDescriptor(
            BuildBooleanDescriptor(false),
            "TRIM"));

        var tooShort = new byte[8];
        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsPhysicalDiskPropertyParser.ParseBooleanDescriptor(
                tooShort,
                "TRIM"));

        var invalidSize = BuildBooleanDescriptor(true);
        BinaryPrimitives.WriteUInt32LittleEndian(invalidSize.AsSpan(4, 4), 100);
        Assert.ThrowsException<InvalidDataException>(() =>
            WindowsPhysicalDiskPropertyParser.ParseBooleanDescriptor(
                invalidSize,
                "seek-penalty"));
    }

    [TestMethod]
    public void CapabilityContractsKeepUnknownEvidenceSeparateFromFalse()
    {
        var available = PhysicalDiskBooleanCapability.Available(
            false,
            "reported false");
        var unsupported = PhysicalDiskBooleanCapability.Unsupported(
            "not supported");
        var unavailable = PhysicalDiskBooleanCapability.Unavailable(
            "read failed");

        Assert.AreEqual(PhysicalDiskCapabilityStatus.Available, available.Status);
        Assert.AreEqual(false, available.Value);
        Assert.AreEqual(PhysicalDiskCapabilityStatus.Unsupported, unsupported.Status);
        Assert.IsNull(unsupported.Value);
        Assert.AreEqual(PhysicalDiskCapabilityStatus.Unavailable, unavailable.Status);
        Assert.IsNull(unavailable.Value);
        Assert.ThrowsException<ArgumentException>(() =>
            new PhysicalDiskBooleanCapability(
                PhysicalDiskCapabilityStatus.Unsupported,
                false,
                "invalid"));
    }

    [TestMethod]
    public void ContextResultRequiresMatchingPhysicalDiskNumber()
    {
        var descriptor = new PhysicalDiskDeviceDescriptor(
            2,
            11,
            "SATA",
            null,
            "Disk",
            null,
            null,
            false,
            true);
        var context = new PhysicalDiskDeviceContext(
            descriptor,
            PhysicalDiskBooleanCapability.Available(true, "seek reported"),
            PhysicalDiskBooleanCapability.Unsupported("trim unknown"));

        var result = PhysicalDiskDeviceContextResult.Available(context, "captured");

        Assert.AreEqual(2, result.PhysicalDiskNumber);
        Assert.AreSame(context, result.Context);
        Assert.ThrowsException<ArgumentException>(() =>
            InvokeMismatchedResult(context));
    }

    [TestMethod]
    public void NativeProviderIsReadOnlyAndFailureTolerantWhenWindowsIsAvailable()
    {
        var provider = new WindowsPhysicalDiskDeviceContextProvider();
        var result = provider.Query(0);

        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(PhysicalDiskDeviceContextStatus.Unsupported, result.Status);
            Assert.IsNull(result.Context);
            return;
        }

        Assert.IsTrue(Enum.IsDefined(result.Status));
        if (result.Status == PhysicalDiskDeviceContextStatus.Available)
        {
            Assert.IsNotNull(result.Context);
            Assert.AreEqual(0, result.Context.Descriptor.PhysicalDiskNumber);
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.Context.Descriptor.BusTypeLabel));
        }
        else
        {
            Assert.IsNull(result.Context);
        }
    }

    private static void InvokeMismatchedResult(PhysicalDiskDeviceContext context)
    {
        var constructor = typeof(PhysicalDiskDeviceContextResult)
            .GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single();
        try
        {
            constructor.Invoke([
                3,
                PhysicalDiskDeviceContextStatus.Available,
                context,
                "invalid",
            ]);
        }
        catch (System.Reflection.TargetInvocationException exception)
            when (exception.InnerException is ArgumentException inner)
        {
            throw inner;
        }
    }

    private static byte[] BuildDescriptor(
        uint rawBusType = 11,
        string? vendor = null,
        string? product = null,
        string? revision = null,
        string? serial = null,
        bool removable = false,
        bool commandQueueing = false)
    {
        var buffer = new byte[128];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), 37);
        buffer[10] = removable ? (byte)1 : (byte)0;
        buffer[11] = commandQueueing ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(28, 4), rawBusType);

        var next = 36;
        WriteOptionalAscii(buffer, 12, ref next, vendor);
        WriteOptionalAscii(buffer, 16, ref next, product);
        WriteOptionalAscii(buffer, 20, ref next, revision);
        WriteOptionalAscii(buffer, 24, ref next, serial);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(32, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), checked((uint)next));
        return buffer[..next];
    }

    private static void WriteOptionalAscii(
        byte[] buffer,
        int offsetField,
        ref int next,
        string? value)
    {
        if (value is null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offsetField, 4), 0);
            return;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.AsSpan(offsetField, 4),
            checked((uint)next));
        var bytes = System.Text.Encoding.ASCII.GetBytes(value);
        bytes.CopyTo(buffer.AsSpan(next));
        next += bytes.Length;
        buffer[next++] = 0;
    }

    private static byte[] BuildBooleanDescriptor(bool value)
    {
        var buffer = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), 12);
        buffer[8] = value ? (byte)1 : (byte)0;
        return buffer;
    }
}
