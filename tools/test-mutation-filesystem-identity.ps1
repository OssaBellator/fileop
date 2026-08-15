param(
    [switch]$OfflineOnly,
    [string]$Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $repoRoot "tests\FileOp.Windows.Tests\FileOp.Windows.Tests.csproj"

if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
    throw "Python 3 is required for the mutation filesystem identity verifier."
}

& python (Join-Path $repoRoot "tools\verify_mutation_filesystem_identity_boundary.py") `
    --repo-root $repoRoot `
    --cases 50000
if ($LASTEXITCODE -ne 0) {
    throw "Mutation filesystem identity source/model verification failed with exit code $LASTEXITCODE."
}

& python (Join-Path $repoRoot "tools\verify_mutation_filesystem_product_wiring.py") `
    --repo-root $repoRoot
if ($LASTEXITCODE -ne 0) {
    throw "Mutation filesystem identity product-wiring verification failed with exit code $LASTEXITCODE."
}

if ($OfflineOnly) {
    Write-Host "PASS mutation filesystem identity offline verification" -ForegroundColor Green
    return
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw "Compiled mutation filesystem identity validation requires Windows. Rerun with -OfflineOnly for portable source/model checks."
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET 10 SDK is required for compiled mutation filesystem identity validation."
}
if (-not (Test-Path -LiteralPath $testProject -PathType Leaf)) {
    throw "FileOp.Windows.Tests project was not found at '$testProject'."
}

# The shared prefix deliberately selects both:
# - WindowsMutationFilesystemCapabilityBoundaryTests
# - WindowsMutationFilesystemCapabilityBindingTests
& dotnet test $testProject `
    -c $Configuration `
    --filter "FullyQualifiedName~WindowsMutationFilesystemCapability" `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Mutation filesystem identity Windows tests failed with exit code $LASTEXITCODE."
}

Write-Host "PASS mutation filesystem identity Windows validation" -ForegroundColor Green
