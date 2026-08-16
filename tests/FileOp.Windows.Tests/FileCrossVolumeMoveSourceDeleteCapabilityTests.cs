using System;
using System.Reflection;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileCrossVolumeMoveSourceDeleteCapabilityTests
{
    private const uint FileSupportsPosixUnlinkRename = 0x00000400u;

    [TestMethod]
    public void PosixUnlinkRenameCapabilityBitIsRequired()
    {
        Assert.IsTrue(SupportsPosixUnlinkRename(FileSupportsPosixUnlinkRename));
        Assert.IsTrue(SupportsPosixUnlinkRename(FileSupportsPosixUnlinkRename | 0x00000008u));
    }

    [TestMethod]
    public void UnrelatedFilesystemCapabilityBitsDoNotSatisfyPosixUnlinkRequirement()
    {
        Assert.IsFalse(SupportsPosixUnlinkRename(0));
        Assert.IsFalse(SupportsPosixUnlinkRename(0x00000001u));
        Assert.IsFalse(SupportsPosixUnlinkRename(0x00000008u));
        Assert.IsFalse(SupportsPosixUnlinkRename(0x00040000u));
    }

    private static bool SupportsPosixUnlinkRename(uint fileSystemFlags)
    {
        var method = typeof(WindowsFileCrossVolumeMoveSourceDeletePrimitive).GetMethod(
            "SupportsPosixUnlinkRename",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("SupportsPosixUnlinkRename classifier was not found.");
        return (bool)(method.Invoke(null, new object[] { fileSystemFlags })
            ?? throw new AssertFailedException("SupportsPosixUnlinkRename returned null."));
    }
}
