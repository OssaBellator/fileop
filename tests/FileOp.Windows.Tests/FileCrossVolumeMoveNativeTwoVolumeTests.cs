using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace FileOp.Windows.Tests;

/// <summary>
/// Opt-in native tests for the dormant cross-volume Move engine. These tests bypass the
/// production WindowsMoveOperationExecutionValidator product block deliberately, but they
/// retain the repository's NTFS-only mutation identity boundary by wrapping the ordinary
/// execution-grade validator with WindowsNtfsMutationExecutionValidator.
///
/// Set FILEOP_CROSS_VOLUME_MOVE_SOURCE_ROOT and FILEOP_CROSS_VOLUME_MOVE_DESTINATION_ROOT to
/// writable ordinary directories on different filesystem volumes. The dedicated local gate
/// in tools/test-cross-volume-move-native.ps1 requires those roots explicitly.
/// </summary>
[TestClass]
public sealed class FileCrossVolumeMoveNativeTwoVolumeTests
{
    private const string SourceRootVariable = "FILEOP_CROSS_VOLUME_MOVE_SOURCE_ROOT";
    private const string DestinationRootVariable = "FILEOP_CROSS_VOLUME_MOVE_DESTINATION_ROOT";
    private const uint FileWriteEa = 0x00000010u;
    private const uint Synchronize = 0x00100000u;

    [TestMethod]
    [TestCategory("CrossVolumeMoveNative")]
    public async Task RealCompositeMoveCopiesThenDeletesExactSourceEntry()
    {
        using var fixture = CreateFixture();
        var sourcePath = fixture.CreateSourceFile("ordinary.txt", "cross-volume payload");
        var plan = CreatePlan(fixture.SourceDirectory, fixture.DestinationDirectory, sourcePath);
        await AssertReadyOnDifferentVolumesAsync(plan);

        using var history = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.HistoryDatabasePath);
        var executor = CreateRealExecutor(history);

        var result = await executor.ExecuteAsync(plan);

        Assert.AreEqual(FileOperationExecutionState.Succeeded, result.State, result.Failure?.Message);
        Assert.IsFalse(File.Exists(sourcePath), "Successful composite Move must remove the selected source entry.");
        var destinationPath = Path.Combine(fixture.DestinationDirectory, Path.GetFileName(sourcePath));
        Assert.IsTrue(File.Exists(destinationPath));
        Assert.AreEqual("cross-volume payload", File.ReadAllText(destinationPath));

