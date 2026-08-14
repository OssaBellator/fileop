using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace FileOp.Windows.IndexingService;

internal sealed record WindowsAuthenticodeFileTrustResult(
    string SignerThumbprint,
    string SignerSubject);

/// <summary>
/// Verifies an embedded Authenticode signature and pins the signer certificate to
/// thumbprints compiled into FileOp.Windows through AssemblyMetadata.
/// </summary>
internal static class WindowsAuthenticodeFileTrust
{
    private const string TrustedSignerMetadataName = "FileOpTrustedIndexerSignerThumbprints";
    private static readonly Guid GenericVerifyV2 =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeWholeChain = 1;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionIgnore = 0;
    private const uint WtdProvFlagsRevocationCheckChainExcludeRoot = 0x00000080;

    internal static WindowsAuthenticodeFileTrustResult VerifyPinnedEmbeddedSignature(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var trustedThumbprints = GetTrustedSignerThumbprints();
        if (trustedThumbprints.Length == 0)
        {
#if DEBUG
            if (string.Equals(
                    Environment.GetEnvironmentVariable("FILEOP_ALLOW_UNSIGNED_ELEVATED_HELPER_FOR_DEVELOPMENT"),
                    "1",
                    StringComparison.Ordinal))
            {
                return new WindowsAuthenticodeFileTrustResult("DEBUG-BYPASS", "Explicit debug-only unsigned helper bypass");
            }
#endif
            throw new InvalidOperationException(
                "Elevated helper launch is disabled because this build contains no trusted indexer signer thumbprints. " +
                "Production builds must set FileOpTrustedIndexerSignerThumbprints at build time.");
        }

        VerifyAuthenticodePolicy(fullPath);

#pragma warning disable SYSLIB0057 // The framework has no replacement for extracting an Authenticode signer from a PE file.
        using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(fullPath));
#pragma warning restore SYSLIB0057
        var thumbprint = NormalizeThumbprint(signer.Thumbprint);
        if (thumbprint.Length == 0 ||
            !trustedThumbprints.Contains(thumbprint, StringComparer.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"The indexing helper is Authenticode-signed but its signer thumbprint '{thumbprint}' is not pinned by this FileOp build.");
        }

        return new WindowsAuthenticodeFileTrustResult(thumbprint, signer.Subject);
    }

    private static string[] GetTrustedSignerThumbprints()
    {
        var metadata = typeof(WindowsAuthenticodeFileTrust)
            .Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(
                attribute.Key,
                TrustedSignerMetadataName,
                StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(metadata?.Value))
        {
            return Array.Empty<string>();
        }

        return metadata.Value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeThumbprint)
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeThumbprint(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    private static void VerifyAuthenticodePolicy(string fullPath)
    {
        var filePathPointer = Marshal.StringToCoTaskMemUni(fullPath);
        var fileInfoPointer = IntPtr.Zero;
        var trustDataPointer = IntPtr.Zero;
        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                StructSize = checked((uint)Marshal.SizeOf<WinTrustFileInfo>()),
                FilePath = filePathPointer,
            };
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);

            var trustData = new WinTrustData
            {
                StructSize = checked((uint)Marshal.SizeOf<WinTrustData>()),
                UiChoice = WtdUiNone,
                RevocationChecks = WtdRevokeWholeChain,
                UnionChoice = WtdChoiceFile,
                FileInfo = fileInfoPointer,
                StateAction = WtdStateActionIgnore,
                ProviderFlags = WtdProvFlagsRevocationCheckChainExcludeRoot,
            };
            trustDataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(trustData, trustDataPointer, fDeleteOld: false);

            var action = GenericVerifyV2;
            var status = WinVerifyTrust(new IntPtr(-1), ref action, trustDataPointer);
            if (status != 0)
            {
                throw new UnauthorizedAccessException(
                    $"Authenticode verification failed for '{fullPath}' with status 0x{unchecked((uint)status):X8}.",
                    new Win32Exception(status));
            }
        }
        finally
        {
            if (trustDataPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(trustDataPointer);
            }
            if (fileInfoPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(fileInfoPointer);
            }
            Marshal.FreeCoTaskMem(filePathPointer);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        ref Guid actionId,
        IntPtr trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}

/// <summary>
/// Rejects elevation from a helper/app directory that the current unelevated token
/// can mutate. Each dangerous access right is probed independently so possessing
/// any one mutation capability is enough to block elevation.
/// </summary>
internal static class WindowsElevatedHelperPathProtection
{
    private const uint FileWriteData = 0x0002;
    private const uint FileAppendData = 0x0004;
    private const uint FileWriteAttributes = 0x0100;
    private const uint DeleteAccess = 0x00010000;
    private const uint WriteDac = 0x00040000;
    private const uint WriteOwner = 0x00080000;
    private const uint FileAddFile = 0x0002;
    private const uint FileAddSubdirectory = 0x0004;
    private const uint FileDeleteChild = 0x0040;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    private static readonly uint[] FileMutationRights =
    [
        FileWriteData,
        FileAppendData,
        FileWriteAttributes,
        DeleteAccess,
        WriteDac,
        WriteOwner,
    ];

    private static readonly uint[] DirectoryMutationRights =
    [
        FileAddFile,
        FileAddSubdirectory,
        FileDeleteChild,
        FileWriteAttributes,
        DeleteAccess,
        WriteDac,
        WriteOwner,
    ];

    internal static void RequireProtectedLaunchPath(string helperPath)
    {
        var fullHelperPath = Path.GetFullPath(helperPath);
        var helperDirectory = Path.GetDirectoryName(fullHelperPath)
            ?? throw new InvalidOperationException("The indexing helper path has no containing directory.");
        var parentDirectory = Directory.GetParent(helperDirectory)?.FullName
            ?? throw new InvalidOperationException("The indexing helper directory has no parent directory.");

        if (CanOpenForAnyMutation(fullHelperPath, isDirectory: false))
        {
            throw new UnauthorizedAccessException(
                "Elevated helper launch is blocked because the current unelevated token can mutate the helper executable.");
        }

        if (CanOpenForAnyMutation(helperDirectory, isDirectory: true))
        {
            throw new UnauthorizedAccessException(
                "Elevated helper launch is blocked because the current unelevated token can mutate the helper directory.");
        }

        if (CanOpenForAnyMutation(parentDirectory, isDirectory: true))
        {
            throw new UnauthorizedAccessException(
                "Elevated helper launch is blocked because the current unelevated token can replace the helper directory from its parent.");
        }
    }

    private static bool CanOpenForAnyMutation(string path, bool isDirectory)
    {
        var rights = isDirectory ? DirectoryMutationRights : FileMutationRights;
        foreach (var desiredAccess in rights)
        {
            using var handle = CreateFileW(
                path,
                desiredAccess,
                FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero,
                FileMode.Open,
                (isDirectory ? FileFlagBackupSemantics : 0) | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                return true;
            }
        }

        return false;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}
