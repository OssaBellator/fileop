param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot,

    [Parameter(Mandatory = $true)]
    [string]$DestinationRoot,

    [string]$Configuration = "Release",

    [string]$EvidenceDirectory = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw "The cross-volume Move merge gate requires Windows."
}

$currentIdentity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$currentPrincipal = [System.Security.Principal.WindowsPrincipal]::new($currentIdentity)
if ($currentPrincipal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "The cross-volume Move merge gate must run from an ordinary unelevated token. Close the elevated shell and rerun normally."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

foreach ($command in @("git", "dotnet", "python", "powershell.exe")) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "Required command '$command' was not found."
    }
}

$headSha = (& git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($headSha)) {
    throw "Could not resolve the exact git HEAD for cross-volume Move validation."
}

$dirty = @(& git status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) {
    throw "Could not verify the git working-tree state."
}
if ($dirty.Count -ne 0) {
    throw "The cross-volume Move merge gate requires a clean working tree so the evidence is bound to exact HEAD $headSha."
}

$sourceItem = Get-Item -LiteralPath $SourceRoot -ErrorAction Stop
$destinationItem = Get-Item -LiteralPath $DestinationRoot -ErrorAction Stop
if (-not $sourceItem.PSIsContainer -or -not $destinationItem.PSIsContainer) {
    throw "SourceRoot and DestinationRoot must both be existing directories."
}

