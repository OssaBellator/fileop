using FileOp.Core.Models;

namespace FileOp.Windows.Ntfs;

public static class NtfsHardLinkNamespacePlanner
{
    public static IReadOnlyList<NtfsIndexMutation> PlanRefresh(
        NtfsVolume volume,
        NtfsJournalChange change,
        FileIdentity identity,
        NtfsFileMetadata metadata,
        IReadOnlyList<FileRecord> existingRows,
        string eventPath,
        FileIdentity eventParentIdentity,
        IReadOnlyList<string> liveLinkPaths)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(existingRows);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventPath);
        ArgumentNullException.ThrowIfNull(liveLinkPaths);

        if (metadata.IsDirectory)
        {
            throw new NtfsIndexResnapshotRequiredException(
                $"Directory {identity} reported a hard-link namespace change. A fresh snapshot is required.");
        }

        var distinctLinkPaths = liveLinkPaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (distinctLinkPaths.Length != metadata.NumberOfLinks)
        {
            throw new NtfsIndexResnapshotRequiredException(
                $"Hard-linked file {identity} reports {metadata.NumberOfLinks} links, but {distinctLinkPaths.Length} live paths were enumerated. " +
                "A fresh snapshot is required.");
        }

        var normalizedEventPath = Path.GetFullPath(eventPath);
        var parentByPath = new Dictionary<string, FileIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in existingRows)
        {
            if (row.ParentIdentity is { } parent)
            {
                parentByPath[Path.GetFullPath(row.Path)] = parent;
            }
        }

        parentByPath[normalizedEventPath] = eventParentIdentity;

        var mutations = new List<NtfsIndexMutation>(existingRows.Count + distinctLinkPaths.Length);
        foreach (var existing in existingRows)
        {
            if (!distinctLinkPaths.Contains(Path.GetFullPath(existing.Path), StringComparer.OrdinalIgnoreCase))
            {
                mutations.Add(NtfsIndexMutation.Delete(identity, existing.Path, existing.IsDirectory));
            }
        }

        foreach (var linkPath in distinctLinkPaths)
        {
            if (!parentByPath.TryGetValue(linkPath, out var parentIdentity))
            {
                throw new NtfsIndexResnapshotRequiredException(
                    $"Hard-link refresh for {identity} discovered new path {linkPath} without a matching journal parent identity. " +
                    "A fresh snapshot is required.");
            }

            var name = Path.GetFileName(linkPath);
            if (string.IsNullOrEmpty(name))
            {
                throw new NtfsIndexResnapshotRequiredException(
                    $"Hard-link refresh for {identity} returned invalid path {linkPath}.");
            }

            var namespaceEntry = new NtfsMftEntry(
                change.FileReferenceNumber,
                parentIdentity.FileReferenceNumber,
                change.Usn,
                change.Timestamp,
                change.Reason,
                change.Attributes,
                name);

            mutations.Add(NtfsIndexMutation.Upsert(
                NtfsFileRecordFactory.Create(volume, linkPath, namespaceEntry, metadata)));
        }

        return mutations;
    }
}
