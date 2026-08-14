param(
    [switch]$PurgeUserData
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$knownProgramFiles = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::ProgramFiles)
if ([string]::IsNullOrWhiteSpace($knownProgramFiles)) {
    throw 'The canonical Program Files known folder is unavailable. FileOp uninstall will not use an environment-variable or caller-selected fallback path.'
}
$knownLocalAppData = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::LocalApplicationData)
if ([string]::IsNullOrWhiteSpace($knownLocalAppData)) {
    throw 'The current-user LocalApplicationData known folder is unavailable. FileOp uninstall will not use an environment-variable fallback for purge state.'
}

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'FileOp uninstall must run from an elevated PowerShell process.'
}
$runningFileOp = Get-Process -Name 'FileOp.App', 'FileOp.Indexer' -ErrorAction SilentlyContinue
if ($runningFileOp) {
    $names = ($runningFileOp.ProcessName | Sort-Object -Unique) -join ', '
    throw "Close FileOp and wait for its indexing helper to exit before uninstalling. Still running: $names"
}

# Uninstall owns exactly the release install root. It is intentionally not a
# generic elevated recursive-delete wrapper around a caller-provided path.
# Privileged roots come from OS known-folder resolution, not mutable process
# environment strings.
$programFiles = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($knownProgramFiles))
$install = [IO.Path]::GetFullPath((Join-Path $programFiles 'FileOp'))
$expectedInstall = $programFiles + [IO.Path]::DirectorySeparatorChar + 'FileOp'
if (-not [string]::Equals(
        [IO.Path]::TrimEndingDirectorySeparator($install),
        $expectedInstall,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The FileOp uninstall root did not resolve to the canonical Program Files\FileOp path.'
}

if (Test-Path -LiteralPath $install) {
    $existingInstall = Get-Item -LiteralPath $install -Force
    if ($existingInstall.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'The canonical FileOp install path is a reparse point; refusing privileged recursive deletion.'
    }
    Remove-Item -LiteralPath $install -Recurse -Force
}

$localAppData = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($knownLocalAppData))
$userData = [IO.Path]::GetFullPath((Join-Path $localAppData 'FileOp'))
$expectedUserData = $localAppData + [IO.Path]::DirectorySeparatorChar + 'FileOp'
if (-not [string]::Equals(
        [IO.Path]::TrimEndingDirectorySeparator($userData),
        $expectedUserData,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The FileOp per-user data root did not resolve beneath the current-user LocalApplicationData known folder.'
}

if ($PurgeUserData -and (Test-Path -LiteralPath $userData)) {
    $userDataItem = Get-Item -LiteralPath $userData -Force
    if ($userDataItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'The FileOp per-user data path is a reparse point; refusing privileged recursive deletion.'
    }
    Remove-Item -LiteralPath $userData -Recurse -Force
    Write-Host 'FileOp binaries and per-user FileOp data were removed.' -ForegroundColor Green
}
else {
    Write-Host "FileOp binaries were removed. Per-user data remains at $userData; rerun with -PurgeUserData to remove it explicitly." -ForegroundColor Green
}
