using System;
using System.IO;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class DirectoryOperationTreeManifestStreamSyntaxTests
{
    [TestMethod]
    public void StreamLikeRelativePathComponentIsRejectedBeforeManifestCreation()
    {
        var root = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "FileOp.DirectoryOperationTreeManifest.StreamSyntax.Tests",
            "root"));
        var rootIdentity = new FileIdentity(77, 1);
        var fidelity = new DirectoryOperationFidelityEvidence(
            root,
            DirectoryOperationFidelityFeature.None,
            EnumerationComplete: true,
            MetadataInspectionComplete: true);
        var rootEvidence = new FileOperationCanonicalPath(
            root,
            root,
            FileOperationCanonicalPathState.Directory,
            IsLeafReparsePoint: false,
            Identity: rootIdentity);
        var relative = "plain.txt:stream";
        var childPath = Path.Combine(root, relative);
        var child = new DirectoryOperationTreeEntryEvidence(
            relative,
            new FileOperationCanonicalPath(
                childPath,
                childPath,
                FileOperationCanonicalPathState.File,
                IsLeafReparsePoint: false,
                Identity: new FileIdentity(77, 2)));

        var exception = CaptureException(() =>
            DirectoryOperationTreeManifest.Create(
                fidelity,
                rootEvidence,
                new[] { child }));

        Assert.AreEqual(typeof(ArgumentException), exception.GetType());
        StringAssert.Contains(exception.Message, "stream-like");
    }

    private static Exception CaptureException(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            return exception;
        }

        Assert.Fail("Expected operation to throw.");
        throw new InvalidOperationException("Assert.Fail returned unexpectedly.");
    }
}
