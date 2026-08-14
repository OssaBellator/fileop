using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

[TestClass]
public sealed class WindowsFileCopyMutationSecurityPolicyTests
{
    private const uint ErrorSuccess = 0;
    private const uint DaclSecurityInformation = 0x00000004u;
    private const uint ProtectedDaclSecurityInformation = 0x80000000u;
    private const int SeFileObject = 1;

    [TestMethod]
    public async Task ReviewedCopyCreatesDestinationWithDefaultSecurityInsteadOfCloningSourceNullDacl()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "FileOp.CopySecurityPolicy.Tests",
            Guid.NewGuid().ToString("N"));
        var sourceDirectory = Path.Combine(root, "source");
        var destinationDirectory = Path.Combine(root, "destination");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);

        var sourcePath = Path.Combine(sourceDirectory, "payload.txt");
        var destinationPath = Path.Combine(destinationDirectory, "payload.txt");
        File.WriteAllText(sourcePath, "destination-default security policy");

        try
        {
            SetProtectedNullDacl(sourcePath);
            Assert.IsTrue(
                ReadDaclIsNull(sourcePath),
                "The test source must have a protected NULL DACL so cloning source security is distinguishable from default destination creation.");
            Assert.IsFalse(
                ReadDaclIsNull(destinationDirectory),
                "The destination parent must expose an ordinary non-NULL DACL for this inheritance/default-security regression.");

            var entry = new FileOperationEntry(
                sourcePath,
                Path.GetFileName(sourcePath),
                IsDirectory: false);
            var plan = new FileOperationPlan(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                FileOperationKind.Copy,
                FileOperationCollisionPolicy.Stop,
                new FileOperationIntent(
                    "Left",
                    Guid.NewGuid(),
                    sourceDirectory,
                    new[] { entry },
                    "Right",
                    Guid.NewGuid(),
                    destinationDirectory));

            var validation = await new WindowsFileOperationExecutionValidator().ValidateAsync(plan);
            Assert.AreEqual(FileOperationExecutionValidationStatus.Ready, validation.Status, validation.Summary);
            Assert.AreEqual(1, validation.Items.Count);
            Assert.AreEqual(FileOperationExecutionValidationDecision.Ready, validation.Items[0].Decision);

            var primitive = new WindowsFileCopyMutationPrimitive();
            await using (var lease = await primitive.CopyNewFileAsync(
                new FileCopyMutationRequest(
                    validation.Items[0],
                    validation.SourceDirectory,
                    validation.DestinationDirectory)))
            {
                Assert.AreEqual(
                    Path.GetFullPath(destinationPath),
                    Path.GetFullPath(lease.Receipt.CanonicalDestinationPath),
                    ignoreCase: true);
            }

            Assert.IsTrue(File.Exists(destinationPath));
            Assert.IsFalse(
                ReadDaclIsNull(destinationPath),
                "The reviewed Copy primitive must not clone the source NULL DACL. A new destination created with a null/default OBJECT_ATTRIBUTES security descriptor receives destination-context/default security instead.");
        }
        finally
        {
            TryDeleteFile(destinationPath);
            TryDeleteFile(sourcePath);
            TryDeleteDirectory(destinationDirectory);
            TryDeleteDirectory(sourceDirectory);
            TryDeleteDirectory(root);
        }
    }

    private static void SetProtectedNullDacl(string path)
    {
        var result = SetNamedSecurityInfoW(
            path,
            SeFileObject,
            DaclSecurityInformation | ProtectedDaclSecurityInformation,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        Assert.AreEqual(
            ErrorSuccess,
            result,
            $"SetNamedSecurityInfoW could not prepare the source NULL DACL; Win32 error {result}.");
    }

    private static bool ReadDaclIsNull(string path)
    {
        var result = GetNamedSecurityInfoW(
            path,
            SeFileObject,
            DaclSecurityInformation,
            out _,
            out _,
            out var dacl,
            out _,
            out var securityDescriptor);
        try
        {
            Assert.AreEqual(
                ErrorSuccess,
                result,
                $"GetNamedSecurityInfoW could not read the ordinary DACL for '{path}'; Win32 error {result}.");
            return dacl == IntPtr.Zero;
        }
        finally
        {
            if (securityDescriptor != IntPtr.Zero)
            {
                _ = LocalFree(securityDescriptor);
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [DllImport(
        "advapi32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        ExactSpelling = true,
        CallingConvention = CallingConvention.Winapi)]
    private static extern uint SetNamedSecurityInfoW(
        string pObjectName,
        int objectType,
        uint securityInfo,
        IntPtr psidOwner,
        IntPtr psidGroup,
        IntPtr pDacl,
        IntPtr pSacl);

    [DllImport(
        "advapi32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        ExactSpelling = true,
        CallingConvention = CallingConvention.Winapi)]
    private static extern uint GetNamedSecurityInfoW(
        string pObjectName,
        int objectType,
        uint securityInfo,
        out IntPtr ppsidOwner,
        out IntPtr ppsidGroup,
        out IntPtr ppDacl,
        out IntPtr ppSacl,
        out IntPtr ppSecurityDescriptor);

    [DllImport(
        "kernel32.dll",
        SetLastError = true,
        ExactSpelling = true,
        CallingConvention = CallingConvention.Winapi)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
