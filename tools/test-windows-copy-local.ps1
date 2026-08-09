param(
    [switch]$SkipOfflineModels
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Command
    )

    Write-Host "`n==> $Name" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE."
    }
}

if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
    throw "Python 3 is required for the zero-Actions Copy validation gates."
}

python -c "import sys; raise SystemExit(0 if sys.version_info.major == 3 else 1)"
if ($LASTEXITCODE -ne 0) {
    throw "The 'python' command must run Python 3 for the FileOp offline verifiers."
}

if (-not $SkipOfflineModels) {
    Invoke-Step "Copy executor and mutation property models" {
        & (Join-Path $PSScriptRoot "test-copy-executor-local.ps1")
    }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET 10 SDK is required for the focused Windows Copy compiler/native test gate."
}

Invoke-Step "Package-free .NET Copy metadata interop probe" {
    python tools/verify_copy_basic_metadata_dotnet_sdk.py --repo-root $repoRoot
}

Invoke-Step "FileOp.Core Release build" {
    dotnet build src/FileOp.Core/FileOp.Core.csproj --configuration Release
}

Invoke-Step "FileOp.Windows Release build" {
    dotnet build src/FileOp.Windows/FileOp.Windows.csproj --configuration Release
}

$filter = @(
    "FullyQualifiedName~FileContentFingerprintTests",
    "FullyQualifiedName~FileSecurityDescriptorEvidenceTests",
    "FullyQualifiedName~FileOperationActionHistoryTests",
    "FullyQualifiedName~FileOperationActionHistoryHardLinkEvidence",
    "FullyQualifiedName~FileOperationActionHistoryBasicMetadataEvidenceTests",
    "FullyQualifiedName~FileOperationActionHistorySecurityDescriptorEvidenceTests",
    "FullyQualifiedName~FileOperationRecoveryInspectionTests",
    "FullyQualifiedName~FileOperationRecoveryContentVerificationTests",
    "FullyQualifiedName~FileOperationRecoveryBasicMetadataComparerTests",
    "FullyQualifiedName~FileOperationRecoveryBasicMetadataVerificationTests",
    "FullyQualifiedName~FileOperationRecoverySecurityDescriptorVerificationTests",
    "FullyQualifiedName~FileOperationRecoveryEvidenceAssessmentTests",
    "FullyQualifiedName~FileCopyOperationExecutorTests",
    "FullyQualifiedName~WindowsFileContentFingerprintReaderTests",
    "FullyQualifiedName~WindowsRootBoundFileContentFingerprintReaderTests",
    "FullyQualifiedName~WindowsRootBoundFileHardLinkEvidenceSourceTests",
    "FullyQualifiedName~WindowsFileOperationActionHistoryHardLinkEvidenceStoreTests",
    "FullyQualifiedName~WindowsRootBoundFileCommitBasicMetadataEvidenceSourceTests",
    "FullyQualifiedName~WindowsRootBoundFileBasicMetadataEvidenceReaderTests",
    "FullyQualifiedName~WindowsFileOperationActionHistoryBasicMetadataEvidenceStoreTests",
    "FullyQualifiedName~WindowsRootBoundFileCommitSecurityDescriptorEvidenceSourceTests",
    "FullyQualifiedName~WindowsRootBoundFileSecurityDescriptorEvidenceReaderTests",
    "FullyQualifiedName~WindowsFileOperationActionHistorySecurityDescriptorEvidenceStoreTests",
    "FullyQualifiedName~WindowsFileOperationRecoveryContentVerificationTests",
    "FullyQualifiedName~WindowsFileCopyMutationPrimitiveTests",
    "FullyQualifiedName~WindowsFileCopyMutationPrimitiveMetadataTests",
    "FullyQualifiedName~WindowsFileCopyBasicMetadataInteropTests"
) -join "|"

Invoke-Step "Focused action-history/recovery/fingerprint/Copy native regressions" {
    dotnet test tests/FileOp.Windows.Tests/FileOp.Windows.Tests.csproj `
        --configuration Release `
        --filter $filter
}

Write-Host "`nPASS: focused Windows Copy compiler/native gate completed without GitHub Actions." -ForegroundColor Green