        var persisted = await history.GetAsync(plan.Id);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(FileCrossVolumeMoveTerminalState.Succeeded, persisted.TerminalState);
        Assert.AreEqual(FileCrossVolumeMoveEntryState.Moved, persisted.Entries[0].State);
        Assert.IsFalse(persisted.RequiresRecovery);
    }

    [TestMethod]
    [TestCategory("CrossVolumeMoveNative")]
    public async Task SourceNamedStreamRefusesDestructiveCompletionAndRetainsBothFiles()
    {
        using var fixture = CreateFixture();
        var sourcePath = fixture.CreateSourceFile("with-stream.txt", "main payload");
        File.WriteAllText(sourcePath + ":fileop-test", "named stream payload");
        var plan = CreatePlan(fixture.SourceDirectory, fixture.DestinationDirectory, sourcePath);
        await AssertReadyOnDifferentVolumesAsync(plan);

        using var history = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.HistoryDatabasePath);
        var executor = CreateRealExecutor(history);

        var result = await executor.ExecuteAsync(plan);

        Assert.AreEqual(FileOperationExecutionState.Failed, result.State);
        Assert.AreEqual("CrossVolumeMoveSourceDeletePreparationFailed", result.Failure?.Code);
        Assert.IsTrue(File.Exists(sourcePath), "Unsupported source ADS semantics must retain the source.");
        Assert.AreEqual("named stream payload", File.ReadAllText(sourcePath + ":fileop-test"));
        var destinationPath = Path.Combine(fixture.DestinationDirectory, Path.GetFileName(sourcePath));
        Assert.IsTrue(File.Exists(destinationPath), "The already committed destination Copy should remain explicit.");
        Assert.AreEqual("main payload", File.ReadAllText(destinationPath));

        var persisted = await history.GetAsync(plan.Id);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(FileCrossVolumeMoveTerminalState.Failed, persisted.TerminalState);
        Assert.IsTrue(persisted.HasRetainedSourceDuplicates);
        Assert.IsFalse(persisted.RequiresRecovery);
    }

    [TestMethod]
    [TestCategory("CrossVolumeMoveNative")]
    public async Task SourceExtendedAttributeRefusesDestructiveCompletionAndRetainsBothFiles()
    {
        using var fixture = CreateFixture();
        var sourcePath = fixture.CreateSourceFile("with-ea.txt", "main payload with ea");
        SetExtendedAttribute(sourcePath, "FileOpTest", "extended attribute payload");
        var plan = CreatePlan(fixture.SourceDirectory, fixture.DestinationDirectory, sourcePath);
        await AssertReadyOnDifferentVolumesAsync(plan);

        using var history = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.HistoryDatabasePath);
        var executor = CreateRealExecutor(history);

        var result = await executor.ExecuteAsync(plan);

        Assert.AreEqual(FileOperationExecutionState.Failed, result.State);
        Assert.AreEqual("CrossVolumeMoveSourceDeletePreparationFailed", result.Failure?.Code);
        Assert.IsTrue(File.Exists(sourcePath), "Unsupported source EA semantics must retain the source.");
        var destinationPath = Path.Combine(fixture.DestinationDirectory, Path.GetFileName(sourcePath));
        Assert.IsTrue(File.Exists(destinationPath), "The already committed destination Copy should remain explicit.");
        Assert.AreEqual("main payload with ea", File.ReadAllText(destinationPath));

        var persisted = await history.GetAsync(plan.Id);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(FileCrossVolumeMoveTerminalState.Failed, persisted.TerminalState);
        Assert.IsTrue(persisted.HasRetainedSourceDuplicates);
        Assert.IsFalse(persisted.RequiresRecovery);
    }

    [TestMethod]
    [TestCategory("CrossVolumeMoveNative")]
    public async Task CancellationRequestedByRealCopySettlesAtDestinationCommitted()
    {
        using var fixture = CreateFixture();
        var sourcePath = fixture.CreateSourceFile("cancel-after-copy.txt", new string('x', 64 * 1024));
        var plan = CreatePlan(fixture.SourceDirectory, fixture.DestinationDirectory, sourcePath);
        await AssertReadyOnDifferentVolumesAsync(plan);

        using var history = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.HistoryDatabasePath);
        FileCrossVolumeMoveOperationExecutor? executor = null;
        var copy = new CancellingCopyMutationPrimitive(
            new WindowsFileCopyMutationPrimitive(),
            async () =>
            {
                var active = executor ?? throw new InvalidOperationException("executor not assigned");
                Assert.IsTrue(await active.RequestCancellationAsync(plan.Id));
            });
        executor = new FileCrossVolumeMoveOperationExecutor(
            CreateDirectNtfsValidator(),
            history,
            copy,
            new WindowsFidelityVerifiedFileCrossVolumeMoveSourceDeletePrimitive());

        var result = await executor.ExecuteAsync(plan);

        Assert.AreEqual(FileOperationExecutionState.Cancelled, result.State);
        Assert.IsTrue(File.Exists(sourcePath), "Cancellation after Copy commit must retain the original source.");
        var destinationPath = Path.Combine(fixture.DestinationDirectory, Path.GetFileName(sourcePath));
        Assert.IsTrue(File.Exists(destinationPath), "Cancellation after Copy commit must retain the committed destination.");
        Assert.AreEqual(File.ReadAllText(sourcePath), File.ReadAllText(destinationPath));

        var persisted = await history.GetAsync(plan.Id);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(FileCrossVolumeMoveTerminalState.Cancelled, persisted.TerminalState);
        Assert.AreEqual(FileCrossVolumeMoveEntryState.DestinationCommitted, persisted.Entries[0].State);
        Assert.IsTrue(persisted.HasRetainedSourceDuplicates);
        Assert.IsFalse(persisted.RequiresRecovery);
    }

    [TestMethod]
    [TestCategory("CrossVolumeMoveNative")]
    public async Task MultipleHardLinksMoveOnlySelectedSourceDirectoryEntry()
    {
        using var fixture = CreateFixture();
        var sourcePath = fixture.CreateSourceFile("selected-link.txt", "linked payload");
        var retainedLinkPath = Path.Combine(fixture.SourceDirectory, "retained-link.txt");
        Assert.IsTrue(
            CreateHardLinkW(retainedLinkPath, sourcePath, IntPtr.Zero),
            $"Creating source hard link failed with Win32 error {Marshal.GetLastWin32Error()}.");
        var plan = CreatePlan(fixture.SourceDirectory, fixture.DestinationDirectory, sourcePath);
        await AssertReadyOnDifferentVolumesAsync(plan);

        using var history = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.HistoryDatabasePath);
        var executor = CreateRealExecutor(history);

        var result = await executor.ExecuteAsync(plan);

        Assert.AreEqual(FileOperationExecutionState.Succeeded, result.State, result.Failure?.Message);
        Assert.IsFalse(File.Exists(sourcePath), "Only the selected source directory entry should be unlinked.");
        Assert.IsTrue(File.Exists(retainedLinkPath), "Other source-volume hard links must remain valid.");
        Assert.AreEqual("linked payload", File.ReadAllText(retainedLinkPath));
        var destinationPath = Path.Combine(fixture.DestinationDirectory, Path.GetFileName(sourcePath));
        Assert.IsTrue(File.Exists(destinationPath));
        Assert.AreEqual("linked payload", File.ReadAllText(destinationPath));
    }

    private static FileCrossVolumeMoveOperationExecutor CreateRealExecutor(
        IFileCrossVolumeMoveActionHistoryStore history) =>
        new(
            CreateDirectNtfsValidator(),
            history,
            new WindowsFileCopyMutationPrimitive(),
            new WindowsFidelityVerifiedFileCrossVolumeMoveSourceDeletePrimitive());

    private static IFileOperationExecutionValidator CreateDirectNtfsValidator() =>
        new WindowsNtfsMutationExecutionValidator(
            new WindowsFileOperationExecutionValidator());

    private static async Task AssertReadyOnDifferentVolumesAsync(FileOperationPlan plan)
    {
        var validation = await CreateDirectNtfsValidator().ValidateAsync(plan);
        Assert.IsTrue(validation.CanBeginMutation, validation.Summary);
        Assert.IsTrue(validation.SourceDirectory.Identity.HasValue);
        Assert.IsTrue(validation.DestinationDirectory.Identity.HasValue);
        Assert.AreNotEqual(
            validation.SourceDirectory.Identity.Value.VolumeSerialNumber,
            validation.DestinationDirectory.Identity.Value.VolumeSerialNumber,
            "The opt-in cross-volume test roots resolved to the same filesystem volume.");
    }

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
                "CrossVolumeNativeSource",
                Guid.NewGuid(),
                sourceDirectory,
                new[] { entry },
                "CrossVolumeNativeDestination",
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
                $"Set {SourceRootVariable} and {DestinationRootVariable} to writable directories on different volumes to run opt-in cross-volume Move native tests.");
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
                $"Opening source EA test file failed with Win32 error {openError}.");
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
                    $"NtSetEaFile for the source EA regression failed with NTSTATUS 0x{unchecked((uint)status):X8}. The explicit native matrix requires a source filesystem that supports EAs.");
            }
        }
        finally
        {
            pinned.Free();
        }
    }

    private sealed class CancellingCopyMutationPrimitive : IFileCopyMutationPrimitive
    {
        private readonly IFileCopyMutationPrimitive _inner;
        private readonly Func<ValueTask> _afterCopy;

        public CancellingCopyMutationPrimitive(
            IFileCopyMutationPrimitive inner,
            Func<ValueTask> afterCopy)
        {
            _inner = inner;
            _afterCopy = afterCopy;
        }

        public async ValueTask<IFileCopyMutationLease> CopyNewFileAsync(FileCopyMutationRequest request)
        {
            var lease = await _inner.CopyNewFileAsync(request).ConfigureAwait(false);
            try
            {
                await _afterCopy().ConfigureAwait(false);
                return lease;
            }
            catch
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    private sealed class NativeFixture : IDisposable
    {
        private readonly string _historyDirectory;

        public NativeFixture(string sourceRoot, string destinationRoot)
        {
            var suffix = Guid.NewGuid().ToString("N");
            SourceDirectory = Path.Combine(sourceRoot, "FileOp.CrossVolumeMoveNative", suffix);
            DestinationDirectory = Path.Combine(destinationRoot, "FileOp.CrossVolumeMoveNative", suffix);
            Directory.CreateDirectory(SourceDirectory);
            Directory.CreateDirectory(DestinationDirectory);

            _historyDirectory = Path.Combine(
                Path.GetTempPath(),
                "FileOp.CrossVolumeMoveNative.History",
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
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        ExactSpelling = true,
        CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string lpFileName,
        string lpExistingFileName,
        IntPtr lpSecurityAttributes);

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
