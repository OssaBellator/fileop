param(
    [switch]$OfflineOnly,
    [string]$Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $repoRoot "tests\FileOp.Windows.Tests\FileOp.Windows.Tests.csproj"
$indexerProject = Join-Path $repoRoot "src\FileOp.Indexer\FileOp.Indexer.csproj"
$appProject = Join-Path $repoRoot "src\FileOp.App\FileOp.App.csproj"

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
foreach ($requiredProject in @($testProject, $indexerProject, $appProject)) {
    if (-not (Test-Path -LiteralPath $requiredProject -PathType Leaf)) {
        throw "Required #193 validation project was not found at '$requiredProject'."
    }
}

# The shared prefix deliberately selects all current #193 classes:
# - WindowsMutationFilesystemCapabilityBoundaryTests
# - WindowsMutationFilesystemCapabilityBindingTests
# - WindowsMutationFilesystemCapabilityPolicyTests
# - WindowsMutationFilesystemCapabilityPrimitiveGuardTests
& dotnet test $testProject `
    -c $Configuration `
    --filter "FullyQualifiedName~WindowsMutationFilesystemCapability" `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Mutation filesystem identity Windows tests failed with exit code $LASTEXITCODE."
}

# The product policy is wired by global aliases in FileOp.App, which the Windows test project
# does not compile. Build the same x64 Indexer/App pair used by the full local gate so a broken
# alias or missing App compile item is caught by this targeted gate rather than deferred to the
# complete test-local run.
& dotnet build $indexerProject `
    -c $Configuration `
    -p:Platform=x64 `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Mutation filesystem identity Indexer prerequisite build failed with exit code $LASTEXITCODE."
}

& dotnet build $appProject `
    -c $Configuration `
    -p:Platform=x64 `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Mutation filesystem identity App product-wiring build failed with exit code $LASTEXITCODE."
}

Write-Host "PASS mutation filesystem identity Windows validation" -ForegroundColor Green
