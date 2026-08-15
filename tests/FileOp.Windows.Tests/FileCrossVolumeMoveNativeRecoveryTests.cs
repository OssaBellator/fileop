using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FileOp.Core.Models;
using FileOp.Core.Operations;
using FileOp.Windows.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace FileOp.Windows.Tests;

/// <summary>
/// Opt-in native recovery regressions for the dormant cross-volume Move engine.
/// These tests use real Copy/history/raw source-delete leases. Evidence-only verifier
/// injection is used either to force a deterministic refusal or to introduce a real
/// source metadata change immediately before the real second fidelity proof.
/// </summary>
[TestClass]
public sealed class FileCrossVolumeMoveNativeRecoveryTests
{
    private const string SourceRootVariable = "FILEOP_CROSS_VOLUME_MOVE_SOURCE_ROOT";
    private const string DestinationRootVariable = "FILEOP_CROSS_VOLUME_MOVE_DESTINATION_ROOT";
    private const uint FileWriteEa = 0x00000010u;
    private const uint Synchronize = 0x00100000u;

    [TestMethod]
    [TestCategory("CrossVolumeMoveNative")]
    public async Task PostBarrierFidelityRefusalWithRealLeaseRequiresRecoveryAndRetainsBothFiles()
    {
        using var fixture = CreateFixture();
        var sourcePath = CreateSourceFile(fixture, "post-barrier-refusal.txt", "post-barrier recovery payload");
        var plan = CreatePlan(fixture.SourceDirectory, fixture.DestinationDirectory, sourcePath);
        await AssertReadyOnDifferentVolumesAsync(plan);

        using var history = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.HistoryDatabasePath);
        var verifier = new SequencedFidelityVerifier(
            Allowed(),
            Blocked(FileCrossVolumeMoveFidelityBlocker.SourceExtendedAttributes));
        var result = await ExecuteWithVerifierAsync(plan, history, verifier);

