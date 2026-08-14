param(
    [string]$InstallDirectory = (Join-Path $env:ProgramFiles 'FileOp'),
    [switch]$PurgeUserData
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'FileOp uninstall must run from an elevated PowerShell process.'
}
$runningFileOp = Get-Process -Name 'FileOp.App', 'FileOp.Indexer' -ErrorAction SilentlyContinue
if ($runningFileOp) {
    $names = ($runningFileOp.ProcessName | Sort-Object -Unique) -join ', '
    throw "Close FileOp and wait for its indexing helper to exit before uninstalling. Still running: $names"
}

$install = [IO.Path]::GetFullPath($InstallDirectory)
if (Test-Path -LiteralPath $install) {
    Remove-Item -LiteralPath $install -Recurse -Force
}

$userData = Join-Path $env:LOCALAPPDATA 'FileOp'
if ($PurgeUserData -and (Test-Path -LiteralPath $userData)) {
    Remove-Item -LiteralPath $userData -Recurse -Force
    Write-Host 'FileOp binaries and per-user FileOp data were removed.' -ForegroundColor Green
}
else {
    Write-Host "FileOp binaries were removed. Per-user data remains at $userData; rerun with -PurgeUserData to remove it explicitly." -ForegroundColor Green
}
