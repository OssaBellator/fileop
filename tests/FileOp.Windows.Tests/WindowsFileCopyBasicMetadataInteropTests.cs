using System;
using System.Reflection;
using System.Runtime.InteropServices;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsFileCopyBasicMetadataInteropTests
{
    [TestMethod]
    public void FileBasicInfoInteropContractMatchesWindowsAbi()
    {
        var helperType = typeof(WindowsFileCopyBasicMetadata);
        var basicType = GetNestedType(helperType, "FileBasicInformation");
        var classType = GetNestedType(helperType, "FileInfoByHandleClass");

        Assert.AreEqual(40, Marshal.SizeOf(basicType));
        AssertOffset(basicType, "CreationTime", 0);
        AssertOffset(basicType, "LastAccessTime", 8);
        AssertOffset(basicType, "LastWriteTime", 16);
        AssertOffset(basicType, "ChangeTime", 24);
        AssertOffset(basicType, "FileAttributes", 32);
        Assert.IsTrue(classType.IsEnum);
        Assert.AreEqual(typeof(int), Enum.GetUnderlyingType(classType));
        Assert.AreEqual(0, Convert.ToInt32(Enum.Parse(classType, "FileBasicInfo")));

        var method = GetPrivateStaticMethod(helperType, "SetFileInformationByHandle");
        Assert.AreEqual(typeof(bool), method.ReturnType);
        AssertBooleanMarshalling(method);
        AssertKernel32Import(method);

        var parameters = method.GetParameters();
        Assert.AreEqual(4, parameters.Length);
        Assert.AreEqual(typeof(SafeFileHandle), parameters[0].ParameterType);
        Assert.AreEqual(classType, parameters[1].ParameterType);
        Assert.IsTrue(parameters[2].ParameterType.IsByRef);
        Assert.IsFalse(parameters[2].IsOut);
        Assert.AreEqual(basicType, parameters[2].ParameterType.GetElementType());
        Assert.AreEqual(typeof(uint), parameters[3].ParameterType);
    }

    [TestMethod]
    public void ByHandleInformationInteropContractMatchesWindowsAbi()
    {
        var helperType = typeof(WindowsFileCopyBasicMetadata);
        var informationType = GetNestedType(helperType, "ByHandleFileInformation");
        var fileTimeType = GetNestedType(helperType, "FileTime");

        Assert.AreEqual(8, Marshal.SizeOf(fileTimeType));
        AssertOffset(fileTimeType, "LowDateTime", 0);
        AssertOffset(fileTimeType, "HighDateTime", 4);

        Assert.AreEqual(52, Marshal.SizeOf(informationType));
        AssertOffset(informationType, "FileAttributes", 0);
        AssertOffset(informationType, "CreationTime", 4);
        AssertOffset(informationType, "LastAccessTime", 12);
        AssertOffset(informationType, "LastWriteTime", 20);
        AssertOffset(informationType, "VolumeSerialNumber", 28);
        AssertOffset(informationType, "FileSizeHigh", 32);
        AssertOffset(informationType, "FileSizeLow", 36);
        AssertOffset(informationType, "NumberOfLinks", 40);
        AssertOffset(informationType, "FileIndexHigh", 44);
        AssertOffset(informationType, "FileIndexLow", 48);

        var method = GetPrivateStaticMethod(helperType, "GetFileInformationByHandle");
        Assert.AreEqual(typeof(bool), method.ReturnType);
        AssertBooleanMarshalling(method);
        AssertKernel32Import(method);

        var parameters = method.GetParameters();
        Assert.AreEqual(2, parameters.Length);
        Assert.AreEqual(typeof(SafeFileHandle), parameters[0].ParameterType);
        Assert.IsTrue(parameters[1].IsOut);
        Assert.IsTrue(parameters[1].ParameterType.IsByRef);
        Assert.AreEqual(informationType, parameters[1].ParameterType.GetElementType());
    }

    private static Type GetNestedType(Type declaringType, string name) =>
        declaringType.GetNestedType(name, BindingFlags.NonPublic)
        ?? throw new AssertFailedException($"Missing private interop type '{name}'.");

    private static MethodInfo GetPrivateStaticMethod(Type declaringType, string name) =>
        declaringType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new AssertFailedException($"Missing private interop method '{name}'.");

    private static void AssertOffset(Type type, string fieldName, int expected) =>
        Assert.AreEqual(new IntPtr(expected), Marshal.OffsetOf(type, fieldName));

    private static void AssertKernel32Import(MethodInfo method)
    {
        var attribute = method.GetCustomAttribute<DllImportAttribute>()
            ?? throw new AssertFailedException($"{method.Name} is missing DllImportAttribute.");
        Assert.IsTrue(
            string.Equals(attribute.Value, "kernel32.dll", StringComparison.OrdinalIgnoreCase),
            $"{method.Name} must import kernel32.dll.");
        Assert.IsTrue(attribute.SetLastError, $"{method.Name} must preserve the Win32 last-error value.");
        Assert.AreEqual(CallingConvention.Winapi, attribute.CallingConvention);
    }

    private static void AssertBooleanMarshalling(MethodInfo method)
    {
        var attribute = method.ReturnParameter.GetCustomAttribute<MarshalAsAttribute>()
            ?? throw new AssertFailedException($"{method.Name} return value is missing MarshalAsAttribute.");
        Assert.AreEqual(UnmanagedType.Bool, attribute.Value);
    }
}
