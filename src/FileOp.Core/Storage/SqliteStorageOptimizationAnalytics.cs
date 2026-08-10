using Microsoft.Data.Sqlite;

namespace FileOp.Core.Storage;

public sealed class SqliteStorageOptimizationAnalytics : IStorageOptimizationAnalytics
{
    private const int MaximumResultLimit = 4_096;
    private readonly string _connectionString;

    public SqliteStorageOptimizationAnalytics(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 5,
        }.ToString();
    }

    public async ValueTask<StorageOptimizationAnalysis> AnalyzeOptimizationAsync(
        string rootPath,
        StorageOptimizationPolicy? policy = null,
        DateTimeOffset? asOfUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var effectivePolicy = policy ?? StorageOptimizationPolicy.Default;
        ValidatePolicy(effectivePolicy);
        var effectiveAsOf = (asOfUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var normalizedRoot = NormalizeIndexedPath(rootPath);
        var staleBefore = effectiveAsOf.AddDays(-effectivePolicy.StaleAgeDays);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA query_only = ON;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var largest = await ReadFileCandidatesAsync(
            connection,
            normalizedRoot,
            effectivePolicy.LargeFileMinimumBytes,
            staleBefore: null,
            effectivePolicy.MaxLargeFiles,
            cancellationToken).ConfigureAwait(false);
        var stale = await ReadFileCandidatesAsync(
            connection,
            normalizedRoot,
            effectivePolicy.LargeFileMinimumBytes,
            staleBefore,
            effectivePolicy.MaxStaleLargeFiles,
            cancellationToken).ConfigureAwait(false);
        var sameSize = await ReadSameSizeGroupsAsync(
            connection,
            normalizedRoot,
            effectivePolicy,
            cancellationToken).ConfigureAwait(false);

        return new StorageOptimizationAnalysis(
            normalizedRoot,
            effectiveAsOf,
            effectivePolicy,
            largest,
            stale,
            sameSize);
    }

    private static async ValueTask<IReadOnlyList<StorageOptimizationFileCandidate>> ReadFileCandidatesAsync(
        SqliteConnection connection,
        string rootPath,
        long minimumMeasuredBytes,
        DateTimeOffset? staleBefore,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = CommonScopeSql + """
            SELECT
                path,
                name,
                extension,
                length,
                allocated_length,
                last_write_utc_ticks
            FROM physical_rows
            WHERE is_directory = 0
              AND physical_rank = 1
              AND COALESCE(allocated_length, length) >= @minimum_bytes
              AND (@stale_before_ticks IS NULL OR last_write_utc_ticks < @stale_before_ticks)
            ORDER BY
                COALESCE(allocated_length, length) DESC,
                length DESC,
                last_write_utc_ticks ASC,
                path_norm ASC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@root", rootPath);
        command.Parameters.AddWithValue("@minimum_bytes", minimumMeasuredBytes);
        command.Parameters.AddWithValue(
            "@stale_before_ticks",
            staleBefore is { } cutoff ? cutoff.UtcDateTime.Ticks : DBNull.Value);
        command.Parameters.AddWithValue("@limit", limit);

        var result = new List<StorageOptimizationFileCandidate>(Math.Min(limit, 128));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var extension = reader.GetString(2);
            result.Add(new StorageOptimizationFileCandidate(
                reader.GetString(0),
                reader.GetString(1),
                extension,
                StorageFileCategoryClassifier.Classify(extension),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                FromUtcTicks(reader.GetInt64(5))));
        }

        return result;
    }

    private static async ValueTask<IReadOnlyList<StorageSameSizeCandidateGroup>> ReadSameSizeGroupsAsync(
        SqliteConnection connection,
        string rootPath,
        StorageOptimizationPolicy policy,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = CommonScopeSql + """
            , duplicate_sizes AS (
                SELECT
                    length,
                    COUNT(*) AS candidate_count
                FROM physical_rows
                WHERE is_directory = 0
                  AND physical_rank = 1
                  AND length >= @minimum_same_size_bytes
                GROUP BY length
                HAVING COUNT(*) >= 2
            ),
            ranked_sizes AS (
                SELECT
                    length,
                    candidate_count,
                    ROW_NUMBER() OVER (
                        ORDER BY
                            length * (candidate_count - 1) DESC,
                            length DESC
                    ) AS group_rank
                FROM duplicate_sizes
            ),
            members AS (
                SELECT
                    ranked_sizes.group_rank,
                    ranked_sizes.length,
                    ranked_sizes.candidate_count,
                    physical_rows.path,
                    physical_rows.path_norm,
                    physical_rows.name,
                    physical_rows.extension,
                    physical_rows.allocated_length,
                    physical_rows.last_write_utc_ticks,
                    ROW_NUMBER() OVER (
                        PARTITION BY ranked_sizes.group_rank
                        ORDER BY physical_rows.path_norm
                    ) AS member_rank
                FROM ranked_sizes
                JOIN physical_rows ON
                    physical_rows.length = ranked_sizes.length AND
                    physical_rows.is_directory = 0 AND
                    physical_rows.physical_rank = 1
                WHERE ranked_sizes.group_rank <= @group_limit
            )
            SELECT
                group_rank,
                length,
                candidate_count,
                path,
                name,
                extension,
                allocated_length,
                last_write_utc_ticks
            FROM members
            WHERE member_rank <= @member_limit
            ORDER BY group_rank, member_rank;
            """;
        command.Parameters.AddWithValue("@root", rootPath);
        command.Parameters.AddWithValue("@minimum_same_size_bytes", policy.SameSizeMinimumBytes);
        command.Parameters.AddWithValue("@group_limit", policy.MaxSameSizeGroups);
        command.Parameters.AddWithValue("@member_limit", policy.MaxFilesPerSameSizeGroup);

        var groups = new List<StorageSameSizeCandidateGroup>(Math.Min(policy.MaxSameSizeGroups, 64));
        var currentRank = -1L;
        long currentLength = 0;
        int currentCount = 0;
        List<StorageSameSizeCandidateFile>? members = null;

        void FinishGroup()
        {
            if (members is null)
            {
                return;
            }

            groups.Add(new StorageSameSizeCandidateGroup(
                currentLength,
                currentCount,
                CalculatePotentialLogicalSavingsUpperBound(currentLength, currentCount),
                members.ToArray()));
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var rank = reader.GetInt64(0);
            if (rank != currentRank)
            {
                FinishGroup();
                currentRank = rank;
                currentLength = reader.GetInt64(1);
                currentCount = CheckedCount(reader.GetInt64(2));
                members = new List<StorageSameSizeCandidateFile>(policy.MaxFilesPerSameSizeGroup);
            }

            var extension = reader.GetString(5);
            members!.Add(new StorageSameSizeCandidateFile(
                reader.GetString(3),
                reader.GetString(4),
                extension,
                StorageFileCategoryClassifier.Classify(extension),
                currentLength,
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                FromUtcTicks(reader.GetInt64(7))));
        }

        FinishGroup();
        return groups;
    }

    private const string CommonScopeSql = """
        WITH RECURSIVE tree(
            path,
            path_norm,
            name,
            extension,
            is_directory,
            length,
            allocated_length,
            last_write_utc_ticks,
            volume_serial,
            file_reference
        ) AS (
            SELECT
                path,
                path_norm,
                name,
                extension,
                is_directory,
                length,
                allocated_length,
                last_write_utc_ticks,
                volume_serial,
                file_reference
            FROM files
            WHERE parent_path = @root COLLATE NOCASE

            UNION ALL

            SELECT
                child.path,
                child.path_norm,
                child.name,
                child.extension,
                child.is_directory,
                child.length,
                child.allocated_length,
                child.last_write_utc_ticks,
                child.volume_serial,
                child.file_reference
            FROM files AS child
            JOIN tree ON child.parent_path = tree.path COLLATE NOCASE
        ),
        physical_rows AS (
            SELECT
                tree.*,
                CASE
                    WHEN is_directory = 1 THEN 1
                    ELSE ROW_NUMBER() OVER (
                        PARTITION BY
                            volume_serial,
                            file_reference,
                            CASE
                                WHEN volume_serial IS NULL OR file_reference IS NULL THEN path_norm
                                ELSE ''
                            END
                        ORDER BY path_norm
                    )
                END AS physical_rank
            FROM tree
        )
        """;

    private static void ValidatePolicy(StorageOptimizationPolicy policy)
    {
        if (policy.LargeFileMinimumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Large-file minimum bytes must be positive.");
        }

        if (policy.SameSizeMinimumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Same-size minimum bytes must be positive.");
        }

        if (policy.StaleAgeDays <= 0 || policy.StaleAgeDays > 36_500)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Stale age must be between 1 and 36500 days.");
        }

        ValidateLimit(policy.MaxLargeFiles, nameof(policy.MaxLargeFiles));
        ValidateLimit(policy.MaxStaleLargeFiles, nameof(policy.MaxStaleLargeFiles));
        ValidateLimit(policy.MaxSameSizeGroups, nameof(policy.MaxSameSizeGroups));
        ValidateLimit(policy.MaxFilesPerSameSizeGroup, nameof(policy.MaxFilesPerSameSizeGroup));
    }

    private static void ValidateLimit(int value, string name)
    {
        if (value <= 0 || value > MaximumResultLimit)
        {
            throw new ArgumentOutOfRangeException(
                name,
                value,
                $"Storage optimization limits must be between 1 and {MaximumResultLimit}.");
        }
    }

    private static long CalculatePotentialLogicalSavingsUpperBound(long bytesPerFile, int candidateCount)
    {
        if (bytesPerFile <= 0 || candidateCount <= 1)
        {
            return 0;
        }

        var copies = candidateCount - 1L;
        return bytesPerFile > long.MaxValue / copies
            ? long.MaxValue
            : bytesPerFile * copies;
    }

    private static int CheckedCount(long value)
    {
        if (value < 0 || value > int.MaxValue)
        {
            throw new InvalidDataException($"Storage optimization count {value} is outside the supported range.");
        }

        return checked((int)value);
    }

    private static DateTimeOffset FromUtcTicks(long ticks) =>
        new(new DateTime(ticks, DateTimeKind.Utc));

    private static string NormalizeIndexedPath(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.Length == 3 &&
            char.IsLetter(trimmed[0]) &&
            trimmed[1] == ':' &&
            (trimmed[2] == '\\' || trimmed[2] == '/'))
        {
            return $"{char.ToUpperInvariant(trimmed[0])}:\\";
        }

        trimmed = trimmed.TrimEnd('\\', '/');
        return string.IsNullOrEmpty(trimmed) ? path : trimmed;
    }
}
