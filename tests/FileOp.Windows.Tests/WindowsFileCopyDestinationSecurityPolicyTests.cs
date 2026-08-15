using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FileOp.Windows.Tests;

/// <summary>
/// Pins the ordinary-token security creation behavior relied on by the cross-volume Move
/// policy. The reviewed Copy primitive creates its destination without an explicit security
/// descriptor, so a new file must receive destination-context/default inherited DACL state
/// rather than cloning the source file's DACL.
///
/// This test intentionally queries/sets DACL information only. It does not request a SACL,
/// ACCESS_SYSTEM_SECURITY, backup-security information, or another privileged security view.
/// </summary>
[TestClass]
public sealed class WindowsFileCopyDestinationSecurityPolicyTests
{
    private const uint SddlRevision1 = 1;
    private const uint DaclSecurityInformation = 0x00000004u;
    private const uint ProtectedDaclSecurityInformation = 0x80000000u;
    private const int ErrorInsufficientBuffer = 122;

    [TestMethod]
    [TestCategory("CrossVolumeMoveSecurityNative")]
    public async Task CopyDestinationUsesDestinationInheritedDaclRatherThanSourceDacl()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "FileOp.CopyDestinationSecurity.Tests",
            Guid.NewGuid().ToString("N"));
        var sourceDirectory = Path.Combine(root, "source");
        var destinationDirectory = Path.Combine(root, "destination");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);

        var sourcePath = Path.Combine(sourceDirectory, "payload.txt");
        var destinationPath = Path.Combine(destinationDirectory, "payload.txt");
        var destinationControlPath = Path.Combine(destinationDirectory, "control.txt");

        try
        {
            // Both directories retain full control for Authenticated Users so the test can
            // run under an ordinary token and clean up after itself. The source directory has
            // one extra inheritable Everyone/read ACE solely to make source and destination
            // child DACLs observably different.
            ApplyProtectedDacl(
                sourceDirectory,
                "D:P(A;OICI;FA;;;AU)(A;OICI;FR;;;WD)");
            ApplyProtectedDacl(
                destinationDirectory,
                "D:P(A;OICI;FA;;;AU)");

            File.WriteAllText(sourcePath, "security-policy-source");
            File.WriteAllText(destinationControlPath, "destination-control");

            var sourceDacl = ReadDaclSddl(sourcePath);
            var destinationControlDacl = ReadDaclSddl(destinationControlPath);
            Assert.AreNotEqual(
                sourceDacl,
                destinationControlDacl,
                "The fixture must prove source and destination-parent security contexts differ before Copy.");

            var plan = CreateCopyPlan(sourceDirectory, destinationDirectory, sourcePath);
            var validation = await new WindowsFileOperationExecutionValidator()
                .ValidateAsync(plan);
            Assert.IsTrue(validation.CanBeginMutation, validation.Summary);
            Assert.AreEqual(1, validation.Items.Count);
            Assert.AreEqual(
                FileOperationExecutionValidationDecision.Ready,
                validation.Items[0].Decision);

            var primitive = new WindowsFileCopyMutationPrimitive();
            var lease = await primitive.CopyNewFileAsync(
                new FileCopyMutationRequest(
                    validation.Items[0],
                    validation.SourceDirectory,
                    validation.DestinationDirectory));
            try
            {
                Assert.AreEqual(destinationPath, lease.Receipt.CanonicalDestinationPath, ignoreCase: true);
            }
            finally
            {
                await lease.DisposeAsync();
            }

            Assert.IsTrue(File.Exists(destinationPath));
            Assert.AreEqual("security-policy-source", File.ReadAllText(destinationPath));

            var copiedDestinationDacl = ReadDaclSddl(destinationPath);
            Assert.AreEqual(
                destinationControlDacl,
                copiedDestinationDacl,
                "The reviewed Copy primitive must use destination-context/default inherited DACL semantics.");
            Assert.AreNotEqual(
                sourceDacl,
                copiedDestinationDacl,
                "Cross-volume Move must not accidentally depend on source-DACL cloning that the Copy primitive does not implement.");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static FileOperationPlan CreateCopyPlan(
        string sourceDirectory,
        string destinationDirectory,
        string sourcePath)
    {
        var entry = new FileOperationEntry(
            sourcePath,
            Path.GetFileName(sourcePath),
            IsDirectory: false);
        return new FileOperationPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            FileOperationKind.Copy,
            FileOperationCollisionPolicy.Stop,
            new FileOperationIntent(
                "SecuritySource",
                Guid.NewGuid(),
                sourceDirectory,
                new[] { entry },
                "SecurityDestination",
                Guid.NewGuid(),
                destinationDirectory));
    }

    private static void ApplyProtectedDacl(string path, string sddl)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl,
                SddlRevision1,
                out var descriptor,
                out _))
        {
            throw Win32Failure("Converting test DACL SDDL");
        }

        try
        {
            if (!SetFileSecurityW(
                    path,
                    DaclSecurityInformation | ProtectedDaclSecurityInformation,
                    descriptor))
            {
                throw Win32Failure($"Applying protected test DACL to '{path}'");
            }
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private static string ReadDaclSddl(string path)
    {
        if (GetFileSecurityW(
                path,
                DaclSecurityInformation,
                IntPtr.Zero,
                0,
                out var requiredLength))
        {
            throw new InvalidOperationException(
                "GetFileSecurityW unexpectedly succeeded without a descriptor buffer.");
        }

        var sizingError = Marshal.GetLastWin32Error();
        if (sizingError != ErrorInsufficientBuffer || requiredLength == 0)
        {
            throw new Win32Exception(
                sizingError,
                $"Sizing the DACL security descriptor for '{path}' failed.");
        }

        var descriptor = Marshal.AllocHGlobal(checked((int)requiredLength));
        try
        {
            if (!GetFileSecurityW(
                    path,
                    DaclSecurityInformation,
                    descriptor,
                    requiredLength,
                    out _))
            {
                throw Win32Failure($"Reading the DACL security descriptor for '{path}'");
            }

            if (!ConvertSecurityDescriptorToStringSecurityDescriptorW(
                    descriptor,
                    SddlRevision1,
                    DaclSecurityInformation,
                    out var sddlPointer,
                    out _))
            {
                throw Win32Failure($"Converting the DACL security descriptor for '{path}' to SDDL");
            }

            try
            {
                return Marshal.PtrToStringUni(sddlPointer)
                    ?? throw new InvalidOperationException(
                        $"Windows returned a null DACL SDDL string for '{path}'.");
            }
            finally
            {
                LocalFree(sddlPointer);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(descriptor);
        }
    }

    private static Win32Exception Win32Failure(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{operation} failed with Win32 error {error}.");
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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string StringSecurityDescriptor,
        uint StringSDRevision,
        out IntPtr SecurityDescriptor,
        out uint SecurityDescriptorSize);

    [DllImport(
        "advapi32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        ExactSpelling = true,
        CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileSecurityW(
        string lpFileName,
        uint SecurityInformation,
        IntPtr pSecurityDescriptor);

    [DllImport(
        "advapi32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        ExactSpelling = true,
        CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileSecurityW(
        string lpFileName,
        uint RequestedInformation,
        IntPtr pSecurityDescriptor,
        uint nLength,
        out uint lpnLengthNeeded);

    [DllImport(
        "advapi32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        ExactSpelling = true,
        CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(
        IntPtr SecurityDescriptor,
        uint RequestedStringSDRevision,
        uint SecurityInformation,
        out IntPtr StringSecurityDescriptor,
        out uint StringSecurityDescriptorLen);

    [DllImport(
        "kernel32.dll",
        SetLastError = false,
        ExactSpelling = true,
        CallingConvention = CallingConvention.Winapi)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
