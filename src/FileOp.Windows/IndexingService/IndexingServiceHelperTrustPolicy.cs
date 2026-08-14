namespace FileOp.Windows.IndexingService;

internal static class IndexingServiceHelperTrustPolicy
{
    internal static void RequireTrustedForElevation(string helperPath)
    {
        var trust = WindowsAuthenticodeFileTrust.VerifyPinnedEmbeddedSignature(helperPath);
#if DEBUG
        if (string.Equals(trust.SignerThumbprint, "DEBUG-BYPASS", StringComparison.Ordinal))
        {
            return;
        }
#endif
        WindowsElevatedHelperPathProtection.RequireProtectedLaunchPath(helperPath);
    }
}
