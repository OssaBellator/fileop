using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace FileOp.Windows.Tests;

/// <summary>
/// Opt-in two-volume proofs for #191. These tests compose the non-authorizing source
/// preflight in front of the dormant composite executor and prove deterministic unsupported
/// source state is refused before durable history and destination Copy. The older native
/// post-Copy refusal tests remain intentionally separate defense-in-depth coverage.
/// </summary>
[TestClass]
public sealed class FileCrossVolumeMoveSourcePreflightNativeTests
{
    private const string SourceRootVariable = "FILEOP_CROSS_VOLUME_MOVE_SOURCE_ROOT";
    private const string DestinationRootVariable = "FILEOP_CROSS_VOLUME_MOVE_DESTINATION_ROOT";
    private const uint FileWriteEa = 0x00000010u;
    private const uint Synchronize = 0x00100000u;

    [TestMethod]
    [TestCategory("CrossVolumeMoveNative")]
    public async Task SourceNamedStreamIsRefusedBeforeHistoryOrDestinationCopy()
    {
        using var fixture = CreateFixture();
        var sourcePath = fixture.CreateSourceFile("preflight-stream.txt", "main payload");
        File.WriteAllText(sourcePath + ":fileop-preflight", "named stream payload");
        var plan = CreatePlan(fixture.SourceDirectory, fixture.DestinationDirectory, sourcePath);
        await AssertReadyOnDifferentVolumesAsync(plan);

        using var history = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.HistoryDatabasePath);
        var result = await CreatePreflightExecutor(history).ExecuteAsync(plan);

