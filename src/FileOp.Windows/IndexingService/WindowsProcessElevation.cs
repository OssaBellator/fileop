using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace FileOp.Windows.IndexingService;

public static class WindowsProcessElevation
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var elevation = GetTokenInt32(identity, TokenInformationClass.TokenElevation);
        return elevation != 0;
    }

    public static bool CanElevateCurrentIdentityInPlace()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var elevationType = (TokenElevationType)GetTokenInt32(
            identity,
            TokenInformationClass.TokenElevationType);
        return elevationType == TokenElevationType.Limited;
    }

    private static int GetTokenInt32(WindowsIdentity identity, TokenInformationClass informationClass)
    {
        if (!GetTokenInformation(
            identity.AccessToken,
            informationClass,
            out var value,
            sizeof(int),
            out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return value;
    }

    private enum TokenInformationClass
    {
        TokenElevationType = 18,
        TokenElevation = 20,
    }

    private enum TokenElevationType
    {
        Default = 1,
        Full = 2,
        Limited = 3,
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        Microsoft.Win32.SafeHandles.SafeAccessTokenHandle tokenHandle,
        TokenInformationClass tokenInformationClass,
        out int tokenInformation,
        int tokenInformationLength,
        out int returnLength);
}
