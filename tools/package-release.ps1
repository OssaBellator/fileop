param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9A-Fa-f ]{40,}$')][string]$CertificateThumbprint,
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$TimestampUrl,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$')][string]$Version,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\release')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot
$thumbprint = ($CertificateThumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
if ($thumbprint.Length -ne 40) {
    throw 'CertificateThumbprint must normalize to exactly 40 hexadecimal characters.'
}

$signtool = Get-Command signtool.exe -ErrorAction Stop
$stageRoot = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) "FileOp-$Version-x64"
$zipPath = "$stageRoot.zip"
$digestPath = "$zipPath.sha256"
if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
if (Test-Path -LiteralPath $digestPath) { Remove-Item -LiteralPath $digestPath -Force }
New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null

# Compile the signer pin into FileOp.Windows. Production trust is therefore a
# property of the built artifacts, not a user-writable runtime setting.
dotnet build src/FileOp.App/FileOp.App.csproj `
    --configuration Release `
    -p:Platform=x64 `
    -p:FileOpRequireTrustedIndexerSigner=true `
    -p:FileOpTrustedIndexerSignerThumbprints=$thumbprint
if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }

$appOutput = Join-Path $repoRoot 'src\FileOp.App\bin\x64\Release\net10.0-windows10.0.26100.0'
if (-not (Test-Path -LiteralPath (Join-Path $appOutput 'FileOp.App.exe'))) {
    throw "Release app output was not found at $appOutput"
}
Copy-Item -Path (Join-Path $appOutput '*') -Destination $stageRoot -Recurse -Force

$signTargets = Get-ChildItem -LiteralPath $stageRoot -Recurse -File |
    Where-Object { $_.Extension -in '.exe', '.dll' -and $_.Name -like 'FileOp.*' }
if (-not ($signTargets | Where-Object Name -eq 'FileOp.App.exe')) { throw 'Staging is missing FileOp.App.exe.' }
if (-not ($signTargets | Where-Object Name -eq 'FileOp.Indexer.exe')) { throw 'Staging is missing FileOp.Indexer.exe.' }

foreach ($file in $signTargets) {
    & $signtool.Source sign /sha1 $thumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $file.FullName
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for $($file.FullName)." }
    & $signtool.Source verify /pa /all $file.FullName
    if ($LASTEXITCODE -ne 0) { throw "Authenticode verification failed for $($file.FullName)." }

    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate) {
        throw "PowerShell Authenticode verification failed for $($file.FullName): $($signature.Status)"
    }
    $actual = ($signature.SignerCertificate.Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    if ($actual -ne $thumbprint) {
        throw "Signer mismatch for $($file.FullName). Expected $thumbprint, got $actual."
    }
}

$manifestEntries = Get-ChildItem -LiteralPath $stageRoot -Recurse -File |
    Sort-Object FullName |
    ForEach-Object {
        [pscustomobject]@{
            Path = [IO.Path]::GetRelativePath($stageRoot, $_.FullName).Replace('\', '/')
            Length = $_.Length
            Sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
        }
    }
$manifest = [ordered]@{
    Product = 'FileOp'
    Version = $Version
    Architecture = 'x64'
    CreatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    TrustedSignerThumbprint = $thumbprint
    Files = @($manifestEntries)
}
$manifestPath = Join-Path $stageRoot 'fileop-release-manifest.json'
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

Compress-Archive -Path (Join-Path $stageRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
$zipHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash.ToLowerInvariant()
"$zipHash  $([IO.Path]::GetFileName($zipPath))" | Set-Content -LiteralPath $digestPath -Encoding ascii
Write-Host "PASS: signed FileOp release package created at $zipPath" -ForegroundColor Green
Write-Host "PASS: package SHA-256 for independently authenticated release metadata: $zipHash" -ForegroundColor Green
Write-Host "Digest file: $digestPath" -ForegroundColor Green
