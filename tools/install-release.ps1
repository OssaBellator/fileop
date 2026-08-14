param(
    [Parameter(Mandatory = $true)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })][string]$PackageZip,
    [string]$InstallDirectory = (Join-Path $env:ProgramFiles 'FileOp')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'FileOp install/update must run from an elevated PowerShell process so the installation directory is protected from standard-user mutation.'
}

if (Get-Process -Name 'FileOp.App' -ErrorAction SilentlyContinue) {
    throw 'Close FileOp before installing or updating.'
}

$package = [IO.Path]::GetFullPath($PackageZip)
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
    $thumbprint = ([string]$manifest.TrustedSignerThumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    if ($thumbprint.Length -ne 40) { throw 'The package manifest has an invalid trusted signer thumbprint.' }

    foreach ($entry in $manifest.Files) {
        $relative = ([string]$entry.Path).Replace('/', [IO.Path]::DirectorySeparatorChar)
        if ([IO.Path]::IsPathRooted($relative) -or $relative.Split([IO.Path]::DirectorySeparatorChar) -contains '..') {
            throw "Unsafe package manifest path: $relative"
        }
        $path = Join-Path $temp $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Package file is missing: $relative" }
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
        if ($hash -ne ([string]$entry.Sha256).ToLowerInvariant()) {
            throw "Package hash mismatch: $relative"
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
        if ($actual -ne $thumbprint) {
            throw "Signer mismatch in package for $($file.Name)."
        }
    }
    if (-not ($signedFiles | Where-Object Name -eq 'FileOp.App.exe')) { throw 'Package has no signed FileOp.App.exe.' }
    if (-not ($signedFiles | Where-Object Name -eq 'FileOp.Indexer.exe')) { throw 'Package has no signed FileOp.Indexer.exe.' }

    Copy-Item -LiteralPath $temp -Destination $stage -Recurse
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
    Write-Host "PASS: FileOp $($manifest.Version) installed at $install" -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
    if ($backupCreated -and (Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $install)) {
        Move-Item -LiteralPath $backup -Destination $install -ErrorAction SilentlyContinue
    }
}
