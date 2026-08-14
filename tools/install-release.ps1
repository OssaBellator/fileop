param(
    [Parameter(Mandatory = $true)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })][string]$PackageZip,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$TrustedPackageSha256,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9A-Fa-f ]{40,}$')][string]$TrustedSignerThumbprint,
    [string]$InstallDirectory = (Join-Path $env:ProgramFiles 'FileOp')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$expectedPackageHash = $TrustedPackageSha256.ToLowerInvariant()
$expectedThumbprint = ($TrustedSignerThumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
if ($expectedThumbprint.Length -ne 40) {
    throw 'TrustedSignerThumbprint must normalize to exactly 40 hexadecimal characters.'
}

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'FileOp install/update must run from an elevated PowerShell process so the installation directory is protected from standard-user mutation.'
}

$runningFileOp = Get-Process -Name 'FileOp.App', 'FileOp.Indexer' -ErrorAction SilentlyContinue
if ($runningFileOp) {
    $names = ($runningFileOp.ProcessName | Sort-Object -Unique) -join ', '
    throw "Close FileOp and wait for its indexing helper to exit before installing or updating. Still running: $names"
}

$package = [IO.Path]::GetFullPath($PackageZip)
$actualPackageHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $package).Hash.ToLowerInvariant()
if ($actualPackageHash -ne $expectedPackageHash) {
    throw "Package SHA-256 does not match independently supplied release metadata. Expected $expectedPackageHash, got $actualPackageHash."
}

$install = [IO.Path]::GetFullPath($InstallDirectory)
$parent = Split-Path -Parent $install
if ([string]::IsNullOrWhiteSpace($parent)) { throw 'InstallDirectory must have a parent directory.' }
New-Item -ItemType Directory -Path $parent -Force | Out-Null

$temp = Join-Path ([IO.Path]::GetTempPath()) ("FileOp.Install." + [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $parent (".FileOp.stage." + [Guid]::NewGuid().ToString('N'))
$backup = Join-Path $parent (".FileOp.backup." + [Guid]::NewGuid().ToString('N'))
$backupCreated = $false
try {
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    Expand-Archive -LiteralPath $package -DestinationPath $temp -Force
    $manifestPath = Join-Path $temp 'fileop-release-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw 'The package is missing fileop-release-manifest.json.'
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.Product -ne 'FileOp' -or $manifest.Architecture -ne 'x64') {
        throw 'The package manifest does not describe the supported FileOp x64 package.'
    }
    $manifestThumbprint = ([string]$manifest.TrustedSignerThumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    if ($manifestThumbprint.Length -ne 40) { throw 'The package manifest has an invalid trusted signer thumbprint.' }
    if ($manifestThumbprint -ne $expectedThumbprint) {
        throw "The package signer pin does not match the independently supplied trusted signer. Expected $expectedThumbprint, manifest contains $manifestThumbprint."
    }

    $tempRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($temp))
    $manifestPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifest.Files) {
        $relative = ([string]$entry.Path).Replace('/', [IO.Path]::DirectorySeparatorChar)
        $segments = @($relative -split '[\\/]')
        if ([IO.Path]::IsPathRooted($relative) -or $segments -contains '..' -or $segments -contains '') {
            throw "Unsafe package manifest path: $relative"
        }
        if (-not $manifestPaths.Add($relative)) {
            throw "Duplicate package manifest path: $relative"
        }
        $path = [IO.Path]::GetFullPath((Join-Path $temp $relative))
        $pathParent = [IO.Path]::GetDirectoryName($path)
        if ([string]::IsNullOrWhiteSpace($pathParent) -or
            -not ($path.StartsWith($tempRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))) {
            throw "Package manifest path escapes the extraction root: $relative"
        }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Package file is missing: $relative" }
        $file = Get-Item -LiteralPath $path
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Package payload may not contain a reparse point: $relative"
        }
        if ($file.Length -ne [long]$entry.Length) {
            throw "Package length mismatch: $relative"
        }
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
        if ($hash -ne ([string]$entry.Sha256).ToLowerInvariant()) {
            throw "Package hash mismatch: $relative"
        }
    }

    foreach ($file in Get-ChildItem -LiteralPath $temp -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($temp, $file.FullName)
        if ([string]::Equals($relative, 'fileop-release-manifest.json', [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        if (-not $manifestPaths.Contains($relative)) {
            throw "Package contains an unexpected file not listed by the manifest: $relative"
        }
    }

    $signedFiles = Get-ChildItem -LiteralPath $temp -Recurse -File |
        Where-Object { $_.Extension -in '.exe', '.dll' -and $_.Name -like 'FileOp.*' }
    foreach ($file in $signedFiles) {
        $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
        if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate) {
            throw "Invalid Authenticode signature in package: $($file.Name) ($($signature.Status))"
        }
        $actual = ($signature.SignerCertificate.Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
        if ($actual -ne $expectedThumbprint) {
            throw "Signer mismatch in package for $($file.Name). Expected $expectedThumbprint, got $actual."
        }
    }
    if (-not ($signedFiles | Where-Object Name -eq 'FileOp.App.exe')) { throw 'Package has no signed FileOp.App.exe.' }
    if (-not ($signedFiles | Where-Object Name -eq 'FileOp.Indexer.exe')) { throw 'Package has no signed FileOp.Indexer.exe.' }

    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    Copy-Item -Path (Join-Path $temp '*') -Destination $stage -Recurse -Force
    if (Test-Path -LiteralPath $install) {
        Move-Item -LiteralPath $install -Destination $backup
        $backupCreated = $true
    }
    try {
        Move-Item -LiteralPath $stage -Destination $install
    }
    catch {
        if ($backupCreated -and -not (Test-Path -LiteralPath $install)) {
            Move-Item -LiteralPath $backup -Destination $install
            $backupCreated = $false
        }
        throw
    }

    if ($backupCreated -and (Test-Path -LiteralPath $backup)) {
        Remove-Item -LiteralPath $backup -Recurse -Force
        $backupCreated = $false
    }
    Write-Host "PASS: FileOp $($manifest.Version) installed at $install with independently pinned package hash and signer $expectedThumbprint" -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
    if ($backupCreated -and (Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $install)) {
        Move-Item -LiteralPath $backup -Destination $install -ErrorAction SilentlyContinue
    }
}
