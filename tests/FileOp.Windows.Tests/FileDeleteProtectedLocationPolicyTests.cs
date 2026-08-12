using FileOp.Core.Operations;
using FileOp.Windows.Operations;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class FileDeleteProtectedLocationPolicyTests
{
    [TestMethod]
    public void ProtectedLocationResultRejectsMalformedDecisionsAndReasons()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            new FileDeleteProtectedLocationResult(
                (FileDeleteProtectedLocationDecision)999,
                "invalid"));
        Assert.ThrowsException<ArgumentException>(() =>
            new FileDeleteProtectedLocationResult(
                FileDeleteProtectedLocationDecision.Blocked,
                " "));
    }

    [TestMethod]
    public void ResidualExtendedAndDeviceNamespacesFailClosed()
    {
        var policy = new WindowsFileDeleteProtectedLocationPolicy(Array.Empty<string>());

        Assert.IsTrue(policy.Evaluate(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\Users\A\a.tmp").IsBlocked);
        Assert.IsTrue(policy.Evaluate(@"\\.\C:\Users\A\a.tmp").IsBlocked);
        Assert.IsTrue(policy.Evaluate(@"\??\C:\Users\A\a.tmp").IsBlocked);
        Assert.IsFalse(policy.Evaluate(@"C:\Users\A\a.tmp").IsBlocked);
    }
}
