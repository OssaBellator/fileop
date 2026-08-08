$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
    throw "Python 3 is required for the FileOp offline verifiers."
}

& (Join-Path $PSScriptRoot "test-local.ps1") -OfflineOnly
if ($LASTEXITCODE -ne 0) {
    throw "Existing offline FileOp verification failed with exit code $LASTEXITCODE."
}

python tools/verify_file_copy_executor.py --repo-root $repoRoot --cases 20000
if ($LASTEXITCODE -ne 0) {
    throw "File Copy executor verification failed with exit code $LASTEXITCODE."
}

python tools/verify_windows_file_copy_mutation.py --repo-root $repoRoot --cases 2000
if ($LASTEXITCODE -ne 0) {
    throw "Windows Copy mutation handle-binding verification failed with exit code $LASTEXITCODE."
}

python tools/verify_copy_basic_metadata.py --repo-root $repoRoot --cases 50000
if ($LASTEXITCODE -ne 0) {
    throw "Copy basic metadata verification failed with exit code $LASTEXITCODE."
}

Write-Host "`nPASS: Copy executor, Windows mutation handle binding, and basic metadata boundary verified without GitHub Actions." -ForegroundColor Green