        Assert.AreEqual(FileOperationExecutionState.Failed, result.State);
        Assert.AreEqual("CrossVolumeMoveValidationBlocked", result.Failure?.Code);
        Assert.IsTrue(File.Exists(sourcePath));
        Assert.AreEqual("named stream payload", File.ReadAllText(sourcePath + ":fileop-preflight"));
        Assert.IsFalse(File.Exists(DestinationPath(fixture, sourcePath)));
        Assert.IsNull(await history.GetAsync(plan.Id));
    }

    [TestMethod]
    [TestCategory("CrossVolumeMoveNative")]
    public async Task SourceExtendedAttributeIsRefusedBeforeHistoryOrDestinationCopy()
    {
        using var fixture = CreateFixture();
        var sourcePath = fixture.CreateSourceFile("preflight-ea.txt", "main payload with ea");
        SetExtendedAttribute(sourcePath, "FileOpPreflight", "extended attribute payload");
        var plan = CreatePlan(fixture.SourceDirectory, fixture.DestinationDirectory, sourcePath);
        await AssertReadyOnDifferentVolumesAsync(plan);

        using var history = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.HistoryDatabasePath);
        var result = await CreatePreflightExecutor(history).ExecuteAsync(plan);

        Assert.AreEqual(FileOperationExecutionState.Failed, result.State);
        Assert.AreEqual("CrossVolumeMoveValidationBlocked", result.Failure?.Code);
        Assert.IsTrue(File.Exists(sourcePath));
        Assert.IsFalse(File.Exists(DestinationPath(fixture, sourcePath)));
        Assert.IsNull(await history.GetAsync(plan.Id));
    }

    [TestMethod]
    [TestCategory("CrossVolumeMoveNative")]
    public async Task ReadOnlySourceIsRefusedBeforeHistoryOrDestinationCopy()
    {
        using var fixture = CreateFixture();
        var sourcePath = fixture.CreateSourceFile("preflight-readonly.txt", "read-only payload");
        File.SetAttributes(sourcePath, File.GetAttributes(sourcePath) | FileAttributes.ReadOnly);
        try
        {
            var plan = CreatePlan(fixture.SourceDirectory, fixture.DestinationDirectory, sourcePath);
            await AssertReadyOnDifferentVolumesAsync(plan);

            using var history = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.HistoryDatabasePath);
            var result = await CreatePreflightExecutor(history).ExecuteAsync(plan);

            Assert.AreEqual(FileOperationExecutionState.Failed, result.State);
            Assert.AreEqual("CrossVolumeMoveValidationBlocked", result.Failure?.Code);
            Assert.IsTrue(File.Exists(sourcePath));
            Assert.IsFalse(File.Exists(DestinationPath(fixture, sourcePath)));
            Assert.IsNull(await history.GetAsync(plan.Id));
        }
        finally
        {
            if (File.Exists(sourcePath))
            {
                File.SetAttributes(sourcePath, File.GetAttributes(sourcePath) & ~FileAttributes.ReadOnly);
            }
        }
    }

    private static FileCrossVolumeMoveOperationExecutor CreatePreflightExecutor(
        IFileCrossVolumeMoveActionHistoryStore history)
    {
        var preflightProbe = new FileCrossVolumeMoveProtectedLocationPreflightProbe(
            new WindowsFileCrossVolumeMoveSourcePreflightProbe(),
            new WindowsFileDeleteProtectedLocationPolicy());
        var validator = new FileCrossVolumeMovePreflightExecutionValidator(
            new WindowsFileOperationExecutionValidator(),
            preflightProbe);
        return new FileCrossVolumeMoveOperationExecutor(
            validator,
            history,
            new WindowsFileCopyMutationPrimitive(),
            new WindowsFidelityVerifiedFileCrossVolumeMoveSourceDeletePrimitive());
    }

    private static async Task AssertReadyOnDifferentVolumesAsync(FileOperationPlan plan)
    {
        var validation = await new WindowsFileOperationExecutionValidator().ValidateAsync(plan);
        Assert.IsTrue(validation.CanBeginMutation, validation.Summary);
        Assert.IsTrue(validation.SourceDirectory.Identity.HasValue);
        Assert.IsTrue(validation.DestinationDirectory.Identity.HasValue);
        Assert.AreNotEqual(
            validation.SourceDirectory.Identity.Value.VolumeSerialNumber,
            validation.DestinationDirectory.Identity.Value.VolumeSerialNumber,
            "The opt-in cross-volume source-preflight roots resolved to the same filesystem volume.");
    }

    private static string DestinationPath(NativeFixture fixture, string sourcePath) =>
        Path.Combine(fixture.DestinationDirectory, Path.GetFileName(sourcePath));

    private static FileOperationPlan CreatePlan(
        string sourceDirectory,
        string destinationDirectory,
        string sourcePath)
    {
        var entry = new FileOperationEntry(sourcePath, Path.GetFileName(sourcePath), IsDirectory: false);
        return new FileOperationPlan(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            FileOperationKind.Move,
            FileOperationCollisionPolicy.Stop,
            new FileOperationIntent(
                "CrossVolumePreflightSource",
                Guid.NewGuid(),
                sourceDirectory,
                new[] { entry },
                "CrossVolumePreflightDestination",
                Guid.NewGuid(),
                destinationDirectory));
    }

    private static NativeFixture CreateFixture()
    {
        var sourceRoot = Environment.GetEnvironmentVariable(SourceRootVariable);
        var destinationRoot = Environment.GetEnvironmentVariable(DestinationRootVariable);
        if (string.IsNullOrWhiteSpace(sourceRoot) || string.IsNullOrWhiteSpace(destinationRoot))
        {
            Assert.Inconclusive(
                $"Set {SourceRootVariable} and {DestinationRootVariable} to writable directories on different volumes to run opt-in cross-volume Move source-preflight native tests.");
        }

        return new NativeFixture(
            Path.GetFullPath(sourceRoot!),
            Path.GetFullPath(destinationRoot!));
    }

    private static void SetExtendedAttribute(string path, string name, string value)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        var valueBytes = Encoding.UTF8.GetBytes(value);
        if (nameBytes.Length is 0 or > 254 || valueBytes.Length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(name));
        }

        var buffer = new byte[checked(8 + nameBytes.Length + 1 + valueBytes.Length)];
        buffer[4] = 0;
        buffer[5] = checked((byte)nameBytes.Length);
        buffer[6] = checked((byte)(valueBytes.Length & 0xFF));
        buffer[7] = checked((byte)(valueBytes.Length >> 8));
        Buffer.BlockCopy(nameBytes, 0, buffer, 8, nameBytes.Length);
        Buffer.BlockCopy(valueBytes, 0, buffer, 8 + nameBytes.Length + 1, valueBytes.Length);

        using var handle = CreateFileW(
            path,
            FileWriteEa | Synchronize,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            0,
            IntPtr.Zero);
        var openError = Marshal.GetLastWin32Error();
        if (handle.IsInvalid)
        {
            throw new IOException(
                $"Opening source preflight EA test file failed with Win32 error {openError}.");
        }

        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var status = NtSetEaFile(
                handle,
                out _,
                pinned.AddrOfPinnedObject(),
                checked((uint)buffer.Length));
            if (status < 0)
            {
                throw new IOException(
                    $"NtSetEaFile for the source preflight EA regression failed with NTSTATUS 0x{unchecked((uint)status):X8}. The explicit native matrix requires a source filesystem that supports EAs.");
            }
        }
        finally
        {
            pinned.Free();
        }
    }

    private sealed class NativeFixture : IDisposable
    {
        private readonly string _historyDirectory;

        public NativeFixture(string sourceRoot, string destinationRoot)
        {
            var suffix = Guid.NewGuid().ToString("N");
            SourceDirectory = Path.Combine(sourceRoot, "FileOp.CrossVolumeMovePreflightNative", suffix);
            DestinationDirectory = Path.Combine(destinationRoot, "FileOp.CrossVolumeMovePreflightNative", suffix);
            Directory.CreateDirectory(SourceDirectory);
            Directory.CreateDirectory(DestinationDirectory);

            _historyDirectory = Path.Combine(
                Path.GetTempPath(),
                "FileOp.CrossVolumeMovePreflightNative.History",
                suffix);
            Directory.CreateDirectory(_historyDirectory);
            HistoryDatabasePath = Path.Combine(_historyDirectory, "history.db");
        }

        public string SourceDirectory { get; }
        public string DestinationDirectory { get; }
        public string HistoryDatabasePath { get; }

        public string CreateSourceFile(string name, string contents)
        {
            var path = Path.Combine(SourceDirectory, name);
            File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            TryDeleteDirectory(SourceDirectory);
            TryDeleteDirectory(DestinationDirectory);
            TryDeleteDirectory(_historyDirectory);
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
    }

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        ExactSpelling = true,
        CallingConvention = CallingConvention.Winapi)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport(
        "ntdll.dll",
        ExactSpelling = true,
        CallingConvention = CallingConvention.Winapi)]
    private static extern int NtSetEaFile(
        SafeFileHandle FileHandle,
        out IoStatusBlock IoStatusBlock,
        IntPtr Buffer,
        uint Length);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }
}
