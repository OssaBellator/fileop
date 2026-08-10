using System.Buffers;
using System.Security.Cryptography;
using FileOp.Core.Storage;

namespace FileOp.Windows.Storage;

public sealed class WindowsSameSizeContentVerifier
{
    private readonly Func<DateTimeOffset> _utcNow;

    public WindowsSameSizeContentVerifier(Func<DateTimeOffset>? utcNow = null)
    {
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
    }

    public async ValueTask<StorageSameSizeContentVerification> VerifyAsync(
        StorageSameSizeCandidateGroup group,
        StorageSameSizeContentVerificationPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        cancellationToken.ThrowIfCancellationRequested();
        policy ??= StorageSameSizeContentVerificationPolicy.Default;
        policy.Validate();
        ValidateGroup(group);

        var maxByBytes = group.LogicalBytesPerFile == 0
            ? policy.MaxFiles
            : Math.Min(
                policy.MaxFiles,
                checked((int)Math.Min(
                    int.MaxValue,
                    policy.MaxTotalBytesRead / group.LogicalBytesPerFile)));
        var selectedCount = Math.Min(group.SampleFiles.Count, maxByBytes);
        if (selectedCount < 2)
        {
            return new StorageSameSizeContentVerification(
                StorageSameSizeContentVerificationStatus.BudgetLimited,
                _utcNow(),
                group.LogicalBytesPerFile,
                group.CandidateFileCount,
                group.SampleFiles.Count,
                SelectedFileCount: 0,
                FullyHashedFileCount: 0,
                BytesRead: 0,
                policy.MaxTotalBytesRead,
                MatchingSets: Array.Empty<StorageVerifiedContentMatchSet>(),
                Detail:
                    $"Full SHA-256 verification of even two {group.LogicalBytesPerFile:N0}-byte files would exceed the {policy.MaxTotalBytesRead:N0}-byte read budget. No content was read and no duplicate claim was made.");
        }

        var selected = group.SampleFiles.Take(selectedCount).ToArray();
        var streams = new List<FileStream>(selected.Length);
        long bytesRead = 0;
        var fullyHashed = 0;
        try
        {
            foreach (var candidate in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stream = new FileStream(
                    candidate.Path,
                    new FileStreamOptions
                    {
                        Mode = FileMode.Open,
                        Access = FileAccess.Read,
                        Share = FileShare.Read,
                        BufferSize = policy.BufferSizeBytes,
                        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                    });
                streams.Add(stream);

                if (stream.Length != group.LogicalBytesPerFile)
                {
                    return new StorageSameSizeContentVerification(
                        StorageSameSizeContentVerificationStatus.CandidateChanged,
                        _utcNow(),
                        group.LogicalBytesPerFile,
                        group.CandidateFileCount,
                        group.SampleFiles.Count,
                        selectedCount,
                        FullyHashedFileCount: 0,
                        BytesRead: 0,
                        policy.MaxTotalBytesRead,
                        MatchingSets: Array.Empty<StorageVerifiedContentMatchSet>(),
                        Detail:
                            $"{candidate.Path} is now {stream.Length:N0} bytes instead of the indexed {group.LogicalBytesPerFile:N0} bytes. Refresh Optimize before verifying this group again.");
                }
            }

            var digestGroups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var buffer = ArrayPool<byte>.Shared.Rent(policy.BufferSizeBytes);
            try
            {
                for (var index = 0; index < streams.Count; index++)
                {
                    var stream = streams[index];
                    var digest = await HashStreamAsync(
                        stream,
                        buffer,
                        policy.BufferSizeBytes,
                        cancellationToken,
                        read => bytesRead = AddWithinBudget(
                            bytesRead,
                            read,
                            policy.MaxTotalBytesRead)).ConfigureAwait(false);
                    fullyHashed++;

                    if (stream.Length != group.LogicalBytesPerFile)
                    {
                        return new StorageSameSizeContentVerification(
                            StorageSameSizeContentVerificationStatus.CandidateChanged,
                            _utcNow(),
                            group.LogicalBytesPerFile,
                            group.CandidateFileCount,
                            group.SampleFiles.Count,
                            selectedCount,
                            fullyHashed,
                            bytesRead,
                            policy.MaxTotalBytesRead,
                            MatchingSets: Array.Empty<StorageVerifiedContentMatchSet>(),
                            Detail:
                                $"{selected[index].Path} changed length while verification was active. The partial hash evidence was discarded; refresh Optimize before retrying.");
                    }

                    var key = Convert.ToHexString(digest);
                    if (!digestGroups.TryGetValue(key, out var paths))
                    {
                        paths = [];
                        digestGroups.Add(key, paths);
                    }
                    paths.Add(selected[index].Path);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
            }

            var matchingSets = digestGroups.Values
                .Where(static paths => paths.Count >= 2)
                .OrderByDescending(static paths => paths.Count)
                .ThenBy(static paths => paths[0], StringComparer.OrdinalIgnoreCase)
                .Select(static paths => new StorageVerifiedContentMatchSet(paths.ToArray()))
                .ToArray();
            var matchingFileCount = matchingSets.Sum(static set => set.FileCount);
            return new StorageSameSizeContentVerification(
                StorageSameSizeContentVerificationStatus.Completed,
                _utcNow(),
                group.LogicalBytesPerFile,
                group.CandidateFileCount,
                group.SampleFiles.Count,
                selectedCount,
                fullyHashed,
                bytesRead,
                policy.MaxTotalBytesRead,
                matchingSets,
                Detail:
                    $"Fully SHA-256 hashed {fullyHashed:N0} sampled file(s) under read-only handles; {matchingFileCount:N0} file(s) belong to {matchingSets.Length:N0} matching set(s). " +
                    $"Read {bytesRead:N0} byte(s). Hash matches are verified logical duplicate evidence only for the selected sampled paths; physical reclaimable space is not verified here.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            NotSupportedException or
            PathTooLongException)
        {
            return new StorageSameSizeContentVerification(
                StorageSameSizeContentVerificationStatus.Unavailable,
                _utcNow(),
                group.LogicalBytesPerFile,
                group.CandidateFileCount,
                group.SampleFiles.Count,
                selectedCount,
                fullyHashed,
                bytesRead,
                policy.MaxTotalBytesRead,
                MatchingSets: Array.Empty<StorageVerifiedContentMatchSet>(),
                Detail:
                    $"Content verification could not keep the selected files readable and stable: {exception.Message}. Partial hash evidence was discarded.");
        }
        finally
        {
            foreach (var stream in streams)
            {
                stream.Dispose();
            }
        }
    }

    private static async ValueTask<byte[]> HashStreamAsync(
        FileStream stream,
        byte[] buffer,
        int bufferSize,
        CancellationToken cancellationToken,
        Action<int> recordRead)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        stream.Position = 0;
        while (true)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(0, bufferSize),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return hash.GetHashAndReset();
            }

            recordRead(read);
            hash.AppendData(buffer, 0, read);
        }
    }

    private static long AddWithinBudget(long current, int read, long budget)
    {
        if (read < 0 || current < 0 || current > budget - read)
        {
            throw new InvalidDataException(
                "Same-size content verification exceeded its precomputed read budget.");
        }

        return current + read;
    }

    private static void ValidateGroup(StorageSameSizeCandidateGroup group)
    {
        if (group.LogicalBytesPerFile <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(group),
                "Same-size content verification requires a positive logical file length.");
        }
        if (group.CandidateFileCount < 2 || group.SampleFiles.Count < 2)
        {
            throw new ArgumentException(
                "Same-size content verification requires at least two candidate and sampled files.",
                nameof(group));
        }
        if (group.SampleFiles.Count > group.CandidateFileCount)
        {
            throw new ArgumentException(
                "Sampled files cannot exceed the candidate-file count.",
                nameof(group));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in group.SampleFiles)
        {
            if (string.IsNullOrWhiteSpace(file.Path))
            {
                throw new ArgumentException("Sampled candidate paths must be present.", nameof(group));
            }
            if (file.LogicalBytes != group.LogicalBytesPerFile)
            {
                throw new ArgumentException(
                    "Every sampled candidate must retain the group's indexed logical length.",
                    nameof(group));
            }
            if (!seen.Add(Path.GetFullPath(file.Path)))
            {
                throw new ArgumentException(
                    "Same-size content verification samples must have distinct paths.",
                    nameof(group));
            }
        }
    }
}
