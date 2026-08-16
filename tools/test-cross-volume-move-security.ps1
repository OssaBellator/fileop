param(
    [string]$Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw "Cross-volume Move security-policy validation requires Windows."
}

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [System.Security.Principal.WindowsPrincipal]::new($identity)
if ($principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Cross-volume Move security-policy validation must run from an ordinary unelevated token. Close the elevated shell and rerun normally."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $repoRoot "tests\FileOp.Windows.Tests\FileOp.Windows.Tests.csproj"
if (-not (Test-Path -LiteralPath $testProject -PathType Leaf)) {
    throw "FileOp.Windows.Tests project was not found at '$testProject'."
}

Write-Host "Running ordinary-token cross-volume Move security-policy validation"
Write-Host "  Identity: $($identity.Name)"
Write-Host "  Elevated administrator role: false"
Write-Host "This gate exercises destination-default/inherited DACL behavior only; it does not request SACL or ACCESS_SYSTEM_SECURITY evidence."

& dotnet test $testProject `
    -c $Configuration `
    --filter "TestCategory=CrossVolumeMoveSecurityNative" `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Cross-volume Move ordinary-token security-policy validation failed with exit code $LASTEXITCODE."
}

Write-Host "PASS ordinary-token cross-volume Move security-policy validation"
