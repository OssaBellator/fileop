using System;
using System.Reflection;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileNamedDataStreamNameValidationTests
{
    [TestMethod]
    public void NamedDataStreamValidatorEnforcesWindowsStreamNameRules()
    {
        var digestType = typeof(WindowsRootBoundFileNamedDataStreamTopologyEvidenceReader)
            .Assembly
            .GetType(
                "FileOp.Windows.Operations.WindowsFileNamedDataStreamTopologyDigest",
                throwOnError: true)!;
        var validator = digestType.GetMethod(
            "IsNamedDataStream",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Named-stream validator was not found.");

        bool IsValid(string name) => validator.Invoke(null, new object[] { name }) is true;

        Assert.IsTrue(IsValid(":ads:$DATA"));
        Assert.IsTrue(IsValid(":$DATA:$DATA"));
        Assert.IsTrue(IsValid($":{new string('a', 255)}:$DATA"));
        Assert.IsFalse(IsValid($":{new string('a', 256)}:$DATA"));
        Assert.IsFalse(IsValid(":parent/child:$DATA"));
        Assert.IsFalse(IsValid(":parent\\child:$DATA"));
        Assert.IsFalse(IsValid(":parent:child:$DATA"));
    }
}
