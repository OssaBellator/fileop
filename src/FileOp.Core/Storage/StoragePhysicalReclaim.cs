namespace FileOp.Core.Storage;

public sealed record StoragePhysicalFileIdentity(
    uint VolumeSerialNumber,
    ulong FileIndex);

public sealed record StoragePhysicalFileEvidence(
    string Path,
    StoragePhysicalFileIdentity Identity,
    uint HardLinkCount,
    long AllocatedBytes);

public sealed record StorageVerifiedPhysicalFile(
    StoragePhysicalFileIdentity Identity,
    IReadOnlyList<string> SamplePaths,
    uint HardLinkCount,
    long AllocatedBytes)
{
    public bool IsSingletonLink => HardLinkCount == 1;
}

public sealed record StorageVerifiedPhysicalMatchSet(
    IReadOnlyList<StorageVerifiedPhysicalFile> PhysicalFiles,
    long ReclaimableBytesUpperBound)
{
    public int UniquePhysicalFileCount => PhysicalFiles.Count;

    public int SingletonLinkPhysicalFileCount =>
        PhysicalFiles.Count(static file => file.IsSingletonLink);
}

public enum StoragePhysicalReclaimEvidenceStatus
{
    NotApplicable,
    Verified,
    Unavailable,
}

public sealed record StoragePhysicalReclaimVerification(
    StoragePhysicalReclaimEvidenceStatus Status,
    IReadOnlyList<StorageVerifiedPhysicalMatchSet> MatchingSets,
    long ReclaimableBytesUpperBound,
    string Detail)
{
    public static StoragePhysicalReclaimVerification NotApplicable(string detail) =>
        new(
            StoragePhysicalReclaimEvidenceStatus.NotApplicable,
            Array.Empty<StorageVerifiedPhysicalMatchSet>(),
            0,
            detail);

    public static StoragePhysicalReclaimVerification Unavailable(string detail) =>
        new(
            StoragePhysicalReclaimEvidenceStatus.Unavailable,
            Array.Empty<StorageVerifiedPhysicalMatchSet>(),
            0,
            detail);
}

public static class StoragePhysicalReclaimAnalyzer
{
    public static StoragePhysicalReclaimVerification Analyze(
        IReadOnlyList<StorageVerifiedContentMatchSet> contentMatches,
        IReadOnlyList<StoragePhysicalFileEvidence> physicalEvidence)
    {
        ArgumentNullException.ThrowIfNull(contentMatches);
        ArgumentNullException.ThrowIfNull(physicalEvidence);

        if (contentMatches.Count == 0)
        {
            return StoragePhysicalReclaimVerification.NotApplicable(
                "No SHA-256 matching sampled paths require physical reclaim verification.");
        }

        var evidenceByPath = new Dictionary<string, StoragePhysicalFileEvidence>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var evidence in physicalEvidence)
        {
            if (string.IsNullOrWhiteSpace(evidence.Path) ||
                evidence.HardLinkCount == 0 ||
                evidence.AllocatedBytes < 0)
            {
                return StoragePhysicalReclaimVerification.Unavailable(
                    "Current physical file evidence was incomplete or invalid, so no physical reclaim bytes were inferred.");
            }

            var fullPath = Path.GetFullPath(evidence.Path);
            if (!evidenceByPath.TryAdd(fullPath, evidence with { Path = fullPath }))
            {
                return StoragePhysicalReclaimVerification.Unavailable(
                    "Current physical evidence contained a duplicate sampled path, so no physical reclaim bytes were inferred.");
            }
        }

        var consumedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var consumedIdentities = new HashSet<StoragePhysicalFileIdentity>();
        var verifiedSets = new List<StorageVerifiedPhysicalMatchSet>(contentMatches.Count);
        long totalReclaimable = 0;

