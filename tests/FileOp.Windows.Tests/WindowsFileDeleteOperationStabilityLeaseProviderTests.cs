using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsFileDeleteOperationStabilityLeaseProviderTests
{
    [TestMethod]
    public async Task LeaseBindsExactAuthorizationAndBlocksWriteDeleteAndParentRenameUntilDisposed()
    {
        using var fixture = new DeleteFixture();
        await File.WriteAllTextAsync(fixture.SourcePath, "authorized-content");
        var authorization = await fixture.AuthorizeAsync();
        var request = new FileDeleteOperationStabilityLeaseRequest(authorization, 0);
        var provider = fixture.CreateProvider();

        var lease = await provider.AcquireAsync(request);
        try
        {
            Assert.IsTrue(lease.Evidence.IsBoundTo(authorization, 0));
            Assert.AreEqual(authorization.AuthorizationId, lease.Evidence.AuthorizationId);
            Assert.AreEqual(authorization.PlanId, lease.Evidence.PlanId);
            Assert.AreEqual(authorization.CanonicalSourceDirectoryPath, lease.Evidence.CanonicalSourceDirectoryPath);
            Assert.AreEqual(authorization.SourceDirectoryIdentity, lease.Evidence.SourceDirectoryIdentity);
            Assert.AreEqual(authorization.Items[0].CanonicalPath, lease.Evidence.CanonicalSourcePath);
            Assert.AreEqual(authorization.Items[0].Identity, lease.Evidence.SourceIdentity);
            Assert.IsFalse(request.DeleteMutationAuthorized);
            Assert.IsFalse(lease.Evidence.DeleteMutationAuthorized);
            Assert.IsFalse(lease.DeleteMutationAuthorized);

            Assert.IsTrue(DeleteIsBlocked(fixture.SourcePath));
            Assert.IsTrue(WriteOpenIsBlocked(fixture.SourcePath));

            var movedRoot = fixture.RootPath + ".moved";
            Assert.IsTrue(DirectoryRenameIsBlocked(fixture.RootPath, movedRoot));
            Assert.IsTrue(File.Exists(fixture.SourcePath));
            Assert.AreEqual("authorized-content", await File.ReadAllTextAsync(fixture.SourcePath));
        }
        finally
        {
            await lease.DisposeAsync();
        }

        await File.AppendAllTextAsync(fixture.SourcePath, "-after-dispose");
        Assert.AreEqual("authorized-content-after-dispose", await File.ReadAllTextAsync(fixture.SourcePath));
        File.Delete(fixture.SourcePath);
        Assert.IsFalse(File.Exists(fixture.SourcePath));
    }

    [TestMethod]
    public async Task FileIdentityReplacementAfterAuthorizationFailsBeforeLease()
    {
        using var fixture = new DeleteFixture();
        await File.WriteAllTextAsync(fixture.SourcePath, "original");
        var authorization = await fixture.AuthorizeAsync();

        var originalPath = fixture.SourcePath + ".original";
        File.Move(fixture.SourcePath, originalPath);
        await File.WriteAllTextAsync(fixture.SourcePath, "replacement");

        await AssertThrowsAsync<IOException>(async () =>
            await fixture.CreateProvider().AcquireAsync(
                new FileDeleteOperationStabilityLeaseRequest(authorization, 0)));

        Assert.AreEqual("original", await File.ReadAllTextAsync(originalPath));
        Assert.AreEqual("replacement", await File.ReadAllTextAsync(fixture.SourcePath));
    }

    [TestMethod]
    public async Task SourceRootReplacementAfterAuthorizationFailsBeforeLease()
    {
        using var fixture = new DeleteFixture();
        await File.WriteAllTextAsync(fixture.SourcePath, "original");
        var authorization = await fixture.AuthorizeAsync();

        var originalRoot = fixture.RootPath + ".original";
        Directory.Move(fixture.RootPath, originalRoot);
        try
        {
            Directory.CreateDirectory(fixture.RootPath);
            await File.WriteAllTextAsync(fixture.SourcePath, "replacement");

            await AssertThrowsAsync<IOException>(async () =>
                await fixture.CreateProvider().AcquireAsync(
                    new FileDeleteOperationStabilityLeaseRequest(authorization, 0)));

            Assert.AreEqual(
                "original",
                await File.ReadAllTextAsync(Path.Combine(originalRoot, DeleteFixture.FileName)));
            Assert.AreEqual("replacement", await File.ReadAllTextAsync(fixture.SourcePath));
        }
        finally
        {
            try
            {
                Directory.Delete(originalRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [TestMethod]
    public async Task ProtectedLocationPolicyIsRecheckedAtLeaseAcquisition()
    {
        using var fixture = new DeleteFixture();
        await File.WriteAllTextAsync(fixture.SourcePath, "authorized-content");
        var authorization = await fixture.AuthorizeAsync();
        var provider = new WindowsFileDeleteOperationStabilityLeaseProvider(
            new AlwaysBlockedPolicy());

        await AssertThrowsAsync<UnauthorizedAccessException>(async () =>
            await provider.AcquireAsync(
                new FileDeleteOperationStabilityLeaseRequest(authorization, 0)));

        Assert.IsTrue(File.Exists(fixture.SourcePath));
    }

    [TestMethod]
    public async Task RequestRejectsWrongOrdinalAndAcquisitionHonorsPreCancellation()
    {
        using var fixture = new DeleteFixture();
        await File.WriteAllTextAsync(fixture.SourcePath, "authorized-content");
        var authorization = await fixture.AuthorizeAsync();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FileDeleteOperationStabilityLeaseRequest(authorization, 1));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(async () =>
            await fixture.CreateProvider().AcquireAsync(
                new FileDeleteOperationStabilityLeaseRequest(authorization, 0),
                cancellation.Token));

        Assert.IsTrue(File.Exists(fixture.SourcePath));
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        Assert.Fail($"Expected {typeof(TException).Name} to be thrown.");
    }

    private static bool DeleteIsBlocked(string path)
    {
        // Probe the documented Windows delete-sharing contract directly. Managed
        // File.Delete can select a different disposition path on newer runtimes.
        if (DeleteFileW(path))
        {
            return false;
        }

        Assert.AreEqual(
            32, // ERROR_SHARING_VIOLATION.
            Marshal.GetLastPInvokeError(),
            "The stability lease should reject a native delete through sharing semantics.");
        return true;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteFileW(string lpFileName);

    private static bool WriteOpenIsBlocked(string path)
    {
        try
        {
            using var writer = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool DirectoryRenameIsBlocked(string path, string destination)
    {
        try
        {
            Directory.Move(path, destination);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private sealed class AlwaysBlockedPolicy : IFileDeleteProtectedLocationPolicy
    {
        public FileDeleteProtectedLocationResult Evaluate(string canonicalPath) =>
            new(
                FileDeleteProtectedLocationDecision.Blocked,
                "blocked by test policy");
    }

    private sealed class DeleteFixture : IDisposable
    {
        public const string FileName = "payload.tmp";

        private static readonly IFileDeleteProtectedLocationPolicy PermissivePolicy =
            new WindowsFileDeleteProtectedLocationPolicy(Array.Empty<string>());

        public DeleteFixture()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "FileOp.DeleteStability.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
            SourcePath = Path.Combine(RootPath, FileName);
        }

        public string RootPath { get; }

        public string SourcePath { get; }

        public WindowsFileDeleteOperationStabilityLeaseProvider CreateProvider() =>
            new(PermissivePolicy);

        public async Task<FileDeleteOperationUserAuthorizationReceipt> AuthorizeAsync()
        {
            var entry = new FileOperationEntry(SourcePath, FileName, IsDirectory: false);
            var plan = new FileDeleteOperationPlan(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                new FileDeleteOperationIntent(
                    "left",
                    Guid.NewGuid(),
                    RootPath,
                    new[] { entry }));
            var validation = await new WindowsFileDeleteOperationExecutionValidator(
                    protectedLocationPolicy: PermissivePolicy)
                .ValidateAsync(plan);
            Assert.AreEqual(
                FileDeleteOperationExecutionValidationStatus.ReadyForAuthorizationReview,
                validation.Status);
            Assert.IsTrue(validation.CanRequestAuthorizationReview);
            Assert.IsFalse(validation.DeleteMutationAuthorized);

            return new FileDeleteOperationUserAuthorizationIssuer()
                .IssueAfterExplicitUserConfirmation(validation);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(RootPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
