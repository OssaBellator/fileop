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

Write-Host "`nPASS: Copy executor orchestration verified without GitHub Actions." -ForegroundColor Green
