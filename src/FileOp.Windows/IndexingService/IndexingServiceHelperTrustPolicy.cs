namespace FileOp.Windows.IndexingService;

internal static class IndexingServiceHelperTrustPolicy
{
    internal static void RequireTrustedForElevation(string helperPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
        var requested = Path.GetFullPath(helperPath);
        var adjacent = Path.GetFullPath(
            IndexingServiceHelperLocator.ResolveAdjacentHelper(AppContext.BaseDirectory));
        if (!string.Equals(requested, adjacent, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                "Elevated helper launch is restricted to the exact FileOp.Indexer.exe installed beside the running FileOp application.");
        }

        var trust = WindowsAuthenticodeFileTrust.VerifyPinnedEmbeddedSignature(adjacent);
#if DEBUG
        if (string.Equals(trust.SignerThumbprint, "DEBUG-BYPASS", StringComparison.Ordinal))
        {
            return;
        }
#endif
        WindowsElevatedHelperPathProtection.RequireProtectedLaunchPath(adjacent);
    }
}
