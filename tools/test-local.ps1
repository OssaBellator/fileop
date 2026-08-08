param(
    [switch]$SkipBenchmarks,
    [switch]$SkipWinUI
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

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK is required. Install the .NET 10 SDK before running this verifier."
}

if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
    throw "Python 3 is required for the offline FileOp verifiers."
}

Invoke-Step "Offline Storage UI/property verifier" {
    python tools/verify_storage_ui.py --repo-root $repoRoot
}

Invoke-Step "Offline Storage file-type verifier" {
    python tools/verify_storage_types.py --repo-root $repoRoot
}

Invoke-Step "FileOp.Core Release build" {
    dotnet build src/FileOp.Core/FileOp.Core.csproj --configuration Release
}

if (-not $SkipBenchmarks) {
    Invoke-Step "Benchmark harness Release build" {
        dotnet build benchmarks/FileOp.Benchmarks/FileOp.Benchmarks.csproj --configuration Release
    }
}

Invoke-Step "FileOp.Windows Release build" {
    dotnet build src/FileOp.Windows/FileOp.Windows.csproj --configuration Release
}

Invoke-Step "FileOp.Indexer x64 Release build" {
    dotnet build src/FileOp.Indexer/FileOp.Indexer.csproj --configuration Release -p:Platform=x64
}

$indexerPath = Join-Path $repoRoot "src/FileOp.Indexer/bin/x64/Release/net10.0-windows10.0.17763.0/FileOp.Indexer.exe"
if (-not (Test-Path -LiteralPath $indexerPath)) {
    throw "Indexer host was not produced at the expected path: $indexerPath"
}

$previousIndexerPath = $env:FILEOP_INDEXER_PATH
try {
    $env:FILEOP_INDEXER_PATH = $indexerPath
    Invoke-Step "Windows regression/integration tests" {
        dotnet test tests/FileOp.Windows.Tests/FileOp.Windows.Tests.csproj --configuration Release
    }
}
finally {
    $env:FILEOP_INDEXER_PATH = $previousIndexerPath
}

if (-not $SkipWinUI) {
    Invoke-Step "FileOp.App WinUI x64 Release build" {
        dotnet build src/FileOp.App/FileOp.App.csproj --configuration Release -p:Platform=x64
    }

    $appDirectory = Join-Path $repoRoot "src/FileOp.App/bin/x64/Release/net10.0-windows10.0.26100.0"
    $requiredArtifacts = @(
        "FileOp.Indexer.exe",
        "FileOp.Indexer.dll",
        "FileOp.Indexer.deps.json",
        "FileOp.Indexer.runtimeconfig.json"
    )

    Invoke-Step "Bundled indexer artifact check" {
        foreach ($name in $requiredArtifacts) {
            $path = Join-Path $appDirectory $name
            if (-not (Test-Path -LiteralPath $path)) {
                throw "Missing bundled indexing helper artifact: $path"
            }
        }
    }

    $previousIndexerPath = $env:FILEOP_INDEXER_PATH
    try {
        $env:FILEOP_INDEXER_PATH = Join-Path $appDirectory "FileOp.Indexer.exe"
        Invoke-Step "Bundled helper real-process handshake" {
            dotnet test tests/FileOp.Windows.Tests/FileOp.Windows.Tests.csproj `
                --configuration Release `
                --no-build `
                --filter IndexerProcessSessionCompletesRealHostHandshake
        }
    }
    finally {
        $env:FILEOP_INDEXER_PATH = $previousIndexerPath
    }
}

Write-Host "`nPASS: local FileOp verification completed without GitHub Actions." -ForegroundColor Green