$resolvedSourceRoot = $sourceItem.FullName
$resolvedDestinationRoot = $destinationItem.FullName
if ([string]::Equals(
        $resolvedSourceRoot.TrimEnd('\'),
        $resolvedDestinationRoot.TrimEnd('\'),
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "SourceRoot and DestinationRoot must be different directories on different filesystem volumes."
}

if (-not ("FileOpCrossVolumeMoveGate.NativeMethods" -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FileOpCrossVolumeMoveGate
{
    public static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVolumePathNameW(
            string lpszFileName,
            StringBuilder lpszVolumePathName,
            uint cchBufferLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVolumeInformationW(
            string lpRootPathName,
            StringBuilder lpVolumeNameBuffer,
            uint nVolumeNameSize,
            out uint lpVolumeSerialNumber,
            out uint lpMaximumComponentLength,
            out uint lpFileSystemFlags,
            StringBuilder lpFileSystemNameBuffer,
            uint nFileSystemNameSize);
    }
}
'@
}

function Get-VolumeEvidence {
    param([Parameter(Mandatory = $true)][string]$Path)

    $volumePath = [System.Text.StringBuilder]::new(1024)
    if (-not [FileOpCrossVolumeMoveGate.NativeMethods]::GetVolumePathNameW(
            $Path,
            $volumePath,
            [uint32]$volumePath.Capacity)) {
        $error = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        throw "GetVolumePathNameW failed for '$Path' with Win32 error $error."
    }

    $volumeName = [System.Text.StringBuilder]::new(261)
    $fileSystemName = [System.Text.StringBuilder]::new(261)
    [uint32]$serial = 0
    [uint32]$maximumComponentLength = 0
    [uint32]$fileSystemFlags = 0
    if (-not [FileOpCrossVolumeMoveGate.NativeMethods]::GetVolumeInformationW(
            $volumePath.ToString(),
            $volumeName,
            [uint32]$volumeName.Capacity,
            [ref]$serial,
            [ref]$maximumComponentLength,
            [ref]$fileSystemFlags,
            $fileSystemName,
            [uint32]$fileSystemName.Capacity)) {
        $error = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        throw "GetVolumeInformationW failed for '$Path' with Win32 error $error."
    }

    [pscustomobject]@{
        Root = $volumePath.ToString()
        Serial = $serial
        SerialHex = ("0x{0:X8}" -f $serial)
        FileSystem = $fileSystemName.ToString()
    }
}

$sourceVolume = Get-VolumeEvidence -Path $resolvedSourceRoot
$destinationVolume = Get-VolumeEvidence -Path $resolvedDestinationRoot
if ($sourceVolume.Serial -eq $destinationVolume.Serial) {
    throw "The #185 schema-v1 native matrix requires different stable filesystem volume serials. Source and destination both reported $($sourceVolume.SerialHex)."
}

if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
    $timestamp = [DateTimeOffset]::UtcNow.ToString("yyyyMMddTHHmmssZ")
    $EvidenceDirectory = Join-Path ([IO.Path]::GetTempPath()) "FileOp.CrossVolumeMoveGate.$($headSha.Substring(0, 12)).$timestamp"
}
$evidencePath = [IO.Path]::GetFullPath($EvidenceDirectory)
$repoPathForContainment = [IO.Path]::GetFullPath($repoRoot).TrimEnd('\') + '\'
$evidencePathForContainment = $evidencePath.TrimEnd('\') + '\'
if ($evidencePathForContainment.StartsWith(
        $repoPathForContainment,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "EvidenceDirectory must be outside the repository checkout so the validation run cannot dirty its own exact-head working tree."
}
[void](New-Item -ItemType Directory -Path $evidencePath -Force)

$dotnetVersion = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($dotnetVersion)) {
    throw "Could not resolve the .NET SDK version."
}

$windowsVersion = [Environment]::OSVersion.VersionString
try {
    $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
    if (-not [string]::IsNullOrWhiteSpace($os.Caption)) {
        $windowsVersion = "$($os.Caption) $($os.Version) build $($os.BuildNumber)"
    }
}
catch {
    # Environment.OSVersion is still recorded when CIM is unavailable.
}

$manifestPath = Join-Path $evidencePath "environment.txt"
@(
    "RecordedAtUtc=$([DateTimeOffset]::UtcNow.ToString('O'))",
    "GitHead=$headSha",
    "WorkingTreeClean=true",
    "Identity=$($currentIdentity.Name)",
    "ElevatedAdministratorRole=false",
    "WindowsVersion=$windowsVersion",
    "DotNetSdkVersion=$dotnetVersion",
    "SourceRoot=$resolvedSourceRoot",
    "SourceVolumeRoot=$($sourceVolume.Root)",
    "SourceVolumeSerial=$($sourceVolume.SerialHex)",
    "SourceFileSystem=$($sourceVolume.FileSystem)",
    "DestinationRoot=$resolvedDestinationRoot",
    "DestinationVolumeRoot=$($destinationVolume.Root)",
    "DestinationVolumeSerial=$($destinationVolume.SerialHex)",
    "DestinationFileSystem=$($destinationVolume.FileSystem)"
) | Set-Content -LiteralPath $manifestPath -Encoding UTF8

$commandsPath = Join-Path $evidencePath "commands.txt"
@(
    "powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-local.ps1",
    "powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-cross-volume-move-security.ps1 -Configuration $Configuration",
    "powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-cross-volume-move-native.ps1 -SourceRoot <recorded SourceRoot> -DestinationRoot <recorded DestinationRoot> -Configuration $Configuration"
) | Set-Content -LiteralPath $commandsPath -Encoding UTF8

function Invoke-LoggedGateStep {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$LogPath
    )

    Write-Host "`n==> $Name" -ForegroundColor Cyan
    Write-Host "    Log: $LogPath"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments 2>&1 |
        Tee-Object -FilePath $LogPath
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "$Name failed with exit code $exitCode. Complete output is retained at '$LogPath'."
    }
}

$localGate = Join-Path $repoRoot "tools\test-local.ps1"
$securityGate = Join-Path $repoRoot "tools\test-cross-volume-move-security.ps1"
$nativeGate = Join-Path $repoRoot "tools\test-cross-volume-move-native.ps1"
foreach ($script in @($localGate, $securityGate, $nativeGate)) {
    if (-not (Test-Path -LiteralPath $script -PathType Leaf)) {
        throw "Required gate script was not found: $script"
    }
}

Invoke-LoggedGateStep `
    -Name "Complete local FileOp gate" `
    -ScriptPath $localGate `
    -Arguments @() `
    -LogPath (Join-Path $evidencePath "01-test-local.log")

Invoke-LoggedGateStep `
    -Name "Ordinary-token cross-volume Move security gate" `
    -ScriptPath $securityGate `
    -Arguments @("-Configuration", $Configuration) `
    -LogPath (Join-Path $evidencePath "02-cross-volume-security.log")

Invoke-LoggedGateStep `
    -Name "Explicit two-volume cross-volume Move native gate" `
    -ScriptPath $nativeGate `
    -Arguments @(
        "-SourceRoot", $resolvedSourceRoot,
        "-DestinationRoot", $resolvedDestinationRoot,
        "-Configuration", $Configuration) `
    -LogPath (Join-Path $evidencePath "03-cross-volume-native.log")

$summaryPath = Join-Path $evidencePath "PASS.txt"
@(
    "PASS: exact-head cross-volume Move merge gate completed.",
    "GitHead=$headSha",
    "CompletedAtUtc=$([DateTimeOffset]::UtcNow.ToString('O'))",
    "EvidenceDirectory=$evidencePath"
) | Set-Content -LiteralPath $summaryPath -Encoding UTF8

Write-Host "`nPASS exact-head cross-volume Move merge gate" -ForegroundColor Green
Write-Host "  Git head:  $headSha"
Write-Host "  Evidence:  $evidencePath"
