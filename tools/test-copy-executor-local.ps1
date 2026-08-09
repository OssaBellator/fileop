$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
    throw "Python 3 is required for the FileOp offline verifiers."
}

python -c "import sys; raise SystemExit(0 if sys.version_info.major == 3 else 1)"
if ($LASTEXITCODE -ne 0) {
    throw "The 'python' command must run Python 3 for the FileOp offline verifiers."
}

& (Join-Path $PSScriptRoot "test-local.ps1") -OfflineOnly
if ($LASTEXITCODE -ne 0) {
    throw "Existing offline FileOp verification failed with exit code $LASTEXITCODE."
}

python tools/verify_file_operation_recovery_inspection.py --repo-root $repoRoot --cases 50000
if ($LASTEXITCODE -ne 0) {
    throw "Read-only Copy recovery inspection verification failed with exit code $LASTEXITCODE."
}

python tools/verify_copy_content_fingerprint.py --repo-root $repoRoot --cases 20000
if ($LASTEXITCODE -ne 0) {
    throw "Copy content fingerprint evidence verification failed with exit code $LASTEXITCODE."
}

python tools/verify_recovery_main_stream.py --repo-root $repoRoot --cases 50000
if ($LASTEXITCODE -ne 0) {
    throw "Recovery main-stream verification failed with exit code $LASTEXITCODE."
}

python tools/verify_recovery_root_bound_reader_abi.py --repo-root $repoRoot
if ($LASTEXITCODE -ne 0) {
    throw "Recovery root-bound reader ABI verification failed with exit code $LASTEXITCODE."
}

python tools/verify_recovery_root_identity.py --repo-root $repoRoot --cases 50000
if ($LASTEXITCODE -ne 0) {
    throw "Recovery destination-root identity verification failed with exit code $LASTEXITCODE."
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

python tools/verify_copy_basic_metadata_windows_semantics.py --cases 100000
if ($LASTEXITCODE -ne 0) {
    throw "Windows FileBasicInformation semantics verification failed with exit code $LASTEXITCODE."
}

python tools/verify_copy_basic_metadata_abi.py --repo-root $repoRoot
if ($LASTEXITCODE -ne 0) {
    throw "Copy basic metadata ABI verification failed with exit code $LASTEXITCODE."
}

Write-Host "`nPASS: Copy executor, recovery inspection, content fingerprint evidence, root-bound recovery main-stream verification, root-bound reader ABI, destination-root identity evidence, Windows mutation handle binding, basic metadata, Windows FileBasicInformation semantics, and interop ABI boundaries verified without GitHub Actions." -ForegroundColor Green
