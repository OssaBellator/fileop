param(
    [switch]$ConfirmPurge
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $ConfirmPurge) {
    throw 'Per-user FileOp data purge is destructive and removes indexes, settings and recovery/history evidence. Rerun with -ConfirmPurge only when that loss is explicitly intended.'
}

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Per-user FileOp data purge must run from a non-elevated PowerShell process. Elevated recursive deletion of a user-writable tree is intentionally unsupported.'
}

$runningFileOp = Get-Process -Name 'FileOp.App', 'FileOp.Indexer' -ErrorAction SilentlyContinue
if ($runningFileOp) {
    $names = ($runningFileOp.ProcessName | Sort-Object -Unique) -join ', '
    throw "Close FileOp and wait for its indexing helper to exit before purging per-user data. Still running: $names"
}

$knownLocalAppData = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::LocalApplicationData)
if ([string]::IsNullOrWhiteSpace($knownLocalAppData)) {
    throw 'The current-user LocalApplicationData known folder is unavailable. FileOp purge will not use an environment-variable or caller-selected fallback path.'
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

if (-not (Test-Path -LiteralPath $userData)) {
    Write-Host "No FileOp per-user data exists at $userData" -ForegroundColor Green
    return
}

# Refuse reparse points anywhere in the tree before recursive deletion. This is
# primarily an accidental-data-loss guard: purge already runs with only the
# current user's ordinary token, never an administrator token.
$rootItem = Get-Item -LiteralPath $userData -Force
if ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'The FileOp per-user data root is a reparse point; refusing recursive deletion.'
}

$pending = [System.Collections.Generic.Stack[string]]::new()
$pending.Push($rootItem.FullName)
while ($pending.Count -gt 0) {
    $directory = $pending.Pop()
    foreach ($child in Get-ChildItem -LiteralPath $directory -Force) {
        if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "The FileOp per-user data tree contains a reparse point and will not be recursively purged: $($child.FullName)"
        }
        if ($child.PSIsContainer) {
            $pending.Push($child.FullName)
        }
    }
}

Remove-Item -LiteralPath $userData -Recurse -Force
Write-Host "FileOp per-user data was removed from $userData under the current user's non-elevated token." -ForegroundColor Green