        foreach (var contentSet in contentMatches)
        {
            if (contentSet.Paths.Count < 2)
            {
                return StoragePhysicalReclaimVerification.Unavailable(
                    "A content-match set contained fewer than two paths, so no physical reclaim bytes were inferred.");
            }

            var setEvidence = new List<StoragePhysicalFileEvidence>(contentSet.Paths.Count);
            foreach (var path in contentSet.Paths)
            {
                var fullPath = Path.GetFullPath(path);
                if (!consumedPaths.Add(fullPath) ||
                    !evidenceByPath.TryGetValue(fullPath, out var evidence))
                {
                    return StoragePhysicalReclaimVerification.Unavailable(
                        "Every hash-matched sampled path must have one current physical evidence record before reclaim bytes can be verified.");
                }

                setEvidence.Add(evidence);
            }

            var physicalFiles = new List<StorageVerifiedPhysicalFile>();
            foreach (var identityGroup in setEvidence.GroupBy(static evidence => evidence.Identity))
            {
                var groupedEvidence = identityGroup.ToArray();
                var first = groupedEvidence[0];
                if (!consumedIdentities.Add(first.Identity))
                {
                    return StoragePhysicalReclaimVerification.Unavailable(
                        "One current physical file identity appeared in more than one content-match set, so reclaim accounting was rejected.");
                }
                if (first.HardLinkCount < groupedEvidence.Length)
                {
                    return StoragePhysicalReclaimVerification.Unavailable(
                        "The current hard-link count was smaller than the number of sampled paths mapping to the same physical file.");
                }
                if (groupedEvidence.Any(evidence =>
                    evidence.HardLinkCount != first.HardLinkCount ||
                    evidence.AllocatedBytes != first.AllocatedBytes))
                {
                    return StoragePhysicalReclaimVerification.Unavailable(
                        "Two sampled paths mapped to the same current physical file but disagreed on link-count or allocation evidence.");
                }

                physicalFiles.Add(new StorageVerifiedPhysicalFile(
                    first.Identity,
                    groupedEvidence
                        .Select(static evidence => evidence.Path)
                        .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    first.HardLinkCount,
                    first.AllocatedBytes));
            }

            physicalFiles.Sort(static (left, right) =>
            {
                var volume = left.Identity.VolumeSerialNumber.CompareTo(right.Identity.VolumeSerialNumber);
                return volume != 0
                    ? volume
                    : left.Identity.FileIndex.CompareTo(right.Identity.FileIndex);
            });

            var setReclaimable = CalculateSetReclaimableUpperBound(physicalFiles);
            verifiedSets.Add(new StorageVerifiedPhysicalMatchSet(
                physicalFiles.ToArray(),
                setReclaimable));
            totalReclaimable = SaturatingAdd(totalReclaimable, setReclaimable);
        }

        return new StoragePhysicalReclaimVerification(
            StoragePhysicalReclaimEvidenceStatus.Verified,
            verifiedSets.ToArray(),
            totalReclaimable,
            "Physical identity, current hard-link count, and current allocated-byte evidence were revalidated for every SHA-256 matched sampled path. " +
            "The reclaim value is a maximum upper bound assuming one content-equivalent physical file remains in each match set and only singleton-link physical files are later authorized for deletion.");
    }

    private static long CalculateSetReclaimableUpperBound(
        IReadOnlyList<StorageVerifiedPhysicalFile> physicalFiles)
    {
        if (physicalFiles.Count < 2)
        {
            return 0;
        }

        var singletonFiles = physicalFiles
            .Where(static file => file.IsSingletonLink)
            .ToArray();
        if (singletonFiles.Length == 0)
        {
            return 0;
        }

        if (physicalFiles.Any(static file => !file.IsSingletonLink))
        {
            long reclaimable = 0;
            foreach (var file in singletonFiles)
            {
                reclaimable = SaturatingAdd(reclaimable, file.AllocatedBytes);
            }

            return reclaimable;
        }

        if (singletonFiles.Length < 2)
        {
            return 0;
        }

        var keepIndex = 0;
        for (var index = 1; index < singletonFiles.Length; index++)
        {
            if (singletonFiles[index].AllocatedBytes < singletonFiles[keepIndex].AllocatedBytes)
            {
                keepIndex = index;
            }
        }

        long result = 0;
        for (var index = 0; index < singletonFiles.Length; index++)
        {
            if (index != keepIndex)
            {
                result = SaturatingAdd(result, singletonFiles[index].AllocatedBytes);
            }
        }

        return result;
    }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right
            ? long.MaxValue
            : left + right;
}
