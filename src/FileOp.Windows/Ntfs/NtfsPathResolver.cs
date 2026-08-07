namespace FileOp.Windows.Ntfs;

public static class NtfsPathResolver
{
    public static IReadOnlyDictionary<ulong, string> Resolve(
        string rootPath,
        IEnumerable<NtfsMftEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(entries);

        var byId = new Dictionary<ulong, NtfsMftEntry>();
        foreach (var entry in entries)
        {
            byId[entry.FileReferenceNumber] = entry;
        }

        var resolved = new Dictionary<ulong, string>(byId.Count);
        var visiting = new HashSet<ulong>();
        var normalizedRoot = Path.GetFullPath(rootPath);

        foreach (var fileReferenceNumber in byId.Keys)
        {
            _ = ResolveOne(fileReferenceNumber);
        }

        return resolved;

        string? ResolveOne(ulong fileReferenceNumber)
        {
            if (resolved.TryGetValue(fileReferenceNumber, out var existing))
            {
                return existing;
            }

            if (!byId.TryGetValue(fileReferenceNumber, out var entry))
            {
                return null;
            }

            if (!visiting.Add(fileReferenceNumber))
            {
                return null;
            }

            try
            {
                if (entry.ParentFileReferenceNumber == fileReferenceNumber)
                {
                    resolved[fileReferenceNumber] = normalizedRoot;
                    return normalizedRoot;
                }

                var parentPath = ResolveOne(entry.ParentFileReferenceNumber);
                if (parentPath is null)
                {
                    return null;
                }

                var path = Path.Combine(parentPath, entry.Name);
                resolved[fileReferenceNumber] = path;
                return path;
            }
            finally
            {
                visiting.Remove(fileReferenceNumber);
            }
        }
    }
}
