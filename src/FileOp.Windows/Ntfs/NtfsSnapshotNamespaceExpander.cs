using System.ComponentModel;
using FileOp.Core.Models;

namespace FileOp.Windows.Ntfs;

public sealed class NtfsSnapshotNamespaceExpander
{
    private readonly INtfsHardLinkEnumerator _hardLinkEnumerator;

    public NtfsSnapshotNamespaceExpander(INtfsHardLinkEnumerator? hardLinkEnumerator = null)
    {
        _hardLinkEnumerator = hardLinkEnumerator ?? new NtfsHardLinkEnumerator();
    }

    public IReadOnlyList<FileRecord> Expand(
        NtfsVolume volume,
        NtfsMftEntry entry,
        string baselinePath,
        NtfsFileMetadata metadata,
        IReadOnlyDictionary<string, ulong> directoryFileReferencesByPath)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(baselinePath);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(directoryFileReferencesByPath);

        if (metadata.IsDirectory || metadata.NumberOfLinks <= 1)
        {
            return [NtfsFileRecordFactory.Create(volume, baselinePath, entry, metadata)];
        }

        IReadOnlyList<string> enumeratedLinks;
        try
        {
            enumeratedLinks = _hardLinkEnumerator.Enumerate(baselinePath);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3 or 1168)
        {
            throw new NtfsIndexResnapshotRequiredException(
                $"The baseline namespace path {baselinePath} disappeared while enumerating hard links for file reference " +
                $"{entry.FileReferenceNumber} on {volume.RootPath}. A fresh snapshot is required.",
                exception);
        }

        var linkPaths = enumeratedLinks
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (linkPaths.Length != metadata.NumberOfLinks)
        {
            throw new NtfsIndexResnapshotRequiredException(
                $"File reference {entry.FileReferenceNumber} on {volume.RootPath} reports {metadata.NumberOfLinks} hard links, " +
                $"but {linkPaths.Length} distinct namespace paths were enumerated. A fresh snapshot is required.");
        }

        var records = new List<FileRecord>(linkPaths.Length);
        foreach (var linkPath in linkPaths)
        {
            var parentPath = Path.GetDirectoryName(linkPath) ?? string.Empty;
            if (!directoryFileReferencesByPath.TryGetValue(parentPath, out var parentFileReferenceNumber))
            {
                throw new NtfsIndexResnapshotRequiredException(
                    $"Could not resolve parent directory {parentPath} for hard link {linkPath} on {volume.RootPath}. " +
                    "A fresh snapshot is required.");
            }

            var name = Path.GetFileName(linkPath);
            if (string.IsNullOrEmpty(name))
            {
                throw new NtfsIndexResnapshotRequiredException(
                    $"Hard-link enumeration returned an invalid namespace path for file reference {entry.FileReferenceNumber} on {volume.RootPath}.");
            }

            var namespaceEntry = entry with
            {
                ParentFileReferenceNumber = parentFileReferenceNumber,
                Name = name,
            };

            records.Add(NtfsFileRecordFactory.Create(volume, linkPath, namespaceEntry, metadata));
        }

        return records;
    }
}
