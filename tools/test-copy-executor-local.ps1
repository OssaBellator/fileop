$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
    throw "Python 3 is required for the FileOp offline verifiers."
}

python tools/test-copy-executor-local.py --repo-root $repoRoot
if ($LASTEXITCODE -ne 0) {
    throw "Offline File Copy verification failed with exit code $LASTEXITCODE."
}

Write-Host "`nPASS: Copy executor, Windows mutation handle binding, and basic metadata boundary verified without GitHub Actions." -ForegroundColor Green