        await AssertRecoveryWithBothFilesAsync(
            result,
            history,
            plan,
            sourcePath,
            fixture.DestinationDirectory,
            expectedFailureEvidence: null);
        Assert.AreEqual(2, verifier.CallCount);
    }

    [TestMethod]
    [TestCategory("CrossVolumeMoveNative")]
    public async Task PostBarrierSourceNamedStreamIsDetectedByRealSecondProofAndRequiresRecovery()
    {
        using var fixture = CreateFixture();
        var sourcePath = CreateSourceFile(fixture, "post-barrier-ads.txt", "post-barrier ads payload");
        var plan = CreatePlan(fixture.SourceDirectory, fixture.DestinationDirectory, sourcePath);
        await AssertReadyOnDifferentVolumesAsync(plan);

        using var history = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.HistoryDatabasePath);
        var verifier = new MutateOnSecondProofFidelityVerifier(path =>
            File.WriteAllText(path + ":fileop-post-barrier", "post-barrier stream"));
        var result = await ExecuteWithVerifierAsync(plan, history, verifier);

        await AssertRecoveryWithBothFilesAsync(
            result,
            history,
            plan,
            sourcePath,
            fixture.DestinationDirectory,
            FileCrossVolumeMoveFidelityBlocker.SourceNamedDataStreams.ToString());
        Assert.AreEqual(2, verifier.CallCount);
        Assert.AreEqual("post-barrier stream", File.ReadAllText(sourcePath + ":fileop-post-barrier"));
    }

    [TestMethod]
    [TestCategory("CrossVolumeMoveNative")]
    public async Task PostBarrierSourceExtendedAttributeIsDetectedByRealSecondProofAndRequiresRecovery()
    {
        using var fixture = CreateFixture();
        var sourcePath = CreateSourceFile(fixture, "post-barrier-ea.txt", "post-barrier ea payload");
        var plan = CreatePlan(fixture.SourceDirectory, fixture.DestinationDirectory, sourcePath);
        await AssertReadyOnDifferentVolumesAsync(plan);

        using var history = new SqliteFileCrossVolumeMoveActionHistoryStore(fixture.HistoryDatabasePath);
        var verifier = new MutateOnSecondProofFidelityVerifier(path =>
            SetExtendedAttribute(path, "FileOpPostBarrier", "post-barrier ea"));
        var result = await ExecuteWithVerifierAsync(plan, history, verifier);

        await AssertRecoveryWithBothFilesAsync(
            result,
            history,
            plan,
            sourcePath,
            fixture.DestinationDirectory,
            FileCrossVolumeMoveFidelityBlocker.SourceExtendedAttributes.ToString());
        Assert.AreEqual(2, verifier.CallCount);
    }

    private static async Task<FileOperationExecutionSnapshot> ExecuteWithVerifierAsync(
        FileOperationPlan plan,
        IFileCrossVolumeMoveActionHistoryStore history,
        IFileCrossVolumeMoveFidelityVerifier verifier)
    {
        var sourceDelete = new WindowsFidelityVerifiedFileCrossVolumeMoveSourceDeletePrimitive(
            new WindowsFileCrossVolumeMoveSourceDeletePrimitive(),
            verifier);
        var executor = new FileCrossVolumeMoveOperationExecutor(
            new WindowsFileOperationExecutionValidator(),
            history,
            new WindowsFileCopyMutationPrimitive(),
            sourceDelete);
        return await executor.ExecuteAsync(plan);
    }

    private static async Task AssertRecoveryWithBothFilesAsync(
        FileOperationExecutionSnapshot result,
        SqliteFileCrossVolumeMoveActionHistoryStore history,
        FileOperationPlan plan,
        string sourcePath,
        string destinationDirectory,
        string? expectedFailureEvidence)
    {
        Assert.AreEqual(FileOperationExecutionState.Failed, result.State);
        Assert.AreEqual("CrossVolumeMoveSourceDeleteFailed", result.Failure?.Code);
        if (!string.IsNullOrWhiteSpace(expectedFailureEvidence))
        {
            StringAssert.Contains(result.Failure?.Message ?? string.Empty, expectedFailureEvidence);
        }

        var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(sourcePath));
        Assert.IsTrue(
            File.Exists(sourcePath),
            "A post-barrier fidelity refusal must not invoke the raw source-delete mutation.");
        Assert.IsTrue(
            File.Exists(destinationPath),
            "The already committed destination Copy remains present when post-barrier proof refuses deletion.");
        Assert.AreEqual(File.ReadAllText(sourcePath), File.ReadAllText(destinationPath));

        var persisted = await history.GetAsync(plan.Id);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(FileCrossVolumeMoveTerminalState.RecoveryRequired, persisted.TerminalState);
        Assert.AreEqual(FileCrossVolumeMoveEntryState.RecoveryRequired, persisted.Entries[0].State);
        Assert.IsNotNull(persisted.Entries[0].CopyMutationStartedAtUtc);
        Assert.IsNotNull(persisted.Entries[0].DestinationCommittedAtUtc);
        Assert.IsNotNull(persisted.Entries[0].SourceDeleteStartedAtUtc);
        Assert.IsTrue(persisted.RequiresRecovery);
        Assert.IsTrue(persisted.Entries[0].DestinationIsDurablyCommitted);
        Assert.IsTrue(persisted.Entries[0].HasDestinationRecoveryEvidence);
        Assert.IsFalse(
            persisted.HasRetainedSourceDuplicates,
            "Generic RecoveryRequired history must not claim a known retained duplicate even when this deterministic test directly observes both paths.");
    }

    private static string CreateSourceFile(NativeFixture fixture, string name, string contents)
    {
        var path = Path.Combine(fixture.SourceDirectory, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private static FileCrossVolumeMoveFidelityClassification Allowed() =>
        new(
            CanDeleteSourceAfterDurableBarrier: true,
            Array.Empty<FileCrossVolumeMoveFidelityBlocker>(),
            "native recovery fixture allowed first proof");

    private static FileCrossVolumeMoveFidelityClassification Blocked(
        FileCrossVolumeMoveFidelityBlocker blocker) =>
        new(
            CanDeleteSourceAfterDurableBarrier: false,
            new[] { blocker },
            "native recovery fixture rejected second proof");

    private static async Task AssertReadyOnDifferentVolumesAsync(FileOperationPlan plan)
    {
        var validation = await new WindowsFileOperationExecutionValidator().ValidateAsync(plan);
        Assert.IsTrue(validation.CanBeginMutation, validation.Summary);
        Assert.IsTrue(validation.SourceDirectory.Identity.HasValue);
        Assert.IsTrue(validation.DestinationDirectory.Identity.HasValue);
        Assert.AreNotEqual(
            validation.SourceDirectory.Identity.Value.VolumeSerialNumber,
            validation.DestinationDirectory.Identity.Value.VolumeSerialNumber,
            "The opt-in cross-volume recovery roots resolved to the same filesystem volume.");
    }

    private static FileOperationPlan CreatePlan(
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
            FileOperationKind.Move,
            FileOperationCollisionPolicy.Stop,
            new FileOperationIntent(
                "CrossVolumeNativeRecoverySource",
                Guid.NewGuid(),
                sourceDirectory,
                new[] { entry },
                "CrossVolumeNativeRecoveryDestination",
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
                $"Opening post-barrier source EA file failed with Win32 error {openError}.");
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
                    $"NtSetEaFile for the post-barrier EA regression failed with NTSTATUS 0x{unchecked((uint)status):X8}.");
            }
        }
        finally
        {
            pinned.Free();
        }
    }

    private sealed class SequencedFidelityVerifier : IFileCrossVolumeMoveFidelityVerifier
    {
        private readonly Queue<FileCrossVolumeMoveFidelityClassification> _results;

        public SequencedFidelityVerifier(params FileCrossVolumeMoveFidelityClassification[] results) =>
            _results = new Queue<FileCrossVolumeMoveFidelityClassification>(results);

        public int CallCount { get; private set; }

        public ValueTask<FileCrossVolumeMoveFidelityClassification> VerifyAsync(
            FileCrossVolumeMoveSourceDeleteRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            if (_results.Count == 0)
            {
                throw new InvalidOperationException("No native recovery fidelity result remains.");
            }

            return ValueTask.FromResult(_results.Dequeue());
        }
    }

    private sealed class MutateOnSecondProofFidelityVerifier : IFileCrossVolumeMoveFidelityVerifier
    {
        private readonly Action<string> _mutateSource;
        private readonly WindowsFileCrossVolumeMoveFidelityVerifier _inner = new();

        public MutateOnSecondProofFidelityVerifier(Action<string> mutateSource) =>
            _mutateSource = mutateSource ?? throw new ArgumentNullException(nameof(mutateSource));

        public int CallCount { get; private set; }

        public async ValueTask<FileCrossVolumeMoveFidelityClassification> VerifyAsync(
            FileCrossVolumeMoveSourceDeleteRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            if (CallCount == 2)
            {
                _mutateSource(request.CanonicalSourcePath);
            }
            else if (CallCount > 2)
            {
                throw new InvalidOperationException("Unexpected extra native recovery fidelity proof.");
            }

            return await _inner.VerifyAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class NativeFixture : IDisposable
    {
        private readonly string _historyDirectory;

        public NativeFixture(string sourceRoot, string destinationRoot)
        {
            var suffix = Guid.NewGuid().ToString("N");
            SourceDirectory = Path.Combine(
                sourceRoot,
                "FileOp.CrossVolumeMoveNativeRecovery",
                suffix);
            DestinationDirectory = Path.Combine(
                destinationRoot,
                "FileOp.CrossVolumeMoveNativeRecovery",
                suffix);
            Directory.CreateDirectory(SourceDirectory);
            Directory.CreateDirectory(DestinationDirectory);

            _historyDirectory = Path.Combine(
                Path.GetTempPath(),
                "FileOp.CrossVolumeMoveNativeRecovery.History",
                suffix);
            Directory.CreateDirectory(_historyDirectory);
            HistoryDatabasePath = Path.Combine(_historyDirectory, "history.db");
        }

        public string SourceDirectory { get; }

        public string DestinationDirectory { get; }

        public string HistoryDatabasePath { get; }

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