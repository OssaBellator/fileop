param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot,

    [Parameter(Mandatory = $true)]
    [string]$DestinationRoot,

    [string]$Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw "Cross-volume Move native validation requires Windows."
}

$currentIdentity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$currentPrincipal = [System.Security.Principal.WindowsPrincipal]::new($currentIdentity)
if ($currentPrincipal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Cross-volume Move native validation must run from an ordinary unelevated token. Close the elevated shell and rerun from the normal FileOp development user session."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $repoRoot "tests\FileOp.Windows.Tests\FileOp.Windows.Tests.csproj"
if (-not (Test-Path -LiteralPath $testProject -PathType Leaf)) {
    throw "FileOp.Windows.Tests project was not found at '$testProject'."
}
if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
    throw "Python 3 is required for the cross-volume Move native matrix inventory verifier."
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET 10 SDK is required for the cross-volume Move native matrix."
}

& python (Join-Path $repoRoot "tools\verify_cross_volume_move_source_preflight.py") --repo-root $repoRoot --cases 50000
if ($LASTEXITCODE -ne 0) {
    throw "Cross-volume Move source-preflight source/model verification failed with exit code $LASTEXITCODE."
}

& python (Join-Path $repoRoot "tools\verify_cross_volume_move_native_inventory.py") --repo-root $repoRoot
if ($LASTEXITCODE -ne 0) {
    throw "Cross-volume Move native matrix inventory verification failed with exit code $LASTEXITCODE."
}

& python (Join-Path $repoRoot "tools\verify_cross_volume_move_source_preflight_native_inventory.py") --repo-root $repoRoot
if ($LASTEXITCODE -ne 0) {
    throw "Cross-volume Move source-preflight native inventory verification failed with exit code $LASTEXITCODE."
}

& python (Join-Path $repoRoot "tools\verify_move_volume_identity.py") --repo-root $repoRoot
if ($LASTEXITCODE -ne 0) {
    throw "Move volume identity source verification failed with exit code $LASTEXITCODE."
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

$sourceDriveRoot = [System.IO.Path]::GetPathRoot($resolvedSourceRoot)
$destinationDriveRoot = [System.IO.Path]::GetPathRoot($resolvedDestinationRoot)
if ([string]::IsNullOrWhiteSpace($sourceDriveRoot) -or
    [string]::IsNullOrWhiteSpace($destinationDriveRoot)) {
    throw "Cross-volume Move native validation requires filesystem roots with resolvable local drive roots."
}

$sourceDrive = [System.IO.DriveInfo]::new($sourceDriveRoot)
$destinationDrive = [System.IO.DriveInfo]::new($destinationDriveRoot)
if (-not [string]::Equals($sourceDrive.DriveFormat, "NTFS", [StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals($destinationDrive.DriveFormat, "NTFS", [StringComparison]::OrdinalIgnoreCase)) {
    throw "Cross-volume Move native validation requires both supplied roots to be on NTFS, matching FileOp's current mutation identity/capability boundary. Source='$($sourceDrive.DriveFormat)', Destination='$($destinationDrive.DriveFormat)'."
}

$previousSourceRoot = [Environment]::GetEnvironmentVariable(
    "FILEOP_CROSS_VOLUME_MOVE_SOURCE_ROOT",
    [EnvironmentVariableTarget]::Process)
$previousDestinationRoot = [Environment]::GetEnvironmentVariable(
    "FILEOP_CROSS_VOLUME_MOVE_DESTINATION_ROOT",
    [EnvironmentVariableTarget]::Process)

try {
    [Environment]::SetEnvironmentVariable(
        "FILEOP_CROSS_VOLUME_MOVE_SOURCE_ROOT",
        $resolvedSourceRoot,
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        "FILEOP_CROSS_VOLUME_MOVE_DESTINATION_ROOT",
        $resolvedDestinationRoot,
        [EnvironmentVariableTarget]::Process)

    Write-Host "Running explicit cross-volume Move native matrix under an ordinary unelevated token"
    Write-Host "  Identity:          $($currentIdentity.Name)"
    Write-Host "  Source root:       $resolvedSourceRoot"
    Write-Host "  Source filesystem: $($sourceDrive.DriveFormat)"
    Write-Host "  Destination root:  $resolvedDestinationRoot"
    Write-Host "  Destination filesystem: $($destinationDrive.DriveFormat)"
    Write-Host "  Elevated administrator role: false"
    Write-Host "The tests themselves verify that the resolved filesystem volume serials differ."
    Write-Host "The raw source-delete provider independently proves exact handle-bound NTFS and FILE_SUPPORTS_POSIX_UNLINK_RENAME before SourceDeleteStarted."

    & dotnet test $testProject `
        -c $Configuration `
        --filter "TestCategory=CrossVolumeMoveNative" `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Cross-volume Move native matrix failed with exit code $LASTEXITCODE."
    }
}
finally {
    [Environment]::SetEnvironmentVariable(
        "FILEOP_CROSS_VOLUME_MOVE_SOURCE_ROOT",
        $previousSourceRoot,
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        "FILEOP_CROSS_VOLUME_MOVE_DESTINATION_ROOT",
        $previousDestinationRoot,
        [EnvironmentVariableTarget]::Process)
}

Write-Host "PASS explicit cross-volume Move native matrix"
